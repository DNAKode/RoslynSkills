using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class GetSolutionSnapshotCommand : IAgentCommand
{
    private const string DefaultMode = "compact";
    private static readonly string[] SupportedModes = ["raw", "compact", "guided"];
    private static readonly string[] DefaultSeverityFilter = ["Error", "Warning"];

    public CommandDescriptor Descriptor { get; } = new(
        Id: "diag.get_solution_snapshot",
        Summary: "Analyze C# diagnostics for a file set with filtered raw/compact/guided output modes.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: [CommandTrait.PotentiallySlow]);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        string[] filePaths = InputParsing.GetOptionalStringArray(input, "file_paths");
        IReadOnlyDictionary<string, string> overrides = ParseFileOverrides(input, errors);
        string? directoryPath = null;
        if (input.TryGetProperty("directory_path", out JsonElement directoryProperty) && directoryProperty.ValueKind == JsonValueKind.String)
        {
            directoryPath = directoryProperty.GetString();
        }

        if (filePaths.Length == 0 && string.IsNullOrWhiteSpace(directoryPath) && overrides.Count == 0)
        {
            errors.Add(new CommandError(
                "invalid_input",
                "At least one of 'file_paths', 'directory_path', or 'file_overrides' must be provided."));
            return errors;
        }

        if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
        {
            errors.Add(new CommandError(
                "directory_not_found",
                $"Input directory '{directoryPath}' does not exist."));
        }

        foreach (string path in filePaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) && !overrides.ContainsKey(fullPath))
            {
                errors.Add(new CommandError("file_not_found", $"Input file '{path}' does not exist and has no override content."));
            }
        }

        if (input.TryGetProperty("mode", out JsonElement modeProperty))
        {
            if (modeProperty.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", "Property 'mode' must be a string."));
            }
            else
            {
                string mode = modeProperty.GetString() ?? string.Empty;
                if (!IsSupportedMode(mode))
                {
                    errors.Add(new CommandError(
                        "invalid_input",
                        $"Unsupported mode '{mode}'. Supported modes: {string.Join(", ", SupportedModes)}."));
                }
            }
        }

        if (input.TryGetProperty("severity_filter", out JsonElement severityFilterProperty))
        {
            if (severityFilterProperty.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new CommandError("invalid_input", "Property 'severity_filter' must be an array of severity names."));
            }
            else
            {
                foreach (JsonElement severityItem in severityFilterProperty.EnumerateArray())
                {
                    if (severityItem.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(new CommandError("invalid_input", "Property 'severity_filter' must contain string values only."));
                        break;
                    }

                    string severity = severityItem.GetString() ?? string.Empty;
                    if (!TryNormalizeSeverity(severity, out _))
                    {
                        errors.Add(new CommandError(
                            "invalid_input",
                            $"Unsupported severity '{severity}'. Supported severities: Error, Warning, Info, Hidden."));
                        break;
                    }
                }
            }
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> validationErrors = Validate(input).ToList();
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        string mode = GetMode(input);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: false);
        int maxFiles = InputParsing.GetOptionalInt(input, "max_files", defaultValue: 500, minValue: 1, maxValue: 20_000);
        int maxDiagnostics = InputParsing.GetOptionalInt(
            input,
            "max_diagnostics",
            defaultValue: brief ? 200 : 500,
            minValue: 1,
            maxValue: 100_000);
        int maxDiagnosticsPerFile = InputParsing.GetOptionalInt(input, "max_diagnostics_per_file", defaultValue: 50, minValue: 1, maxValue: 10_000);
        int defaultMaxFilesInOutput = string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase)
            ? maxFiles
            : brief
                ? 8
                : 15;
        int maxFilesInOutput = InputParsing.GetOptionalInt(input, "max_files_in_output", defaultMaxFilesInOutput, minValue: 1, maxValue: 20_000);
        int maxTopDiagnostics = InputParsing.GetOptionalInt(
            input,
            "max_top_diagnostics",
            defaultValue: brief ? 5 : 10,
            minValue: 1,
            maxValue: 500);
        bool recursive = InputParsing.GetOptionalBool(input, "recursive", defaultValue: true);
        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        bool includeSummary = InputParsing.GetOptionalBool(input, "include_summary", defaultValue: true);
        bool includeQuery = InputParsing.GetOptionalBool(input, "include_query", defaultValue: !brief);
        bool includeResolution = InputParsing.GetOptionalBool(input, "include_resolution", defaultValue: !brief);
        bool includeUnfiltered = InputParsing.GetOptionalBool(
            input,
            "include_unfiltered",
            defaultValue: string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase) && !brief);
        bool includeTopDiagnostics = InputParsing.GetOptionalBool(input, "include_top_diagnostics", defaultValue: true);
        bool includeFiles = InputParsing.GetOptionalBool(input, "include_files", defaultValue: !brief);
        bool includeRawDiagnostics = InputParsing.GetOptionalBool(
            input,
            "include_diagnostics",
            defaultValue: string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase) && !brief);
        bool includeGuidance = InputParsing.GetOptionalBool(
            input,
            "include_guidance",
            defaultValue: string.Equals(mode, "guided", StringComparison.OrdinalIgnoreCase) && !brief);
        bool includeFilesWithoutDiagnostics = InputParsing.GetOptionalBool(
            input,
            "include_files_without_diagnostics",
            defaultValue: string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase) && !brief);
        bool includePerFileDiagnostics = InputParsing.GetOptionalBool(
            input,
            "include_per_file_diagnostics",
            defaultValue: string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase) && !brief);
        bool useSdkDefaults = InputParsing.GetOptionalBool(input, "use_sdk_defaults", defaultValue: true);

        HashSet<string> severityFilter = ParseSeverityFilter(input);
        HashSet<string> diagnosticIdFilter = InputParsing
            .GetOptionalStringArray(input, "diagnostic_ids")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] additionalGlobalUsings = InputParsing.GetOptionalStringArray(input, "global_usings");
        IReadOnlyDictionary<string, string> overrides = ParseFileOverrides(input, errors: null);
        string[] explicitFilePaths = InputParsing.GetOptionalStringArray(input, "file_paths");
        HashSet<string> candidatePaths = new(StringComparer.OrdinalIgnoreCase);
        int skippedGeneratedFiles = 0;

        foreach (string explicitPath in explicitFilePaths)
        {
            string fullPath = Path.GetFullPath(explicitPath);
            if (!includeGenerated && IsGeneratedPath(fullPath))
            {
                skippedGeneratedFiles++;
                continue;
            }

            candidatePaths.Add(fullPath);
        }

        if (input.TryGetProperty("directory_path", out JsonElement directoryProperty) &&
            directoryProperty.ValueKind == JsonValueKind.String)
        {
            string? directoryPath = directoryProperty.GetString();
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                IEnumerable<string> discovered = Directory.EnumerateFiles(
                    directoryPath,
                    "*.cs",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                foreach (string filePath in discovered)
                {
                    string fullPath = Path.GetFullPath(filePath);
                    if (!includeGenerated && IsGeneratedPath(fullPath))
                    {
                        skippedGeneratedFiles++;
                        continue;
                    }

                    candidatePaths.Add(fullPath);
                }
            }
        }

        foreach (string overridePath in overrides.Keys)
        {
            if (!includeGenerated && IsGeneratedPath(overridePath))
            {
                skippedGeneratedFiles++;
                continue;
            }

            candidatePaths.Add(overridePath);
        }

        string[] filePaths = candidatePaths
            .Where(path => File.Exists(path) || overrides.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .ToArray();

        if (filePaths.Length == 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "No C# files were resolved for snapshot diagnostics.") });
        }

        List<SyntaxTree> trees = new(filePaths.Length);
        bool hasTopLevelStatements = false;
        foreach (string filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source;
            if (overrides.TryGetValue(filePath, out string? overrideContent))
            {
                source = overrideContent;
            }
            else
            {
                source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
            trees.Add(tree);

            if (!hasTopLevelStatements)
            {
                SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                hasTopLevelStatements = root.DescendantNodes(descendIntoTrivia: false).OfType<GlobalStatementSyntax>().Any();
            }
        }

        if (useSdkDefaults || additionalGlobalUsings.Length > 0)
        {
            trees.Add(BuildGlobalUsingsTree(useSdkDefaults, additionalGlobalUsings, cancellationToken));
        }

        CSharpCompilation compilation = CreateCompilation("RoslynSkills.Snapshot", trees, useSdkDefaults, hasTopLevelStatements);
        IReadOnlyList<Diagnostic> allDiagnostics = compilation.GetDiagnostics(cancellationToken);
        NormalizedDiagnostic[] normalizedAll = CompilationDiagnostics.Normalize(allDiagnostics);
        List<NormalizedDiagnostic> filteredDiagnostics = normalizedAll
            .Where(d => severityFilter.Contains(NormalizeSeverity(d.severity)))
            .Where(d => diagnosticIdFilter.Count == 0 || diagnosticIdFilter.Contains(d.id))
            .OrderByDescending(d => GetSeverityRank(d.severity))
            .ThenBy(d => d.file_path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.line)
            .ThenBy(d => d.column)
            .ThenBy(d => d.id, StringComparer.Ordinal)
            .ToList();

        NormalizedDiagnostic[] returnedDiagnostics = filteredDiagnostics.Take(maxDiagnostics).ToArray();
        SeverityCounts filteredCounts = CountBySeverity(returnedDiagnostics);
        SeverityCounts unfilteredCounts = CountBySeverity(normalizedAll);

        Dictionary<string, List<NormalizedDiagnostic>> diagnosticsByFile = returnedDiagnostics
            .GroupBy(d => string.IsNullOrWhiteSpace(d.file_path) ? "<global>" : d.file_path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        List<SnapshotFileResult> fileResults = new();
        foreach (string filePath in filePaths.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            diagnosticsByFile.TryGetValue(filePath, out List<NormalizedDiagnostic>? perFile);
            perFile ??= new List<NormalizedDiagnostic>();

            if (!includeFilesWithoutDiagnostics && perFile.Count == 0)
            {
                continue;
            }

            SeverityCounts perFileCounts = CountBySeverity(perFile);
            IReadOnlyList<NormalizedDiagnostic> perFilePayload = includePerFileDiagnostics
                ? perFile.Take(maxDiagnosticsPerFile).ToArray()
                : Array.Empty<NormalizedDiagnostic>();

            string[] topIds = perFile
                .GroupBy(d => d.id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(g => g.Key)
                .ToArray();

            fileResults.Add(new SnapshotFileResult(
                file_path: filePath,
                total: perFile.Count,
                errors: perFileCounts.Errors,
                warnings: perFileCounts.Warnings,
                info: perFileCounts.Info,
                hidden: perFileCounts.Hidden,
                top_diagnostic_ids: topIds,
                diagnostics: perFilePayload));
        }

        SnapshotFileResult[] files = fileResults
            .OrderByDescending(f => f.errors)
            .ThenByDescending(f => f.warnings)
            .ThenByDescending(f => f.total)
            .ThenBy(f => f.file_path, StringComparer.OrdinalIgnoreCase)
            .Take(maxFilesInOutput)
            .ToArray();

        TopDiagnosticSummary[] topDiagnostics = filteredDiagnostics
            .GroupBy(d => new { Id = d.id, Severity = NormalizeSeverity(d.severity) })
            .Select(g =>
            {
                NormalizedDiagnostic sample = g.First();
                return new TopDiagnosticSummary(
                    id: g.Key.Id,
                    severity: g.Key.Severity,
                    count: g.Count(),
                    sample_message: sample.message,
                    sample_file_path: sample.file_path,
                    sample_line: sample.line,
                    sample_column: sample.column);
            })
            .OrderByDescending(s => s.count)
            .ThenByDescending(s => GetSeverityRank(s.severity))
            .ThenBy(s => s.id, StringComparer.OrdinalIgnoreCase)
            .Take(maxTopDiagnostics)
            .ToArray();

        GuidanceSuggestion[] guidance = includeGuidance
            ? BuildGuidance(topDiagnostics, files)
            : Array.Empty<GuidanceSuggestion>();

        object summary = new
        {
            analyzed_files = filePaths.Length,
            diagnostics = new
            {
                total = filteredDiagnostics.Count,
                returned = returnedDiagnostics.Length,
                errors = filteredCounts.Errors,
                warnings = filteredCounts.Warnings,
                info = filteredCounts.Info,
                hidden = filteredCounts.Hidden,
            },
            output = new
            {
                files_returned = files.Length,
                top_diagnostics_returned = topDiagnostics.Length,
                guidance_returned = guidance.Length,
            },
        };

        Dictionary<string, object?> data = new();
        if (includeSummary)
        {
            data["summary"] = summary;
        }

        data["mode"] = mode;
        data["total_files"] = filePaths.Length;
        data["total_diagnostics"] = filteredDiagnostics.Count;
        data["returned_diagnostics"] = returnedDiagnostics.Length;
        data["errors"] = filteredCounts.Errors;
        data["warnings"] = filteredCounts.Warnings;
        data["info"] = filteredCounts.Info;
        data["hidden"] = filteredCounts.Hidden;

        if (includeQuery)
        {
            data["query"] = new
            {
                file_paths = explicitFilePaths,
                directory_path = input.TryGetProperty("directory_path", out JsonElement directoryPropertyValue) &&
                                 directoryPropertyValue.ValueKind == JsonValueKind.String
                    ? directoryPropertyValue.GetString()
                    : null,
                recursive,
                brief,
                include_generated = includeGenerated,
                severity_filter = severityFilter.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                diagnostic_ids = diagnosticIdFilter.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                max_files = maxFiles,
                max_files_in_output = maxFilesInOutput,
                max_diagnostics = maxDiagnostics,
                max_diagnostics_per_file = maxDiagnosticsPerFile,
                include_files_without_diagnostics = includeFilesWithoutDiagnostics,
                include_per_file_diagnostics = includePerFileDiagnostics,
                use_sdk_defaults = useSdkDefaults,
                global_usings = additionalGlobalUsings,
                include_summary = includeSummary,
                include_query = includeQuery,
                include_resolution = includeResolution,
                include_unfiltered = includeUnfiltered,
                include_top_diagnostics = includeTopDiagnostics,
                include_files = includeFiles,
                include_diagnostics = includeRawDiagnostics,
                include_guidance = includeGuidance,
                file_overrides = overrides.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            };
        }

        if (includeResolution)
        {
            data["resolution"] = new
            {
                total_candidate_files = candidatePaths.Count,
                analyzed_files = filePaths.Length,
                skipped_generated_files = skippedGeneratedFiles,
            };
        }

        if (includeUnfiltered)
        {
            data["unfiltered"] = new
            {
                total_diagnostics = normalizedAll.Length,
                errors = unfilteredCounts.Errors,
                warnings = unfilteredCounts.Warnings,
                info = unfilteredCounts.Info,
                hidden = unfilteredCounts.Hidden,
            };
        }

        if (includeTopDiagnostics)
        {
            data["top_diagnostics"] = topDiagnostics;
        }

        if (includeFiles)
        {
            data["files"] = files;
        }

        if (includeRawDiagnostics)
        {
            data["diagnostics"] = returnedDiagnostics;
        }

        if (includeGuidance)
        {
            data["guidance"] = guidance;
        }

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private sealed record SnapshotFileResult(
        string file_path,
        int total,
        int errors,
        int warnings,
        int info,
        int hidden,
        IReadOnlyList<string> top_diagnostic_ids,
        IReadOnlyList<NormalizedDiagnostic> diagnostics);

    private sealed record TopDiagnosticSummary(
        string id,
        string severity,
        int count,
        string sample_message,
        string sample_file_path,
        int sample_line,
        int sample_column);

    private sealed record GuidanceSuggestion(
        int rank,
        string operation_id,
        string rationale,
        IReadOnlyList<string> diagnostic_ids,
        object example_input);

    private sealed record SeverityCounts(
        int Errors,
        int Warnings,
        int Info,
        int Hidden);

    private static IReadOnlyDictionary<string, string> ParseFileOverrides(JsonElement input, List<CommandError>? errors)
    {
        if (!input.TryGetProperty("file_overrides", out JsonElement overridesProperty))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase);
        if (overridesProperty.ValueKind != JsonValueKind.Array)
        {
            errors?.Add(new CommandError("invalid_input", "Property 'file_overrides' must be an array."));
            return overrides;
        }

        foreach (JsonElement item in overridesProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors?.Add(new CommandError("invalid_input", "Each item in 'file_overrides' must be an object with 'file_path' and 'content'."));
                continue;
            }

            if (!item.TryGetProperty("file_path", out JsonElement filePathProperty) || filePathProperty.ValueKind != JsonValueKind.String)
            {
                errors?.Add(new CommandError("invalid_input", "Each file override must include a string 'file_path'."));
                continue;
            }

            if (!item.TryGetProperty("content", out JsonElement contentProperty) || contentProperty.ValueKind != JsonValueKind.String)
            {
                errors?.Add(new CommandError("invalid_input", "Each file override must include a string 'content'."));
                continue;
            }

            string? rawPath = filePathProperty.GetString();
            string? content = contentProperty.GetString();
            if (string.IsNullOrWhiteSpace(rawPath) || content is null)
            {
                errors?.Add(new CommandError("invalid_input", "Each file override must include non-empty 'file_path' and valid 'content'."));
                continue;
            }

            string fullPath = Path.GetFullPath(rawPath);
            if (overrides.ContainsKey(fullPath))
            {
                errors?.Add(new CommandError("invalid_input", $"Duplicate file override path '{rawPath}'."));
                continue;
            }

            overrides[fullPath] = content;
        }

        return overrides;
    }

    private static bool IsSupportedMode(string mode)
        => SupportedModes.Any(supported => string.Equals(supported, mode, StringComparison.OrdinalIgnoreCase));

    private static string GetMode(JsonElement input)
    {
        if (!input.TryGetProperty("mode", out JsonElement modeProperty) || modeProperty.ValueKind != JsonValueKind.String)
        {
            return DefaultMode;
        }

        string mode = modeProperty.GetString() ?? DefaultMode;
        return IsSupportedMode(mode) ? mode.ToLowerInvariant() : DefaultMode;
    }

    private static HashSet<string> ParseSeverityFilter(JsonElement input)
    {
        if (!input.TryGetProperty("severity_filter", out JsonElement filterProperty) || filterProperty.ValueKind != JsonValueKind.Array)
        {
            return DefaultSeverityFilter.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> severities = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in filterProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (TryNormalizeSeverity(item.GetString() ?? string.Empty, out string normalized))
            {
                severities.Add(normalized);
            }
        }

        if (severities.Count == 0)
        {
            foreach (string severity in DefaultSeverityFilter)
            {
                severities.Add(severity);
            }
        }

        return severities;
    }

    private static bool TryNormalizeSeverity(string rawSeverity, out string normalizedSeverity)
    {
        string trimmed = rawSeverity.Trim();
        if (string.Equals(trimmed, "Information", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSeverity = "Info";
            return true;
        }

        if (string.Equals(trimmed, "Error", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSeverity = "Error";
            return true;
        }

        if (string.Equals(trimmed, "Warning", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSeverity = "Warning";
            return true;
        }

        if (string.Equals(trimmed, "Info", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSeverity = "Info";
            return true;
        }

        if (string.Equals(trimmed, "Hidden", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSeverity = "Hidden";
            return true;
        }

        normalizedSeverity = string.Empty;
        return false;
    }

    private static string NormalizeSeverity(string severity)
        => TryNormalizeSeverity(severity, out string normalizedSeverity) ? normalizedSeverity : severity;

    private static int GetSeverityRank(string severity)
    {
        string normalized = NormalizeSeverity(severity);
        return normalized switch
        {
            "Error" => 4,
            "Warning" => 3,
            "Info" => 2,
            "Hidden" => 1,
            _ => 0,
        };
    }

    private static SeverityCounts CountBySeverity(IReadOnlyList<NormalizedDiagnostic> diagnostics)
    {
        int errors = 0;
        int warnings = 0;
        int info = 0;
        int hidden = 0;

        foreach (NormalizedDiagnostic diagnostic in diagnostics)
        {
            string severity = NormalizeSeverity(diagnostic.severity);
            if (string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
            {
                errors++;
            }
            else if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase))
            {
                warnings++;
            }
            else if (string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase))
            {
                info++;
            }
            else if (string.Equals(severity, "Hidden", StringComparison.OrdinalIgnoreCase))
            {
                hidden++;
            }
        }

        return new SeverityCounts(errors, warnings, info, hidden);
    }

    private static bool IsGeneratedPath(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] segments = filePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        bool useSdkDefaults,
        bool hasTopLevelStatements)
    {
        CSharpCompilationOptions options = new(hasTopLevelStatements
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary);

        if (useSdkDefaults)
        {
            options = options.WithNullableContextOptions(NullableContextOptions.Enable);
        }

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: syntaxTrees,
            references: CompilationReferenceBuilder.BuildMetadataReferences(),
            options: options);
    }

    private static SyntaxTree BuildGlobalUsingsTree(
        bool includeSdkDefaults,
        IReadOnlyList<string> additionalGlobalUsings,
        CancellationToken cancellationToken)
    {
        HashSet<string> namespaces = new(StringComparer.OrdinalIgnoreCase);
        if (includeSdkDefaults)
        {
            foreach (string ns in GetSdkDefaultGlobalUsings())
            {
                namespaces.Add(ns);
            }
        }

        foreach (string ns in additionalGlobalUsings)
        {
            if (!string.IsNullOrWhiteSpace(ns))
            {
                namespaces.Add(ns.Trim());
            }
        }

        string source = string.Join(
            Environment.NewLine,
            namespaces
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(ns => $"global using {ns};"));

        return CSharpSyntaxTree.ParseText(
            source,
            path: "<roslynskills-global-usings>",
            cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<string> GetSdkDefaultGlobalUsings()
    {
        return
        [
            "System",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Net.Http",
            "System.Threading",
            "System.Threading.Tasks",
        ];
    }

    private static GuidanceSuggestion[] BuildGuidance(
        IReadOnlyList<TopDiagnosticSummary> topDiagnostics,
        IReadOnlyList<SnapshotFileResult> files)
    {
        List<GuidanceSuggestion> suggestions = new();
        HashSet<string> ids = topDiagnostics.Select(d => d.id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? targetFilePath = files.FirstOrDefault(f => f.total > 0)?.file_path;
        if (targetFilePath is null)
        {
            targetFilePath = topDiagnostics.FirstOrDefault()?.sample_file_path;
        }

        if (ids.Overlaps(["CS0246", "CS0103"]))
        {
            suggestions.Add(new GuidanceSuggestion(
                rank: suggestions.Count + 1,
                operation_id: "repair.propose_from_diagnostics",
                rationale: "Unresolved symbol diagnostics are often fixed most efficiently by guided repair planning.",
                diagnostic_ids: ids.Where(id => id is "CS0246" or "CS0103").OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                example_input: new
                {
                    file_path = targetFilePath,
                    max_steps = 5,
                }));
        }

        if (ids.Overlaps(["CS0105", "CS8019"]))
        {
            suggestions.Add(new GuidanceSuggestion(
                rank: suggestions.Count + 1,
                operation_id: "edit.update_usings",
                rationale: "Using-directive diagnostics can be addressed with deterministic using normalization.",
                diagnostic_ids: ids.Where(id => id is "CS0105" or "CS8019").OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                example_input: new
                {
                    file_path = targetFilePath,
                    remove_unused = true,
                    apply = true,
                }));
        }

        if (ids.Overlaps(["CS1002", "CS1513", "CS1514", "CS1519", "CS1026", "CS1001"]))
        {
            suggestions.Add(new GuidanceSuggestion(
                rank: suggestions.Count + 1,
                operation_id: "diag.get_after_edit",
                rationale: "Syntax errors are best iterated with tight proposed-content diagnostic loops before file writes.",
                diagnostic_ids: ids.Where(id => id is "CS1002" or "CS1513" or "CS1514" or "CS1519" or "CS1026" or "CS1001")
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                example_input: new
                {
                    file_path = targetFilePath,
                    proposed_content = "<updated file text>",
                }));
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add(new GuidanceSuggestion(
                rank: 1,
                operation_id: "diag.get_solution_snapshot",
                rationale: "No specialized rule matched. Narrow scope and switch to raw mode for deeper triage.",
                diagnostic_ids: topDiagnostics.Select(d => d.id).Take(5).ToArray(),
                example_input: new
                {
                    file_paths = files.Select(f => f.file_path).Take(5).ToArray(),
                    mode = "raw",
                    include_generated = false,
                    severity_filter = new[] { "Error", "Warning" },
                }));
        }

        return suggestions.ToArray();
    }
}

