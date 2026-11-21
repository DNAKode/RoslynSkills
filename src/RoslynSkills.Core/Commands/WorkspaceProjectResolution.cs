using Microsoft.CodeAnalysis;
using System.Xml.Linq;

namespace RoslynSkills.Core.Commands;

internal sealed record UnresolvedProjectInfo(
    string project_name,
    string? project_path,
    IReadOnlyList<string> reasons,
    bool needs_restore);

/// <summary>
/// Inspects a loaded MSBuild <see cref="Solution"/> to identify projects whose references
/// did not resolve cleanly (zero metadata references, missing NuGet restore assets for
/// PackageReference/PackageDownload projects, unresolved ProjectReferences, or broken
/// core runtime types).
/// </summary>
internal static class WorkspaceProjectResolution
{
    public const string UnresolvedProjectDiagnosticPrefix = "unresolved_project:";
    private const int MaxOtherWorkspaceDiagnostics = 30;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static IReadOnlyList<UnresolvedProjectInfo> InspectSolution(
        Solution solution,
        IReadOnlyDictionary<ProjectId, Compilation>? compilationsByProject = null)
    {
        Project[] projects = solution.Projects
            .OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (projects.Length == 0)
        {
            return Array.Empty<UnresolvedProjectInfo>();
        }

        Dictionary<string, ProjectXmlInfo> xmlInfoByPath = new(PathComparer);
        Dictionary<string, bool> ancestorPackageRequirementCache = new(PathComparer);
        Dictionary<ProjectId, bool> directPackageRequirement = new();

        foreach (Project project in projects)
        {
            ProjectXmlInfo xmlInfo = GetProjectXmlInfo(project.FilePath, xmlInfoByPath);
            bool requiresPackages = xmlInfo.HasPackageReferences ||
                HasUnconditionalAncestorPackageReferences(project.FilePath, ancestorPackageRequirementCache);
            directPackageRequirement[project.Id] = requiresPackages;
        }

        // Propagate package-restore requirement transitively across project references:
        // if B references A and A has PackageReferences, B's project.assets.json is also
        // needed for transitive NuGet references to flow into B.
        Dictionary<ProjectId, bool> effectivePackageRequirement = new(directPackageRequirement);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Project project in projects)
            {
                if (effectivePackageRequirement[project.Id])
                {
                    continue;
                }

                foreach (ProjectReference projectReference in project.ProjectReferences)
                {
                    if (effectivePackageRequirement.TryGetValue(projectReference.ProjectId, out bool referencedNeedsPackages) &&
                        referencedNeedsPackages)
                    {
                        effectivePackageRequirement[project.Id] = true;
                        changed = true;
                        break;
                    }
                }
            }
        }

        List<UnresolvedProjectInfo> unresolved = new();
        foreach (Project project in projects)
        {
            List<string> reasons = new();
            bool needsRestore = false;

            bool hasDocuments = project.Documents.Any();
            if (hasDocuments && project.MetadataReferences.Count == 0)
            {
                needsRestore = true;
                reasons.Add("zero metadata references (project was not restored or MSBuild evaluation failed)");
            }

            if (effectivePackageRequirement[project.Id] && !HasRestoreAssetsFile(project, GetProjectXmlInfo(project.FilePath, xmlInfoByPath)))
            {
                needsRestore = true;
                reasons.Add("missing NuGet restore assets (project.assets.json); run 'dotnet restore'");
            }

            ProjectXmlInfo xmlInfo = GetProjectXmlInfo(project.FilePath, xmlInfoByPath);
            if (xmlInfo.UnconditionalProjectReferences.Count > 0)
            {
                HashSet<string> resolvedProjectRefPaths = project.ProjectReferences
                    .Select(pr => solution.GetProject(pr.ProjectId)?.FilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => NormalizePath(path!))
                    .ToHashSet(PathComparer);

                HashSet<string> metadataRefAssemblyNames = project.MetadataReferences
                    .OfType<PortableExecutableReference>()
                    .Select(reference => reference.FilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => Path.GetFileNameWithoutExtension(path!))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (DeclaredProjectReference declared in xmlInfo.UnconditionalProjectReferences)
                {
                    if (resolvedProjectRefPaths.Contains(declared.ResolvedPath))
                    {
                        continue;
                    }

                    string expectedAssemblyName = Path.GetFileNameWithoutExtension(declared.ResolvedPath);
                    if (!string.IsNullOrWhiteSpace(expectedAssemblyName) &&
                        metadataRefAssemblyNames.Contains(expectedAssemblyName))
                    {
                        continue;
                    }

                    reasons.Add($"unresolved project reference '{declared.RawInclude}'");
                }
            }

            if (compilationsByProject is not null &&
                compilationsByProject.TryGetValue(project.Id, out Compilation? compilation) &&
                HasUnresolvedCoreRuntimeTypes(compilation))
            {
                reasons.Add("core runtime types failed to resolve (System.Object is error type)");
            }

            if (reasons.Count > 0)
            {
                unresolved.Add(new UnresolvedProjectInfo(
                    project_name: project.Name,
                    project_path: project.FilePath,
                    reasons: reasons.ToArray(),
                    needs_restore: needsRestore));
            }
        }

        return unresolved;
    }

    public static bool HasUnresolvedCoreRuntimeTypes(Compilation compilation)
    {
        INamedTypeSymbol objectType = compilation.GetSpecialType(SpecialType.System_Object);
        return objectType.TypeKind == TypeKind.Error;
    }

    public static string FormatDiagnostic(UnresolvedProjectInfo info)
    {
        string location = string.IsNullOrWhiteSpace(info.project_path) ? "unknown" : info.project_path;
        return $"{UnresolvedProjectDiagnosticPrefix} {info.project_name} ({location}): {string.Join("; ", info.reasons)}";
    }

    /// <summary>
    /// Combines auto-restore, unresolved-project, and MSBuild diagnostics.
    /// Auto-restore and unresolved-project entries are never truncated so every
    /// unresolved project is reported even in large solutions.
    /// </summary>
    public static IReadOnlyList<string> MergeDiagnostics(
        IEnumerable<string> rawDiagnostics,
        IReadOnlyList<UnresolvedProjectInfo> unresolvedProjects)
    {
        List<string> autoRestore = new();
        List<string> other = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string diagnostic in rawDiagnostics)
        {
            if (string.IsNullOrWhiteSpace(diagnostic) ||
                diagnostic.StartsWith(UnresolvedProjectDiagnosticPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!seen.Add(diagnostic))
            {
                continue;
            }

            if (diagnostic.StartsWith("auto_restore", StringComparison.Ordinal))
            {
                autoRestore.Add(diagnostic);
            }
            else if (other.Count < MaxOtherWorkspaceDiagnostics)
            {
                other.Add(diagnostic);
            }
        }

        List<string> merged = new(autoRestore.Count + unresolvedProjects.Count + other.Count);
        merged.AddRange(autoRestore);

        foreach (UnresolvedProjectInfo info in unresolvedProjects)
        {
            string formatted = FormatDiagnostic(info);
            if (seen.Add(formatted))
            {
                merged.Add(formatted);
            }
        }

        merged.AddRange(other);
        return merged.ToArray();
    }

    public static UnresolvedProjectInfo? FindForProject(
        IReadOnlyList<UnresolvedProjectInfo> unresolvedProjects,
        Project? project)
    {
        if (project is null || unresolvedProjects.Count == 0)
        {
            return null;
        }

        string? normalizedProjectPath = string.IsNullOrWhiteSpace(project.FilePath)
            ? null
            : NormalizePath(project.FilePath);

        foreach (UnresolvedProjectInfo info in unresolvedProjects)
        {
            if (!string.IsNullOrWhiteSpace(normalizedProjectPath) &&
                !string.IsNullOrWhiteSpace(info.project_path) &&
                PathComparer.Equals(NormalizePath(info.project_path), normalizedProjectPath))
            {
                return info;
            }
        }

        foreach (UnresolvedProjectInfo info in unresolvedProjects)
        {
            if (string.Equals(info.project_name, project.Name, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ResolveProjectIssues(
        IReadOnlyList<UnresolvedProjectInfo> unresolvedProjects,
        Project? project,
        Compilation compilation)
    {
        List<string> issues = new();
        UnresolvedProjectInfo? match = FindForProject(unresolvedProjects, project);
        if (match is not null)
        {
            issues.AddRange(match.reasons);
        }

        if (HasUnresolvedCoreRuntimeTypes(compilation))
        {
            const string coreRuntimeIssue = "core runtime types failed to resolve (System.Object is error type)";
            if (!issues.Contains(coreRuntimeIssue, StringComparer.Ordinal))
            {
                issues.Add(coreRuntimeIssue);
            }
        }

        return issues;
    }

    private static bool HasRestoreAssetsFile(Project project, ProjectXmlInfo xmlInfo)
    {
        string? projectDirectory = string.IsNullOrWhiteSpace(project.FilePath)
            ? null
            : Path.GetDirectoryName(project.FilePath);

        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            if (File.Exists(Path.Combine(projectDirectory, "obj", "project.assets.json")))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(xmlInfo.BaseIntermediateOutputPath) &&
                !xmlInfo.BaseIntermediateOutputPath.Contains("$(", StringComparison.Ordinal))
            {
                try
                {
                    string customObjDir = Path.GetFullPath(Path.Combine(projectDirectory, xmlInfo.BaseIntermediateOutputPath));
                    if (File.Exists(Path.Combine(customObjDir, "project.assets.json")))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore malformed custom paths.
                }
            }
        }

        // Check generated documents (e.g., AssemblyInfo.cs / GlobalUsings.g.cs under obj/ or UseArtifactsOutput).
        int checkedGeneratedDocs = 0;
        foreach (Document document in project.Documents)
        {
            string? docPath = document.FilePath;
            if (string.IsNullOrWhiteSpace(docPath))
            {
                continue;
            }

            bool looksIntermediate = docPath.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                docPath.IndexOf($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!looksIntermediate)
            {
                continue;
            }

            if (HasAssetsFileInAncestorChain(Path.GetDirectoryName(docPath), maxDepth: 4))
            {
                return true;
            }

            checkedGeneratedDocs++;
            if (checkedGeneratedDocs >= 4)
            {
                break;
            }
        }

        // Check sibling obj/ directory for artifacts output layouts (e.g. artifacts/bin/<Proj>/... -> artifacts/obj/<Proj>/project.assets.json).
        if (!string.IsNullOrWhiteSpace(project.OutputFilePath))
        {
            string? outputDir = Path.GetDirectoryName(project.OutputFilePath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                string binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
                int binIndex = outputDir.LastIndexOf(binSegment, StringComparison.OrdinalIgnoreCase);
                if (binIndex >= 0)
                {
                    string candidateObjDir = string.Concat(
                        outputDir.AsSpan(0, binIndex),
                        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        outputDir.AsSpan(binIndex + binSegment.Length));
                    if (HasAssetsFileInAncestorChain(candidateObjDir, maxDepth: 4))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasAssetsFileInAncestorChain(string? directoryPath, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(directoryPath);
        }
        catch
        {
            return false;
        }

        for (int depth = 0; depth < maxDepth && current is not null; depth++)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.assets.json")))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static ProjectXmlInfo GetProjectXmlInfo(
        string? projectFilePath,
        Dictionary<string, ProjectXmlInfo> cache)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            return ProjectXmlInfo.Empty;
        }

        string normalizedPath = NormalizePath(projectFilePath);
        if (cache.TryGetValue(normalizedPath, out ProjectXmlInfo? cached))
        {
            return cached;
        }

        ProjectXmlInfo parsed = ParseProjectXml(normalizedPath);
        cache[normalizedPath] = parsed;
        return parsed;
    }

    private static ProjectXmlInfo ParseProjectXml(string normalizedProjectPath)
    {
        if (!File.Exists(normalizedProjectPath))
        {
            return ProjectXmlInfo.Empty;
        }

        try
        {
            XDocument doc = XDocument.Load(normalizedProjectPath);
            string projectDirectory = Path.GetDirectoryName(normalizedProjectPath) ?? Environment.CurrentDirectory;

            bool hasPackageReferences = doc.Descendants().Any(element =>
                (element.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) ||
                 element.Name.LocalName.Equals("PackageDownload", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace((string?)element.Attribute("Include")));

            string? baseIntermediateOutputPath = doc.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("BaseIntermediateOutputPath", StringComparison.OrdinalIgnoreCase) &&
                    !HasConditionAttribute(element) &&
                    !HasConditionAttribute(element.Parent))
                ?.Value
                ?.Trim();

            List<DeclaredProjectReference> projectReferences = new();
            foreach (XElement element in doc.Descendants())
            {
                if (!element.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (HasConditionAttribute(element) || HasConditionAttribute(element.Parent))
                {
                    continue;
                }

                string? rawInclude = ((string?)element.Attribute("Include"))?.Trim();
                if (string.IsNullOrWhiteSpace(rawInclude) || rawInclude.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }

                string? referenceOutputAssembly = GetMetadataValue(element, "ReferenceOutputAssembly");
                if (string.Equals(referenceOutputAssembly, "false", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? outputItemType = GetMetadataValue(element, "OutputItemType");
                if (string.Equals(outputItemType, "Analyzer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string normalizedInclude = rawInclude.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                string resolvedPath = NormalizePath(Path.Combine(projectDirectory, normalizedInclude));
                projectReferences.Add(new DeclaredProjectReference(rawInclude, resolvedPath));
            }

            return new ProjectXmlInfo(hasPackageReferences, baseIntermediateOutputPath, projectReferences);
        }
        catch
        {
            return ProjectXmlInfo.Empty;
        }
    }

    private static bool HasUnconditionalAncestorPackageReferences(
        string? projectFilePath,
        Dictionary<string, bool> cache)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            return false;
        }

        string? startDirectory = Path.GetDirectoryName(projectFilePath);
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return false;
        }

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string dirPath = NormalizePath(current.FullName);
            if (!cache.TryGetValue(dirPath, out bool dirHasPackages))
            {
                dirHasPackages = FileHasUnconditionalPackageReference(Path.Combine(dirPath, "Directory.Build.props")) ||
                    FileHasUnconditionalPackageReference(Path.Combine(dirPath, "Directory.Build.targets"));
                cache[dirPath] = dirHasPackages;
            }

            if (dirHasPackages)
            {
                return true;
            }

            if (Directory.Exists(Path.Combine(dirPath, ".git")) || File.Exists(Path.Combine(dirPath, ".git")))
            {
                break;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool FileHasUnconditionalPackageReference(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            XDocument doc = XDocument.Load(filePath);
            return doc.Descendants().Any(element =>
                (element.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) ||
                 element.Name.LocalName.Equals("PackageDownload", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace((string?)element.Attribute("Include")) &&
                !HasConditionAttribute(element) &&
                !HasConditionAttribute(element.Parent));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasConditionAttribute(XElement? element)
        => element is not null && !string.IsNullOrWhiteSpace((string?)element.Attribute("Condition"));

    private static string? GetMetadataValue(XElement itemElement, string metadataName)
    {
        string? attr = (string?)itemElement.Attribute(metadataName);
        if (!string.IsNullOrWhiteSpace(attr))
        {
            return attr.Trim();
        }

        return itemElement.Elements()
            .FirstOrDefault(child => child.Name.LocalName.Equals(metadataName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim();
    }

    private static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (fullPath.Length > root.Length)
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return fullPath;
    }

    private sealed record DeclaredProjectReference(string RawInclude, string ResolvedPath);

    private sealed record ProjectXmlInfo(
        bool HasPackageReferences,
        string? BaseIntermediateOutputPath,
        IReadOnlyList<DeclaredProjectReference> UnconditionalProjectReferences)
    {
        public static ProjectXmlInfo Empty { get; } = new(false, null, Array.Empty<DeclaredProjectReference>());
    }
}
