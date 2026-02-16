using RoslynSkills.Contracts;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynSkills.Core.Commands;

public sealed class SearchTextCommand : IAgentCommand
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.search_text",
        Summary: "Search literal/regex text patterns across one file or scoped roots with bounded structured matches.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();

        string mode = GetMode(input);
        if (!IsValidMode(mode))
        {
            errors.Add(new CommandError("invalid_input", "Property 'mode' must be 'literal' or 'regex'."));
            return errors;
        }

        if (!TryGetPatterns(input, errors, out string[] patterns))
        {
            return errors;
        }

        ValidateOptionalString(input, "file_path", errors);
        ValidateOptionalStringArray(input, "roots", errors);
        ValidateOptionalStringArray(input, "include_globs", errors);
        ValidateOptionalStringArray(input, "exclude_globs", errors);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);

        InputParsing.ValidateOptionalBool(input, "case_sensitive", errors);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);

        bool hasScope = HasNonEmptyString(input, "file_path")
            || HasNonEmptyArray(input, "roots")
            || HasNonEmptyString(input, "workspace_path");
        if (!hasScope)
        {
            errors.Add(new CommandError(
                "invalid_input",
                "At least one scope is required: provide 'file_path', 'roots', or 'workspace_path'."));
        }

        if (HasNonEmptyString(input, "file_path") &&
            InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) &&
            !File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        if (input.TryGetProperty("roots", out JsonElement rootsProperty) &&
            rootsProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement rootProperty in rootsProperty.EnumerateArray())
            {
                if (rootProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string root = (rootProperty.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (!File.Exists(root) && !Directory.Exists(root))
                {
                    errors.Add(new CommandError("path_not_found", $"Search root '{root}' does not exist."));
                }
            }
        }

        if (string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
        {
            RegexOptions options = GetRegexOptions(caseSensitive: InputParsing.GetOptionalBool(input, "case_sensitive", defaultValue: false));
            foreach (string pattern in patterns)
            {
                try
                {
                    _ = new Regex(pattern, options, TimeSpan.FromSeconds(2));
                }
                catch (ArgumentException ex)
                {
                    errors.Add(new CommandError("invalid_regex", $"Pattern '{pattern}' is not a valid regex: {ex.Message}"));
                }
            }
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!TryGetPatterns(input, errors, out string[] patterns))
        {
            return new CommandExecutionResult(null, errors);
        }

        string mode = GetMode(input);
        if (!IsValidMode(mode))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'mode' must be 'literal' or 'regex'.") });
        }

        string? filePath = GetOptionalTrimmedString(input, "file_path");
        string[] roots = GetOptionalTrimmedStringArray(input, "roots");
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);

        bool caseSensitive = InputParsing.GetOptionalBool(input, "case_sensitive", defaultValue: false);
        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 200, minValue: 1, maxValue: 10_000);
        int maxFiles = InputParsing.GetOptionalInt(input, "max_files", defaultValue: 1_000, minValue: 1, maxValue: 100_000);
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: brief ? 0 : 1, minValue: 0, maxValue: 10);
        int previewMaxChars = InputParsing.GetOptionalInt(input, "preview_max_chars", defaultValue: 180, minValue: 40, maxValue: 4_000);

        string[] includeGlobs = GetOptionalTrimmedStringArray(input, "include_globs");
        if (includeGlobs.Length == 0)
        {
            includeGlobs = ["**/*.cs", "**/*.csx"];
        }

        string[] excludeGlobs = GetOptionalTrimmedStringArray(input, "exclude_globs");

        List<SearchScope> scopes = ResolveScopes(filePath, roots, workspacePath);
        if (scopes.Count == 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "No valid search scope was resolved.") });
        }

        if (filePath is not null && !File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        Regex[] regexes = Array.Empty<Regex>();
        if (string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
        {
            RegexOptions options = GetRegexOptions(caseSensitive);
            regexes = patterns.Select(pattern => new Regex(pattern, options, TimeSpan.FromSeconds(2))).ToArray();
        }

        List<GlobPattern> includePatterns = includeGlobs.Select(glob => GlobPattern.Create(glob)).Where(pattern => pattern is not null).Select(pattern => pattern!).ToList();
        List<GlobPattern> excludePatterns = excludeGlobs.Select(glob => GlobPattern.Create(glob)).Where(pattern => pattern is not null).Select(pattern => pattern!).ToList();

        HashSet<string> seenFiles = new(PathComparer);
        List<SearchMatch> matches = new();
        int filesScanned = 0;
        int filesWithMatches = 0;
        bool truncated = false;
        bool fileLimitReached = false;

        foreach ((SearchScope scope, string candidatePath) in EnumerateCandidateFiles(scopes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fullPath = Path.GetFullPath(candidatePath);
            if (!seenFiles.Add(fullPath))
            {
                continue;
            }

            if (filesScanned >= maxFiles)
            {
                fileLimitReached = true;
                break;
            }

            if (!includeGenerated && CommandFileFilters.IsGeneratedPath(fullPath))
            {
                continue;
            }

            string relativePath = BuildRelativePath(scope, fullPath);
            string fileName = Path.GetFileName(fullPath);
            if (!MatchesPatterns(relativePath, fileName, includePatterns, excludePatterns))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("io_error", $"Failed to read '{fullPath}': {ex.Message}") });
            }

            filesScanned++;
            int beforeCount = matches.Count;
            StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines[lineIndex];

                if (string.Equals(mode, "literal", StringComparison.OrdinalIgnoreCase))
                {
                    for (int patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
                    {
                        string pattern = patterns[patternIndex];
                        int searchFrom = 0;
                        while (true)
                        {
                            int foundAt = line.IndexOf(pattern, searchFrom, comparison);
                            if (foundAt < 0)
                            {
                                break;
                            }

                            matches.Add(new SearchMatch(
                                file_path: fullPath,
                                line: lineIndex + 1,
                                column: foundAt + 1,
                                pattern: pattern,
                                match_text: pattern,
                                preview: BuildPreview(lines, lineIndex, contextLines, previewMaxChars)));

                            if (matches.Count >= maxResults)
                            {
                                truncated = true;
                                break;
                            }

                            searchFrom = foundAt + Math.Max(1, pattern.Length);
                            if (searchFrom > line.Length)
                            {
                                break;
                            }
                        }

                        if (truncated)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    for (int patternIndex = 0; patternIndex < regexes.Length; patternIndex++)
                    {
                        Regex regex = regexes[patternIndex];
                        string pattern = patterns[patternIndex];

                        foreach (Match match in regex.Matches(line))
                        {
                            if (!match.Success)
                            {
                                continue;
                            }

                            string matchText = match.Value;
                            if (matchText.Length == 0)
                            {
                                continue;
                            }

                            matches.Add(new SearchMatch(
                                file_path: fullPath,
                                line: lineIndex + 1,
                                column: match.Index + 1,
                                pattern: pattern,
                                match_text: TruncateText(matchText, maxChars: 120),
                                preview: BuildPreview(lines, lineIndex, contextLines, previewMaxChars)));

                            if (matches.Count >= maxResults)
                            {
                                truncated = true;
                                break;
                            }
                        }

                        if (truncated)
                        {
                            break;
                        }
                    }
                }

                if (truncated)
                {
                    break;
                }
            }

            if (matches.Count > beforeCount)
            {
                filesWithMatches++;
            }

            if (truncated)
            {
                break;
            }
        }

        object matchPayload = brief
            ? matches.Select(match => new
            {
                match.file_path,
                match.line,
                match.column,
                match.pattern,
                match.preview,
            }).ToArray()
            : matches;

        object data = new
        {
            query = new
            {
                patterns,
                mode,
                case_sensitive = caseSensitive,
                file_path = filePath,
                roots,
                workspace_path = workspacePath,
                include_globs = includeGlobs,
                exclude_globs = excludeGlobs,
                include_generated = includeGenerated,
                max_results = maxResults,
                max_files = maxFiles,
                context_lines = contextLines,
                preview_max_chars = previewMaxChars,
                brief,
            },
            files_scanned = filesScanned,
            files_with_matches = filesWithMatches,
            total_matches = matches.Count,
            truncated,
            file_limit_reached = fileLimitReached,
            matches = matchPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool TryGetPatterns(JsonElement input, List<CommandError> errors, out string[] patterns)
    {
        List<string> values = new();

        if (input.TryGetProperty("pattern", out JsonElement patternProperty))
        {
            if (patternProperty.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", "Property 'pattern' must be a string when provided."));
            }
            else
            {
                string value = (patternProperty.GetString() ?? string.Empty).Trim();
                if (value.Length > 0)
                {
                    values.Add(value);
                }
            }
        }

        if (input.TryGetProperty("patterns", out JsonElement patternsProperty))
        {
            if (patternsProperty.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new CommandError("invalid_input", "Property 'patterns' must be an array of strings."));
            }
            else
            {
                foreach (JsonElement patternElement in patternsProperty.EnumerateArray())
                {
                    if (patternElement.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(new CommandError("invalid_input", "Property 'patterns' must contain only strings."));
                        continue;
                    }

                    string value = (patternElement.GetString() ?? string.Empty).Trim();
                    if (value.Length > 0)
                    {
                        values.Add(value);
                    }
                }
            }
        }

        values = values
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToList();

        if (values.Count == 0)
        {
            errors.Add(new CommandError("invalid_input", "Provide at least one non-empty search pattern via 'pattern' or 'patterns'."));
            patterns = Array.Empty<string>();
            return false;
        }

        patterns = values.ToArray();
        return true;
    }

    private static string GetMode(JsonElement input)
    {
        if (!input.TryGetProperty("mode", out JsonElement modeProperty) || modeProperty.ValueKind != JsonValueKind.String)
        {
            return "literal";
        }

        string mode = (modeProperty.GetString() ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(mode) ? "literal" : mode;
    }

    private static bool IsValidMode(string mode)
        => string.Equals(mode, "literal", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase);

    private static RegexOptions GetRegexOptions(bool caseSensitive)
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return options;
    }

    private static string? GetOptionalTrimmedString(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string value = (property.GetString() ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string[] GetOptionalTrimmedStringArray(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        List<string> values = new();
        foreach (JsonElement element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string value = (element.GetString() ?? string.Empty).Trim();
            if (value.Length > 0)
            {
                values.Add(value);
            }
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasNonEmptyString(JsonElement input, string propertyName)
    {
        string? value = GetOptionalTrimmedString(input, propertyName);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasNonEmptyArray(JsonElement input, string propertyName)
    {
        return GetOptionalTrimmedStringArray(input, propertyName).Length > 0;
    }

    private static void ValidateOptionalString(JsonElement input, string propertyName, List<CommandError> errors)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property))
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must be a string when provided."));
            return;
        }

        string value = (property.GetString() ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must not be empty when provided."));
        }
    }

    private static void ValidateOptionalStringArray(JsonElement input, string propertyName, List<CommandError> errors)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property))
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must be an array of strings when provided."));
            return;
        }

        foreach (JsonElement element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must contain only strings."));
                return;
            }

            string value = (element.GetString() ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must not contain empty values."));
                return;
            }
        }
    }

    private static List<SearchScope> ResolveScopes(string? filePath, IReadOnlyList<string> roots, string? workspacePath)
    {
        List<SearchScope> scopes = new();
        HashSet<string> seen = new(PathComparer);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string fullPath = Path.GetFullPath(filePath);
            if (seen.Add(fullPath))
            {
                scopes.Add(new SearchScope(fullPath, is_file: true));
            }
        }

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(root);
            bool isFile = File.Exists(fullPath);
            bool isDirectory = Directory.Exists(fullPath);
            if (!isFile && !isDirectory)
            {
                continue;
            }

            if (seen.Add(fullPath))
            {
                    scopes.Add(new SearchScope(fullPath, is_file: isFile));
            }
        }

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            string fullPath = Path.GetFullPath(workspacePath);
            if (File.Exists(fullPath))
            {
                string extension = Path.GetExtension(fullPath);
                string scopePath = string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(extension, ".csx", StringComparison.OrdinalIgnoreCase)
                    ? fullPath
                    : Path.GetDirectoryName(fullPath) ?? fullPath;
                bool isFile = File.Exists(scopePath);
                if (seen.Add(scopePath))
                {
                    scopes.Add(new SearchScope(scopePath, is_file: isFile));
                }
            }
            else if (Directory.Exists(fullPath))
            {
                if (seen.Add(fullPath))
                {
                    scopes.Add(new SearchScope(fullPath, is_file: false));
                }
            }
        }

        return scopes;
    }

    private static IEnumerable<(SearchScope Scope, string CandidatePath)> EnumerateCandidateFiles(IReadOnlyList<SearchScope> scopes)
    {
        foreach (SearchScope scope in scopes)
        {
            if (scope.is_file)
            {
                if (File.Exists(scope.path))
                {
                    yield return (scope, scope.path);
                }

                continue;
            }

            if (!Directory.Exists(scope.path))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(scope.path, "*", SearchOption.AllDirectories))
            {
                yield return (scope, file);
            }
        }
    }

    private static string BuildRelativePath(SearchScope scope, string fullPath)
    {
        if (scope.is_file)
        {
            return Path.GetFileName(fullPath).Replace('\\', '/');
        }

        try
        {
            string relative = Path.GetRelativePath(scope.path, fullPath);
            return relative.Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    private static bool MatchesPatterns(
        string relativePath,
        string fileName,
        IReadOnlyList<GlobPattern> includePatterns,
        IReadOnlyList<GlobPattern> excludePatterns)
    {
        bool include = includePatterns.Count == 0 || includePatterns.Any(pattern => pattern.IsMatch(relativePath, fileName));
        if (!include)
        {
            return false;
        }

        bool excluded = excludePatterns.Any(pattern => pattern.IsMatch(relativePath, fileName));
        return !excluded;
    }

    private static string BuildPreview(string[] lines, int lineIndex, int contextLines, int maxChars)
    {
        int startLine = Math.Max(0, lineIndex - contextLines);
        int endLine = Math.Min(lines.Length - 1, lineIndex + contextLines);
        List<string> snippetLines = new();
        for (int i = startLine; i <= endLine; i++)
        {
            snippetLines.Add($"{i + 1,4}: {lines[i]}");
        }

        string preview = string.Join(Environment.NewLine, snippetLines);
        return TruncateText(preview, maxChars);
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars <= 3)
        {
            return text[..maxChars];
        }

        return text[..(maxChars - 3)] + "...";
    }

    private sealed record SearchScope(string path, bool is_file);

    private sealed record SearchMatch(
        string file_path,
        int line,
        int column,
        string pattern,
        string match_text,
        string preview);

    private sealed class GlobPattern
    {
        private readonly Regex _regex;
        private readonly bool _matchOnFileNameOnly;

        private GlobPattern(Regex regex, bool matchOnFileNameOnly)
        {
            _regex = regex;
            _matchOnFileNameOnly = matchOnFileNameOnly;
        }

        public static GlobPattern? Create(string pattern)
        {
            string normalized = (pattern ?? string.Empty).Trim().Replace('\\', '/');
            if (normalized.Length == 0)
            {
                return null;
            }

            bool fileNameOnly = !normalized.Contains('/', StringComparison.Ordinal);
            string regexPattern = "^" + ConvertGlobToRegex(normalized) + "$";
            RegexOptions options = RegexOptions.CultureInvariant;
            if (OperatingSystem.IsWindows())
            {
                options |= RegexOptions.IgnoreCase;
            }

            return new GlobPattern(new Regex(regexPattern, options), fileNameOnly);
        }

        public bool IsMatch(string relativePath, string fileName)
        {
            string candidate = _matchOnFileNameOnly ? fileName : relativePath;
            return _regex.IsMatch(candidate);
        }

        private static string ConvertGlobToRegex(string pattern)
        {
            int index = 0;
            System.Text.StringBuilder builder = new(pattern.Length * 2);
            while (index < pattern.Length)
            {
                char c = pattern[index];
                if (c == '*')
                {
                    bool isDoubleStar = index + 1 < pattern.Length && pattern[index + 1] == '*';
                    if (isDoubleStar)
                    {
                        bool followedBySlash = index + 2 < pattern.Length && pattern[index + 2] == '/';
                        if (followedBySlash)
                        {
                            builder.Append("(?:.*/)?");
                            index += 3;
                        }
                        else
                        {
                            builder.Append(".*");
                            index += 2;
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                        index++;
                    }

                    continue;
                }

                if (c == '?')
                {
                    builder.Append("[^/]");
                    index++;
                    continue;
                }

                if (c == '/')
                {
                    builder.Append('/');
                    index++;
                    continue;
                }

                builder.Append(Regex.Escape(c.ToString()));
                index++;
            }

            return builder.ToString();
        }
    }
}
