using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Evaluates a loaded MSBuild project's source-file include/exclude rules (default compile globs,
/// excluded output folders like <c>bin/</c> and <c>obj/</c>, <c>DefaultItemExcludes</c>, and explicit
/// <c>&lt;Compile Remove/Include&gt;</c> items) so the hot workspace can add new source files to the right
/// project and ignore excluded or non-source files without invoking MSBuild.
/// </summary>
internal sealed class ProjectSourceRules
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly HashSet<string> DefaultExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        ".vs",
        ".roslynskills",
        "TestResults",
        "node_modules",
    };

    private readonly IReadOnlyList<GlobMatcher> _defaultExcludes;
    private readonly IReadOnlyList<CompileItemRule> _compileRules;
    private readonly IReadOnlySet<string> _excludedTopLevelDirectories;
    private readonly bool _hasExternalIncludeRules;

    private ProjectSourceRules(
        string projectFilePath,
        string projectDirectory,
        string language,
        string defaultExtension,
        bool enableDefaultCompileItems,
        IReadOnlyList<GlobMatcher> defaultExcludes,
        IReadOnlyList<CompileItemRule> compileRules,
        IReadOnlySet<string> excludedTopLevelDirectories)
    {
        ProjectFilePath = projectFilePath;
        ProjectDirectory = projectDirectory;
        Language = language;
        DefaultExtension = defaultExtension;
        EnableDefaultCompileItems = enableDefaultCompileItems;
        _defaultExcludes = defaultExcludes;
        _compileRules = compileRules;
        _excludedTopLevelDirectories = excludedTopLevelDirectories;
        _hasExternalIncludeRules = compileRules.Any(rule =>
            rule.IsInclude && rule.Patterns.Any(pattern => pattern.RawPattern.StartsWith("../", StringComparison.Ordinal)));
    }

    public string ProjectFilePath { get; }
    public string ProjectDirectory { get; }
    public string Language { get; }
    public string DefaultExtension { get; }
    public bool EnableDefaultCompileItems { get; }

    public static IReadOnlyList<ProjectSourceRules> FromSolution(Solution solution, string? workspaceRootDirectory = null)
    {
        string? normalizedRoot = string.IsNullOrWhiteSpace(workspaceRootDirectory)
            ? null
            : TrimTrailingSeparators(Path.GetFullPath(workspaceRootDirectory));

        Dictionary<string, ProjectSourceRules> byProjectFile = new(PathComparer);
        foreach (Project project in solution.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath))
            {
                continue;
            }

            string normalizedProjectFile = Path.GetFullPath(project.FilePath);
            if (byProjectFile.ContainsKey(normalizedProjectFile))
            {
                continue;
            }

            ProjectSourceRules? rules = LoadForProject(normalizedProjectFile, project.Language, normalizedRoot);
            if (rules is not null)
            {
                byProjectFile[normalizedProjectFile] = rules;
            }
        }

        return byProjectFile.Values
            .OrderBy(r => r.ProjectFilePath, PathComparer)
            .ToArray();
    }

    public static ProjectSourceRules? LoadForProject(string projectFilePath, string language, string? workspaceRootDirectory = null)
    {
        string normalizedProjectFile = Path.GetFullPath(projectFilePath);
        string? projectDir = Path.GetDirectoryName(normalizedProjectFile);
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return null;
        }

        string normalizedProjectDir = TrimTrailingSeparators(projectDir);
        string defaultExtension = string.Equals(language, LanguageNames.VisualBasic, StringComparison.Ordinal)
            ? ".vb"
            : ".cs";

        bool isSdkProject = true;
        bool? enableDefaultItems = null;
        bool? enableDefaultCompileItems = null;
        List<GlobMatcher> defaultExcludes = new();
        List<CompileItemRule> compileRules = new();
        HashSet<string> excludedTopLevelDirs = new(StringComparer.OrdinalIgnoreCase);

        List<string> filesToParse = new();
        string? propsPath = FindNearestDirectoryBuildFile(normalizedProjectDir, "Directory.Build.props", workspaceRootDirectory);
        if (!string.IsNullOrWhiteSpace(propsPath))
        {
            filesToParse.Add(propsPath);
        }

        if (File.Exists(normalizedProjectFile))
        {
            filesToParse.Add(normalizedProjectFile);
        }

        string? targetsPath = FindNearestDirectoryBuildFile(normalizedProjectDir, "Directory.Build.targets", workspaceRootDirectory);
        if (!string.IsNullOrWhiteSpace(targetsPath))
        {
            filesToParse.Add(targetsPath);
        }

        foreach (string xmlPath in filesToParse)
        {
            bool isMainProjectFile = PathComparer.Equals(xmlPath, normalizedProjectFile);
            ParseMsBuildFile(
                xmlPath,
                isMainProjectFile,
                ref isSdkProject,
                ref enableDefaultItems,
                ref enableDefaultCompileItems,
                defaultExcludes,
                compileRules,
                excludedTopLevelDirs);
        }

        bool effectiveDefaultCompile = (enableDefaultItems ?? isSdkProject) &&
                                       (enableDefaultCompileItems ?? isSdkProject);

        return new ProjectSourceRules(
            normalizedProjectFile,
            normalizedProjectDir,
            language,
            defaultExtension,
            effectiveDefaultCompile,
            defaultExcludes,
            compileRules,
            excludedTopLevelDirs);
    }

    public bool IsUnderProjectDirectory(string fullFilePath)
    {
        string normalized = Path.GetFullPath(fullFilePath);
        return normalized.Length > ProjectDirectory.Length &&
               normalized.StartsWith(ProjectDirectory, PathComparison) &&
               (normalized[ProjectDirectory.Length] == Path.DirectorySeparatorChar ||
                normalized[ProjectDirectory.Length] == Path.AltDirectorySeparatorChar);
    }

    public bool IncludesSourceFile(string fullFilePath, bool includeGenerated)
    {
        string normalized = Path.GetFullPath(fullFilePath);
        if (!includeGenerated && CommandFileFilters.IsGeneratedPath(normalized))
        {
            return false;
        }

        bool underProject = IsUnderProjectDirectory(normalized);
        if (!underProject && !_hasExternalIncludeRules)
        {
            return false;
        }

        string relativePath = NormalizeRelativePath(Path.GetRelativePath(ProjectDirectory, normalized));
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
        {
            return false;
        }

        bool included = false;
        if (underProject && EnableDefaultCompileItems)
        {
            string extension = Path.GetExtension(normalized);
            if (string.Equals(extension, DefaultExtension, StringComparison.OrdinalIgnoreCase) &&
                !HasExcludedDirectorySegment(relativePath, _excludedTopLevelDirectories) &&
                !MatchesAny(relativePath, _defaultExcludes))
            {
                included = true;
            }
        }

        foreach (CompileItemRule rule in _compileRules)
        {
            if (rule.IsInclude)
            {
                if (MatchesAny(relativePath, rule.Patterns) && !MatchesAny(relativePath, rule.Excludes))
                {
                    included = true;
                }
            }
            else
            {
                if (MatchesAny(relativePath, rule.Patterns))
                {
                    included = false;
                }
            }
        }

        return included;
    }

    public static IReadOnlyList<Project> FindMatchingProjects(
        IReadOnlyList<ProjectSourceRules> rules,
        Solution solution,
        string filePath,
        bool includeGenerated)
    {
        string normalizedFilePath = Path.GetFullPath(filePath);
        if (!CommandLanguageServices.IsSupportedSourceFile(normalizedFilePath))
        {
            return Array.Empty<Project>();
        }

        if (!includeGenerated && CommandFileFilters.IsGeneratedPath(normalizedFilePath))
        {
            return Array.Empty<Project>();
        }

        IReadOnlyList<ProjectSourceRules> matchingRules = FindMatchingRules(rules, normalizedFilePath, includeGenerated);
        if (matchingRules.Count == 0)
        {
            return Array.Empty<Project>();
        }

        HashSet<string> matchingProjectFiles = new(
            matchingRules.Select(r => r.ProjectFilePath),
            PathComparer);

        return solution.Projects
            .Where(project => !string.IsNullOrWhiteSpace(project.FilePath) &&
                              matchingProjectFiles.Contains(Path.GetFullPath(project.FilePath)))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool MatchesAnyProject(
        IReadOnlyList<ProjectSourceRules> rules,
        string filePath,
        bool includeGenerated)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        string normalizedFilePath = Path.GetFullPath(filePath);
        if (!CommandLanguageServices.IsSupportedSourceFile(normalizedFilePath))
        {
            return false;
        }

        if (!includeGenerated && CommandFileFilters.IsGeneratedPath(normalizedFilePath))
        {
            return false;
        }

        return FindMatchingRules(rules, normalizedFilePath, includeGenerated).Count > 0;
    }

    public static IEnumerable<string> EnumerateCandidateSourceFiles(
        IReadOnlyList<ProjectSourceRules> rules,
        bool includeGenerated)
    {
        if (rules.Count == 0)
        {
            yield break;
        }

        HashSet<string> allowedExtensions = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectSourceRules rule in rules)
        {
            allowedExtensions.Add(rule.DefaultExtension);
        }

        HashSet<string> visitedDirectories = new(PathComparer);
        HashSet<string> yieldedFiles = new(PathComparer);
        Stack<string> pendingDirectories = new();

        foreach (string projectDir in rules
                     .Select(r => r.ProjectDirectory)
                     .Distinct(PathComparer)
                     .OrderBy(d => d, PathComparer))
        {
            if (Directory.Exists(projectDir))
            {
                pendingDirectories.Push(projectDir);
            }
        }

        while (pendingDirectories.Count > 0)
        {
            string currentDir = pendingDirectories.Pop();
            if (!visitedDirectories.Add(currentDir))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                if (!allowedExtensions.Contains(extension))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(file);
                if (!yieldedFiles.Add(fullPath))
                {
                    continue;
                }

                if (MatchesAnyProject(rules, fullPath, includeGenerated))
                {
                    yield return fullPath;
                }
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string subDir in subDirs)
            {
                string dirName = Path.GetFileName(subDir);
                if (DefaultExcludedDirectoryNames.Contains(dirName))
                {
                    continue;
                }

                pendingDirectories.Push(subDir);
            }
        }
    }

    public static bool IsIgnoredDirectorySegment(string segment)
        => DefaultExcludedDirectoryNames.Contains(segment);

    private static IReadOnlyList<ProjectSourceRules> FindMatchingRules(
        IReadOnlyList<ProjectSourceRules> rules,
        string normalizedFilePath,
        bool includeGenerated)
    {
        string language = CommandLanguageServices.DetectLanguageFromFilePath(normalizedFilePath);

        List<ProjectSourceRules> containingByDir = new();
        List<ProjectSourceRules> externalIncludes = new();

        foreach (ProjectSourceRules rule in rules)
        {
            if (!string.Equals(rule.Language, language, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.IsUnderProjectDirectory(normalizedFilePath))
            {
                containingByDir.Add(rule);
            }
            else if (rule._hasExternalIncludeRules && rule.IncludesSourceFile(normalizedFilePath, includeGenerated))
            {
                externalIncludes.Add(rule);
            }
        }

        List<ProjectSourceRules> matched = new();
        if (containingByDir.Count > 0)
        {
            // Deepest containing project directory owns files inside its subtree (including exclusions).
            int deepestLength = containingByDir.Max(r => r.ProjectDirectory.Length);
            foreach (ProjectSourceRules candidate in containingByDir)
            {
                if (candidate.ProjectDirectory.Length == deepestLength &&
                    candidate.IncludesSourceFile(normalizedFilePath, includeGenerated))
                {
                    matched.Add(candidate);
                }
            }
        }

        foreach (ProjectSourceRules external in externalIncludes)
        {
            if (!matched.Contains(external))
            {
                matched.Add(external);
            }
        }

        return matched;
    }

    private static string? FindNearestDirectoryBuildFile(
        string startDirectory,
        string fileName,
        string? stopDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            if (!string.IsNullOrWhiteSpace(stopDirectory) &&
                PathComparer.Equals(TrimTrailingSeparators(current.FullName), stopDirectory))
            {
                break;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void ParseMsBuildFile(
        string xmlPath,
        bool isMainProjectFile,
        ref bool isSdkProject,
        ref bool? enableDefaultItems,
        ref bool? enableDefaultCompileItems,
        List<GlobMatcher> defaultExcludes,
        List<CompileItemRule> compileRules,
        HashSet<string> excludedTopLevelDirs)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(xmlPath);
        }
        catch
        {
            return;
        }

        XElement? root = doc.Root;
        if (root is null)
        {
            return;
        }

        if (isMainProjectFile)
        {
            isSdkProject = !string.IsNullOrWhiteSpace(root.Attribute("Sdk")?.Value) ||
                           root.Elements().Any(el =>
                               string.Equals(el.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase) ||
                               (string.Equals(el.Name.LocalName, "Import", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(el.Attribute("Sdk")?.Value)));
        }

        foreach (XElement group in root.Elements())
        {
            if (HasCondition(group))
            {
                continue;
            }

            if (string.Equals(group.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase))
            {
                foreach (XElement prop in group.Elements())
                {
                    if (HasCondition(prop))
                    {
                        continue;
                    }

                    string name = prop.Name.LocalName;
                    string value = prop.Value.Trim();
                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    if (string.Equals(name, "EnableDefaultItems", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out bool parsedDefaultItems))
                    {
                        enableDefaultItems = parsedDefaultItems;
                    }
                    else if (string.Equals(name, "EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase) &&
                             bool.TryParse(value, out bool parsedDefaultCompile))
                    {
                        enableDefaultCompileItems = parsedDefaultCompile;
                    }
                    else if (string.Equals(name, "DefaultItemExcludes", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(name, "DefaultExcludesInProjectFolder", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string token in SplitMsBuildList(value))
                        {
                            if (GlobMatcher.TryCreate(token, out GlobMatcher? matcher) && matcher is not null)
                            {
                                defaultExcludes.Add(matcher);
                            }
                        }
                    }
                    else if (string.Equals(name, "BaseOutputPath", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(name, "OutputPath", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(name, "BaseIntermediateOutputPath", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(name, "IntermediateOutputPath", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!value.Contains("$(", StringComparison.Ordinal))
                        {
                            string normalizedDir = NormalizeRelativePath(value).TrimEnd('/');
                            if (!string.IsNullOrWhiteSpace(normalizedDir) &&
                                !normalizedDir.StartsWith("../", StringComparison.Ordinal) &&
                                !Path.IsPathRooted(value))
                            {
                                string firstSegment = normalizedDir.Split('/')[0];
                                if (!string.IsNullOrWhiteSpace(firstSegment))
                                {
                                    excludedTopLevelDirs.Add(firstSegment);
                                }

                                if (GlobMatcher.TryCreate(normalizedDir + "/**", out GlobMatcher? matcher) && matcher is not null)
                                {
                                    defaultExcludes.Add(matcher);
                                }
                            }
                        }
                    }
                }
            }
            else if (string.Equals(group.Name.LocalName, "ItemGroup", StringComparison.OrdinalIgnoreCase))
            {
                foreach (XElement item in group.Elements())
                {
                    if (HasCondition(item) ||
                        !string.Equals(item.Name.LocalName, "Compile", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? removeAttr = item.Attribute("Remove")?.Value;
                    if (!string.IsNullOrWhiteSpace(removeAttr))
                    {
                        GlobMatcher[] patterns = ParseGlobList(removeAttr);
                        if (patterns.Length > 0)
                        {
                            compileRules.Add(new CompileItemRule(IsInclude: false, patterns, Array.Empty<GlobMatcher>()));
                        }
                    }

                    string? includeAttr = item.Attribute("Include")?.Value;
                    if (!string.IsNullOrWhiteSpace(includeAttr))
                    {
                        GlobMatcher[] patterns = ParseGlobList(includeAttr);
                        if (patterns.Length > 0)
                        {
                            string? excludeAttr = item.Attribute("Exclude")?.Value;
                            GlobMatcher[] excludes = string.IsNullOrWhiteSpace(excludeAttr)
                                ? Array.Empty<GlobMatcher>()
                                : ParseGlobList(excludeAttr);
                            compileRules.Add(new CompileItemRule(IsInclude: true, patterns, excludes));
                        }
                    }
                }
            }
        }
    }

    private static bool HasCondition(XElement element)
        => !string.IsNullOrWhiteSpace(element.Attribute("Condition")?.Value);

    private static IEnumerable<string> SplitMsBuildList(string raw)
    {
        foreach (string part in raw.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            yield return trimmed;
        }
    }

    private static GlobMatcher[] ParseGlobList(string raw)
    {
        List<GlobMatcher> matchers = new();
        foreach (string token in SplitMsBuildList(raw))
        {
            if (GlobMatcher.TryCreate(token, out GlobMatcher? matcher) && matcher is not null)
            {
                matchers.Add(matcher);
            }
        }

        return matchers.ToArray();
    }

    private static bool HasExcludedDirectorySegment(string relativePath, IReadOnlySet<string> excludedTopLevelDirs)
    {
        string[] segments = relativePath.Split('/');
        // Only directory segments (all segments except the final file name).
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (DefaultExcludedDirectoryNames.Contains(segments[i]))
            {
                return true;
            }

            if (i == 0 && excludedTopLevelDirs.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAny(string relativePath, IReadOnlyList<GlobMatcher> matchers)
    {
        for (int i = 0; i < matchers.Count; i++)
        {
            if (matchers[i].IsMatch(relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string TrimTrailingSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed record CompileItemRule(
        bool IsInclude,
        IReadOnlyList<GlobMatcher> Patterns,
        IReadOnlyList<GlobMatcher> Excludes);

    private sealed class GlobMatcher
    {
        private readonly Regex _regex;

        private GlobMatcher(string rawPattern, Regex regex)
        {
            RawPattern = rawPattern;
            _regex = regex;
        }

        public string RawPattern { get; }

        public bool IsMatch(string normalizedRelativePath)
            => _regex.IsMatch(normalizedRelativePath);

        public static bool TryCreate(string pattern, out GlobMatcher? matcher)
        {
            string normalized = NormalizeRelativePath(pattern.Trim());
            if (string.IsNullOrEmpty(normalized))
            {
                matcher = null;
                return false;
            }

            // Treat a bare trailing slash like "Legacy/" as matching everything under that directory.
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "**";
            }

            RegexOptions options = OperatingSystem.IsWindows()
                ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
                : RegexOptions.CultureInvariant | RegexOptions.Compiled;

            StringBuilder sb = new("^");
            int i = 0;
            while (i < normalized.Length)
            {
                if (i + 2 < normalized.Length &&
                    normalized[i] == '*' &&
                    normalized[i + 1] == '*' &&
                    normalized[i + 2] == '/')
                {
                    sb.Append("(?:.+/)?");
                    i += 3;
                    continue;
                }

                if (i + 2 == normalized.Length - 1 &&
                    normalized[i] == '/' &&
                    normalized[i + 1] == '*' &&
                    normalized[i + 2] == '*')
                {
                    sb.Append("(?:/.*)?");
                    i += 3;
                    continue;
                }

                if (i + 1 < normalized.Length &&
                    normalized[i] == '*' &&
                    normalized[i + 1] == '*')
                {
                    sb.Append(".*");
                    i += 2;
                    continue;
                }

                char c = normalized[i];
                if (c == '*')
                {
                    sb.Append("[^/]*");
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }

                i++;
            }

            sb.Append('$');
            matcher = new GlobMatcher(normalized, new Regex(sb.ToString(), options));
            return true;
        }
    }
}
