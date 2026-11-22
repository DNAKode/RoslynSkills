using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class GetWorkspaceSnapshotCommand : IAgentCommand
{
    private const string DefaultMode = "compact";
    private static readonly string[] SupportedModes = ["raw", "compact", "guided"];
    private static readonly string[] DefaultSeverityFilter = ["Error", "Warning"];
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public CommandDescriptor Descriptor { get; } = new(
        Id: "diag.get_workspace_snapshot",
        Summary: "Analyze project/workspace-backed diagnostics across a directory, solution, project, or file set (per project, summed).",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: [CommandTrait.PotentiallySlow],
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        string[] filePaths = InputParsing.GetOptionalStringArray(input, "file_paths");
        IReadOnlyDictionary<string, string> overrides = ParseFileOverrides(input, errors);
        string? directoryPath = GetOptionalStringProperty(input, "directory_path");
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);

        if (filePaths.Length == 0 &&
            string.IsNullOrWhiteSpace(directoryPath) &&
            overrides.Count == 0 &&
            string.IsNullOrWhiteSpace(workspacePath) &&
            string.IsNullOrWhiteSpace(workspaceHandle))
        {
            errors.Add(new CommandError(
                "invalid_input",
                "At least one of 'directory_path', 'workspace_path', 'workspace_handle', 'file_paths', or 'file_overrides' must be provided."));
            return errors;
        }

        if (!string.IsNullOrWhiteSpace(directoryPath) &&
            !Directory.Exists(directoryPath) &&
            !(File.Exists(directoryPath) && StaticAnalysisWorkspace.IsMsBuildWorkspaceFile(directoryPath)))
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

        string? rawWorkspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        string? rawDirectoryPath = GetOptionalStringProperty(input, "directory_path");
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: true);

        HashSet<string> severityFilter = ParseSeverityFilter(input);
        HashSet<string> diagnosticIdFilter = InputParsing
            .GetOptionalStringArray(input, "diagnostic_ids")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] additionalGlobalUsings = InputParsing.GetOptionalStringArray(input, "global_usings");
        IReadOnlyDictionary<string, string> overrides = ParseFileOverrides(input, errors: null);
        string[] explicitFilePaths = InputParsing.GetOptionalStringArray(input, "file_paths");

        string? effectiveWorkspacePath = string.IsNullOrWhiteSpace(rawWorkspacePath)
            ? null
            : Path.GetFullPath(rawWorkspacePath);
        string? effectiveDirectoryPath = null;

        if (!string.IsNullOrWhiteSpace(rawDirectoryPath))
        {
            string fullDirOrFile = Path.GetFullPath(rawDirectoryPath);
            if (File.Exists(fullDirOrFile) && StaticAnalysisWorkspace.IsMsBuildWorkspaceFile(fullDirOrFile))
            {
                effectiveWorkspacePath ??= fullDirOrFile;
            }
            else
            {
                effectiveDirectoryPath = NormalizeDirectoryPath(fullDirOrFile);
            }
        }

        if (effectiveDirectoryPath is null &&
            effectiveWorkspacePath is not null &&
            Directory.Exists(effectiveWorkspacePath))
        {
            effectiveDirectoryPath = NormalizeDirectoryPath(effectiveWorkspacePath);
        }

        HashSet<string> explicitFileSet = new(PathComparer);
        int skippedGeneratedFiles = 0;
        foreach (string explicitPath in explicitFilePaths)
        {
            string fullPath = Path.GetFullPath(explicitPath);
            if (!includeGenerated && IsGeneratedPath(fullPath))
            {
                skippedGeneratedFiles++;
                continue;
            }

            explicitFileSet.Add(fullPath);
        }

        foreach (string overridePath in overrides.Keys)
        {
            if (!includeGenerated && IsGeneratedPath(overridePath))
            {
                skippedGeneratedFiles++;
                continue;
            }

            explicitFileSet.Add(overridePath);
        }

        Stopwatch loadTimer = Stopwatch.StartNew();
        ResolvedSnapshotWorkspace? resolved = await ResolveSnapshotWorkspaceAsync(
                effectiveWorkspacePath,
                effectiveDirectoryPath,
                workspaceHandle,
                explicitFileSet,
                overrides,
                recursive,
                includeGenerated,
                cancellationToken)
            .ConfigureAwait(false);
        loadTimer.Stop();

        if (resolved is null || resolved.Solution is null)
        {
            if (!requireWorkspace)
            {
                GetSolutionSnapshotCommand fallback = new();
                return await fallback.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
            }

            string reason = resolved?.FailureReason
                ?? "Workspace context could not be resolved for snapshot diagnostics. Pass workspace_path (.sln/.slnx/.csproj/.vbproj or directory) and retry.";
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("workspace_required", reason) });
        }

        Solution workingSolution = ApplyFileOverrides(resolved.Solution, overrides);
        List<ProjectSelection> selectedProjects = SelectTargetProjects(
            workingSolution,
            effectiveWorkspacePath,
            effectiveDirectoryPath,
            explicitFileSet,
            recursive);

        if (selectedProjects.Count == 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "No MSBuild projects matched the requested snapshot scope.") });
        }

        List<NormalizedDiagnostic> normalizedAllList = new();
        List<NormalizedDiagnostic> filteredDiagnostics = new();
        List<SnapshotProjectResult> projectResults = new();
        HashSet<string> analyzedFilePaths = new(PathComparer);
        List<string> aggregatedProjectIssues = new();
        HashSet<string> seenProjectIssues = new(StringComparer.Ordinal);
        int unresolvedSelectedProjectCount = 0;
        int totalCandidateFiles = 0;

        foreach (ProjectSelection selection in selectedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Project project = selection.Project;
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            IReadOnlyList<string> projectIssues = WorkspaceProjectResolution.ResolveProjectIssues(
                resolved.UnresolvedProjects,
                project,
                compilation);
            bool projectResolved = projectIssues.Count == 0;
            if (!projectResolved)
            {
                unresolvedSelectedProjectCount++;
                foreach (string issue in projectIssues)
                {
                    if (seenProjectIssues.Add(issue))
                    {
                        aggregatedProjectIssues.Add(issue);
                    }
                }
            }

            HashSet<string> projectTargetFiles = CollectProjectTargetFiles(
                project,
                selection.WholeProject,
                effectiveDirectoryPath,
                explicitFileSet,
                recursive,
                includeGenerated,
                ref skippedGeneratedFiles);

            totalCandidateFiles += projectTargetFiles.Count;
            foreach (string path in projectTargetFiles)
            {
                analyzedFilePaths.Add(path);
            }

            bool includeProjectLevelDiagnostics = selection.WholeProject && explicitFileSet.Count == 0;
            List<Diagnostic> projectRawDiagnostics = new();
            foreach (Diagnostic diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.IsSuppressed)
                {
                    continue;
                }

                if (IsDiagnosticInProjectScope(
                        diagnostic,
                        selection.WholeProject,
                        includeProjectLevelDiagnostics,
                        includeGenerated,
                        projectTargetFiles))
                {
                    projectRawDiagnostics.Add(diagnostic);
                }
            }

            NormalizedDiagnostic[] projectNormalized = CompilationDiagnostics.Normalize(projectRawDiagnostics);
            normalizedAllList.AddRange(projectNormalized);

            List<NormalizedDiagnostic> projectFiltered = projectNormalized
                .Where(d => severityFilter.Contains(NormalizeSeverity(d.severity)))
                .Where(d => diagnosticIdFilter.Count == 0 || diagnosticIdFilter.Contains(d.id))
                .ToList();
            filteredDiagnostics.AddRange(projectFiltered);

            SeverityCounts projectCounts = CountBySeverity(projectFiltered);
            string[] projectTopIds = projectFiltered
                .GroupBy(d => d.id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(g => g.Key)
                .ToArray();

            projectResults.Add(new SnapshotProjectResult(
                project_name: project.Name,
                project_path: project.FilePath,
                project_resolved: projectResolved,
                project_resolution_issues: projectIssues,
                analyzed_files: projectTargetFiles.Count,
                total: projectFiltered.Count,
                errors: projectCounts.Errors,
                warnings: projectCounts.Warnings,
                info: projectCounts.Info,
                hidden: projectCounts.Hidden,
                top_diagnostic_ids: projectTopIds));
        }

        if (projectResults.Count == 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "No C# or VB compilations could be created for the requested snapshot scope.") });
        }

        NormalizedDiagnostic[] normalizedAll = normalizedAllList.ToArray();
        filteredDiagnostics = filteredDiagnostics
            .OrderByDescending(d => GetSeverityRank(d.severity))
            .ThenBy(d => d.file_path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.line)
            .ThenBy(d => d.column)
            .ThenBy(d => d.id, StringComparer.Ordinal)
            .ToList();

        NormalizedDiagnostic[] returnedDiagnostics = filteredDiagnostics.Take(maxDiagnostics).ToArray();
        SeverityCounts filteredCounts = CountBySeverity(filteredDiagnostics);
        SeverityCounts unfilteredCounts = CountBySeverity(normalizedAll);

        Dictionary<string, List<NormalizedDiagnostic>> diagnosticsByFile = filteredDiagnostics
            .GroupBy(d => string.IsNullOrWhiteSpace(d.file_path) ? "<global>" : Path.GetFullPath(d.file_path), PathComparer)
            .ToDictionary(g => g.Key, g => g.ToList(), PathComparer);

        foreach (string diagFile in diagnosticsByFile.Keys)
        {
            if (!string.Equals(diagFile, "<global>", StringComparison.OrdinalIgnoreCase))
            {
                analyzedFilePaths.Add(diagFile);
            }
        }

        string[] orderedAnalyzedFiles = analyzedFilePaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<SnapshotFileResult> fileResults = new();
        foreach (string filePath in orderedAnalyzedFiles)
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

        if (diagnosticsByFile.TryGetValue("<global>", out List<NormalizedDiagnostic>? globalDiagnostics) &&
            globalDiagnostics.Count > 0)
        {
            SeverityCounts globalCounts = CountBySeverity(globalDiagnostics);
            fileResults.Add(new SnapshotFileResult(
                file_path: "<global>",
                total: globalDiagnostics.Count,
                errors: globalCounts.Errors,
                warnings: globalCounts.Warnings,
                info: globalCounts.Info,
                hidden: globalCounts.Hidden,
                top_diagnostic_ids: globalDiagnostics
                    .GroupBy(d => d.id, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .Select(g => g.Key)
                    .ToArray(),
                diagnostics: includePerFileDiagnostics
                    ? globalDiagnostics.Take(maxDiagnosticsPerFile).ToArray()
                    : Array.Empty<NormalizedDiagnostic>()));
        }

        SnapshotFileResult[] files = fileResults
            .OrderByDescending(f => f.errors)
            .ThenByDescending(f => f.warnings)
            .ThenByDescending(f => f.total)
            .ThenBy(f => f.file_path, StringComparer.OrdinalIgnoreCase)
            .Take(maxFilesInOutput)
            .ToArray();

        SnapshotProjectResult[] orderedProjects = projectResults
            .OrderByDescending(p => p.errors)
            .ThenByDescending(p => p.warnings)
            .ThenByDescending(p => p.total)
            .ThenBy(p => p.project_name, StringComparer.OrdinalIgnoreCase)
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

        bool allSelectedProjectsResolved = unresolvedSelectedProjectCount == 0;
        int projectsWithErrors = orderedProjects.Count(p => p.errors > 0);
        int totalSolutionProjects = workingSolution.Projects.Count();
        int totalSolutionDocuments = workingSolution.Projects.Sum(p => p.Documents.Count());
        int totalUnresolvedInSolution = Math.Max(unresolvedSelectedProjectCount, resolved.UnresolvedProjects.Count);

        WorkspaceContextInfo workspaceContext = new(
            mode: "workspace",
            resolution_source: resolved.ResolutionSource,
            requested_workspace_path: rawWorkspacePath ?? rawDirectoryPath,
            resolved_workspace_path: resolved.ResolvedWorkspacePath,
            project_path: orderedProjects.Length == 1 ? orderedProjects[0].project_path : null,
            fallback_reason: null,
            attempted_workspace_paths: resolved.AttemptedWorkspacePaths,
            workspace_diagnostics: resolved.WorkspaceDiagnostics,
            workspace_load_duration_ms: (int)loadTimer.ElapsedMilliseconds,
            msbuild_registration_duration_ms: 0,
            workspace_cache_mode: resolved.CacheMode,
            workspace_cache_hit: resolved.CacheHit,
            workspace_kind: resolved.WorkspaceKind,
            project_count: totalSolutionProjects,
            document_count: totalSolutionDocuments,
            workspace_handle: resolved.WorkspaceHandle,
            project_resolved: allSelectedProjectsResolved,
            project_resolution_issues: aggregatedProjectIssues,
            unresolved_project_count: totalUnresolvedInSolution);

        string diagnosticsNote = !allSelectedProjectsResolved
            ? $"{unresolvedSelectedProjectCount} of {orderedProjects.Length} analyzed project(s) are unresolved ({string.Join("; ", aggregatedProjectIssues)}); diagnostics may include false missing-type errors until restored."
            : orderedProjects.Length == 1
                ? $"Diagnostics are project-backed from '{orderedProjects[0].project_name}' in the resolved workspace."
                : $"Diagnostics are project-backed and summed across {orderedProjects.Length} project(s) in the resolved workspace.";

        object summary = new
        {
            analyzed_projects = orderedProjects.Length,
            projects_with_errors = projectsWithErrors,
            analyzed_files = orderedAnalyzedFiles.Length,
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
                projects_returned = orderedProjects.Length,
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
        data["workspace_path"] = rawWorkspacePath;
        data["require_workspace"] = requireWorkspace;
        data["workspace_context"] = WorkspaceContextPayload.Build(workspaceContext);
        data["diagnostics_context"] = new
        {
            mode = "workspace",
            project_resolved = allSelectedProjectsResolved,
            analyzed_projects = orderedProjects.Length,
            unresolved_projects = unresolvedSelectedProjectCount,
            note = diagnosticsNote,
        };
        data["analyzed_projects"] = orderedProjects.Length;
        data["projects_with_errors"] = projectsWithErrors;
        data["total_files"] = orderedAnalyzedFiles.Length;
        data["total_diagnostics"] = filteredDiagnostics.Count;
        data["returned_diagnostics"] = returnedDiagnostics.Length;
        data["errors"] = filteredCounts.Errors;
        data["warnings"] = filteredCounts.Warnings;
        data["info"] = filteredCounts.Info;
        data["hidden"] = filteredCounts.Hidden;
        data["projects"] = orderedProjects;

        if (includeQuery)
        {
            data["query"] = new
            {
                file_paths = explicitFilePaths,
                directory_path = rawDirectoryPath,
                workspace_path = rawWorkspacePath,
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
                total_candidate_files = totalCandidateFiles,
                analyzed_files = orderedAnalyzedFiles.Length,
                analyzed_projects = orderedProjects.Length,
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

    private static async Task<ResolvedSnapshotWorkspace?> ResolveSnapshotWorkspaceAsync(
        string? effectiveWorkspacePath,
        string? effectiveDirectoryPath,
        string? workspaceHandle,
        HashSet<string> explicitFileSet,
        IReadOnlyDictionary<string, string> overrides,
        bool recursive,
        bool includeGenerated,
        CancellationToken cancellationToken)
    {
        List<string> attemptedPaths = new();

        // 1. Reuse the hot workspace when it covers the requested scope.
        if (!string.IsNullOrWhiteSpace(workspaceHandle) &&
            WorkspaceHostStoreProvider.Current.TryGet(workspaceHandle.Trim(), out HostedWorkspace? hosted) &&
            hosted?.Workspace.Solution is not null)
        {
            StaticAnalysisWorkspace hotWorkspace = hosted.Workspace;
            if (CanHotWorkspaceSatisfyRequest(hotWorkspace, effectiveWorkspacePath, effectiveDirectoryPath, explicitFileSet, recursive))
            {
                return new ResolvedSnapshotWorkspace(
                    Solution: hotWorkspace.Solution,
                    UnresolvedProjects: hotWorkspace.UnresolvedProjects,
                    WorkspaceDiagnostics: hotWorkspace.WorkspaceDiagnostics,
                    ResolvedWorkspacePath: hotWorkspace.ResolvedWorkspacePath,
                    WorkspaceKind: hotWorkspace.WorkspaceKind,
                    ResolutionSource: "workspace_handle",
                    CacheMode: "process_hot",
                    CacheHit: true,
                    WorkspaceHandle: hosted.Handle,
                    AttemptedWorkspacePaths: new[] { hotWorkspace.ResolvedWorkspacePath },
                    FailureReason: null);
            }
        }

        // 2. Explicit MSBuild workspace file (.sln / .slnx / .csproj / .vbproj).
        if (!string.IsNullOrWhiteSpace(effectiveWorkspacePath) &&
            File.Exists(effectiveWorkspacePath) &&
            StaticAnalysisWorkspace.IsMsBuildWorkspaceFile(effectiveWorkspacePath))
        {
            attemptedPaths.Add(effectiveWorkspacePath);
            (StaticAnalysisWorkspace? loaded, CommandError? error) = await StaticAnalysisWorkspace
                .LoadAsync(effectiveWorkspacePath, includeGenerated, maxFiles: 100_000, cancellationToken)
                .ConfigureAwait(false);
            if (loaded?.Solution is not null)
            {
                return FromStaticAnalysisWorkspace(loaded, "explicit_workspace_path", attemptedPaths);
            }

            return ResolvedSnapshotWorkspace.Failed(error?.Message ?? $"Failed to load workspace '{effectiveWorkspacePath}'.");
        }

        // 3. Directory scope: prefer local .sln/.slnx, then local single .csproj/.vbproj, then ancestor .sln/.slnx,
        //    then all .csproj/.vbproj files discovered under the directory.
        if (!string.IsNullOrWhiteSpace(effectiveDirectoryPath) && Directory.Exists(effectiveDirectoryPath))
        {
            string[] topSolutions = EnumerateSolutionFiles(effectiveDirectoryPath);
            if (topSolutions.Length > 0)
            {
                attemptedPaths.Add(topSolutions[0]);
                (StaticAnalysisWorkspace? loaded, CommandError? error) = await StaticAnalysisWorkspace
                    .LoadAsync(topSolutions[0], includeGenerated, maxFiles: 100_000, cancellationToken)
                    .ConfigureAwait(false);
                if (loaded?.Solution is not null)
                {
                    return FromStaticAnalysisWorkspace(loaded, "directory_solution", attemptedPaths);
                }

                return ResolvedSnapshotWorkspace.Failed(error?.Message ?? $"Failed to load solution '{topSolutions[0]}'.");
            }

            string[] topProjects = EnumerateProjectFiles(effectiveDirectoryPath, SearchOption.TopDirectoryOnly);
            if (topProjects.Length == 1)
            {
                attemptedPaths.Add(topProjects[0]);
                (StaticAnalysisWorkspace? loaded, CommandError? error) = await StaticAnalysisWorkspace
                    .LoadAsync(topProjects[0], includeGenerated, maxFiles: 100_000, cancellationToken)
                    .ConfigureAwait(false);
                if (loaded?.Solution is not null)
                {
                    return FromStaticAnalysisWorkspace(loaded, "directory_project", attemptedPaths);
                }

                return ResolvedSnapshotWorkspace.Failed(error?.Message ?? $"Failed to load project '{topProjects[0]}'.");
            }

            if (topProjects.Length > 1)
            {
                attemptedPaths.AddRange(topProjects);
                (StaticAnalysisWorkspace? loaded, CommandError? error) = await StaticAnalysisWorkspace
                    .LoadProjectsAsync(effectiveDirectoryPath, topProjects, includeGenerated, maxFiles: 100_000, cancellationToken)
                    .ConfigureAwait(false);
                if (loaded?.Solution is not null)
                {
                    return FromStaticAnalysisWorkspace(loaded, "directory_projects", attemptedPaths);
                }

                return ResolvedSnapshotWorkspace.Failed(error?.Message ?? $"Failed to load projects in '{effectiveDirectoryPath}'.");
            }

            // No solution or project directly in this directory (e.g., "src" or a subfolder inside a project).
            // First check ancestor directories for a solution that covers this directory.
            string? ancestorSolution = FindAncestorSolution(effectiveDirectoryPath);
            if (!string.IsNullOrWhiteSpace(ancestorSolution))
            {
                attemptedPaths.Add(ancestorSolution);
                (StaticAnalysisWorkspace? loaded, _) = await StaticAnalysisWorkspace
                    .LoadAsync(ancestorSolution, includeGenerated, maxFiles: 100_000, cancellationToken)
                    .ConfigureAwait(false);
                if (loaded?.Solution is not null &&
                    SelectTargetProjects(loaded.Solution, effectiveWorkspacePath: null, effectiveDirectoryPath, explicitFileSet, recursive).Count > 0)
                {
                    return FromStaticAnalysisWorkspace(loaded, "ancestor_solution", attemptedPaths);
                }
            }

            // Next check for any projects discovered beneath this directory.
            string[] nestedProjects = recursive
                ? EnumerateProjectFiles(effectiveDirectoryPath, SearchOption.AllDirectories)
                : Array.Empty<string>();
            if (nestedProjects.Length > 0)
            {
                attemptedPaths.AddRange(nestedProjects);
                (StaticAnalysisWorkspace? loaded, CommandError? error) = await StaticAnalysisWorkspace
                    .LoadProjectsAsync(effectiveDirectoryPath, nestedProjects, includeGenerated, maxFiles: 100_000, cancellationToken)
                    .ConfigureAwait(false);
                if (loaded?.Solution is not null)
                {
                    return FromStaticAnalysisWorkspace(loaded, "directory_projects", attemptedPaths);
                }

                return ResolvedSnapshotWorkspace.Failed(error?.Message ?? $"Failed to load projects under '{effectiveDirectoryPath}'.");
            }

            // Finally check if this directory is a subfolder inside an ancestor .csproj/.vbproj.
            string? ancestorProject = FindAncestorProject(effectiveDirectoryPath);
            if (!string.IsNullOrWhiteSpace(ancestorProject))
            {
                attemptedPaths.Add(ancestorProject);
                (StaticAnalysisWorkspace? loaded, CommandError? error) = await StaticAnalysisWorkspace
                    .LoadAsync(ancestorProject, includeGenerated, maxFiles: 100_000, cancellationToken)
                    .ConfigureAwait(false);
                if (loaded?.Solution is not null)
                {
                    return FromStaticAnalysisWorkspace(loaded, "ancestor_project", attemptedPaths);
                }

                return ResolvedSnapshotWorkspace.Failed(error?.Message ?? $"Failed to load project '{ancestorProject}'.");
            }

            return ResolvedSnapshotWorkspace.Failed($"No solution or MSBuild project was found for directory '{effectiveDirectoryPath}'.");
        }

        // 4. File-set only scope (no directory_path or workspace_path).
        string[] onDiskFiles = explicitFileSet
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (onDiskFiles.Length == 0)
        {
            return ResolvedSnapshotWorkspace.Failed("Workspace-backed snapshot requires at least one on-disk file or workspace_path to resolve MSBuild projects.");
        }

        HashSet<string> inferredProjects = new(PathComparer);
        foreach (string filePath in onDiskFiles)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string? proj = FindAncestorProject(dir);
                if (!string.IsNullOrWhiteSpace(proj))
                {
                    inferredProjects.Add(proj);
                }
            }
        }

        if (inferredProjects.Count > 1)
        {
            string? commonAnchorDir = Path.GetDirectoryName(onDiskFiles[0]);
            string? ancestorSolution = !string.IsNullOrWhiteSpace(commonAnchorDir)
                ? FindAncestorSolution(commonAnchorDir)
                : null;
            if (!string.IsNullOrWhiteSpace(ancestorSolution))
            {
                attemptedPaths.Add(ancestorSolution);
                (StaticAnalysisWorkspace? loaded, _) = await StaticAnalysisWorkspace
                    .LoadAsync(ancestorSolution, includeGenerated, maxFiles: 100_000, cancellationToken)
                    .ConfigureAwait(false);
                if (loaded?.Solution is not null)
                {
                    return FromStaticAnalysisWorkspace(loaded, "ancestor_solution", attemptedPaths);
                }
            }

            string[] projectList = inferredProjects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            attemptedPaths.AddRange(projectList);
            (StaticAnalysisWorkspace? multiLoaded, CommandError? multiError) = await StaticAnalysisWorkspace
                .LoadProjectsAsync(Path.GetDirectoryName(projectList[0]) ?? Environment.CurrentDirectory, projectList, includeGenerated, maxFiles: 100_000, cancellationToken)
                .ConfigureAwait(false);
            if (multiLoaded?.Solution is not null)
            {
                return FromStaticAnalysisWorkspace(multiLoaded, "inferred_projects", attemptedPaths);
            }

            return ResolvedSnapshotWorkspace.Failed(multiError?.Message ?? "Failed to load inferred projects for requested files.");
        }

        CommandFileAnalysis anchorAnalysis = await CommandFileAnalysis
            .LoadAsync(onDiskFiles[0], cancellationToken, workspacePath: null, workspaceHandle: null)
            .ConfigureAwait(false);
        if (anchorAnalysis.Document?.Project.Solution is null ||
            !string.Equals(anchorAnalysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvedSnapshotWorkspace.Failed(
                anchorAnalysis.WorkspaceContext.fallback_reason ??
                $"Workspace context could not be resolved for '{onDiskFiles[0]}'.");
        }

        Solution anchorSolution = anchorAnalysis.Document.Project.Solution;
        IReadOnlyList<UnresolvedProjectInfo> anchorUnresolved = WorkspaceProjectResolution.InspectSolution(anchorSolution);
        return new ResolvedSnapshotWorkspace(
            Solution: anchorSolution,
            UnresolvedProjects: anchorUnresolved,
            WorkspaceDiagnostics: anchorAnalysis.WorkspaceContext.workspace_diagnostics,
            ResolvedWorkspacePath: anchorAnalysis.WorkspaceContext.resolved_workspace_path,
            WorkspaceKind: anchorAnalysis.WorkspaceContext.workspace_kind,
            ResolutionSource: anchorAnalysis.WorkspaceContext.resolution_source,
            CacheMode: anchorAnalysis.WorkspaceContext.workspace_cache_mode,
            CacheHit: anchorAnalysis.WorkspaceContext.workspace_cache_hit,
            WorkspaceHandle: anchorAnalysis.WorkspaceContext.workspace_handle,
            AttemptedWorkspacePaths: anchorAnalysis.WorkspaceContext.attempted_workspace_paths,
            FailureReason: null);
    }

    private static bool CanHotWorkspaceSatisfyRequest(
        StaticAnalysisWorkspace hotWorkspace,
        string? effectiveWorkspacePath,
        string? effectiveDirectoryPath,
        HashSet<string> explicitFileSet,
        bool recursive)
    {
        if (hotWorkspace.Solution is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(effectiveWorkspacePath) &&
            File.Exists(effectiveWorkspacePath) &&
            StaticAnalysisWorkspace.IsMsBuildWorkspaceFile(effectiveWorkspacePath))
        {
            string ext = Path.GetExtension(effectiveWorkspacePath);
            if (string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return PathComparer.Equals(Path.GetFullPath(effectiveWorkspacePath), Path.GetFullPath(hotWorkspace.ResolvedWorkspacePath));
            }

            return hotWorkspace.Solution.Projects.Any(project =>
                !string.IsNullOrWhiteSpace(project.FilePath) &&
                PathComparer.Equals(Path.GetFullPath(project.FilePath), effectiveWorkspacePath));
        }

        if (!string.IsNullOrWhiteSpace(effectiveDirectoryPath))
        {
            // If the hot workspace is only a single project, don't let it shadow a multi-project parent directory.
            if (string.Equals(hotWorkspace.WorkspaceKind, "project", StringComparison.OrdinalIgnoreCase))
            {
                string hotRoot = NormalizeDirectoryPath(hotWorkspace.RootDirectory);
                if (!IsPathUnderDirectory(effectiveDirectoryPath, hotRoot, recursive: true))
                {
                    return false;
                }
            }
            else
            {
                string hotRoot = NormalizeDirectoryPath(hotWorkspace.RootDirectory);
                if (!IsPathUnderDirectory(effectiveDirectoryPath, hotRoot, recursive: true))
                {
                    return false;
                }
            }

            return SelectTargetProjects(hotWorkspace.Solution, effectiveWorkspacePath: null, effectiveDirectoryPath, explicitFileSet, recursive).Count > 0;
        }

        if (explicitFileSet.Count > 0)
        {
            return SelectTargetProjects(hotWorkspace.Solution, effectiveWorkspacePath: null, effectiveDirectoryPath: null, explicitFileSet, recursive).Count > 0;
        }

        return true;
    }

    private static ResolvedSnapshotWorkspace FromStaticAnalysisWorkspace(
        StaticAnalysisWorkspace workspace,
        string resolutionSource,
        IReadOnlyList<string> attemptedPaths)
    {
        return new ResolvedSnapshotWorkspace(
            Solution: workspace.Solution,
            UnresolvedProjects: workspace.UnresolvedProjects,
            WorkspaceDiagnostics: workspace.WorkspaceDiagnostics,
            ResolvedWorkspacePath: workspace.ResolvedWorkspacePath,
            WorkspaceKind: workspace.WorkspaceKind,
            ResolutionSource: resolutionSource,
            CacheMode: "none",
            CacheHit: false,
            WorkspaceHandle: null,
            AttemptedWorkspacePaths: attemptedPaths.ToArray(),
            FailureReason: null);
    }

    private static Solution ApplyFileOverrides(Solution solution, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.Count == 0)
        {
            return solution;
        }

        Solution current = solution;
        foreach (KeyValuePair<string, string> entry in overrides)
        {
            SourceText text = SourceText.From(entry.Value);
            bool updatedAny = false;
            foreach (Project project in current.Projects.ToArray())
            {
                foreach (Document document in project.Documents)
                {
                    if (!string.IsNullOrWhiteSpace(document.FilePath) &&
                        PathComparer.Equals(Path.GetFullPath(document.FilePath), entry.Key))
                    {
                        current = current.WithDocumentText(document.Id, text);
                        updatedAny = true;
                    }
                }
            }

            if (!updatedAny)
            {
                IReadOnlyList<Project> containingProjects = StaticAnalysisWorkspace.FindContainingProjectsForNewSourceFile(current, entry.Key);
                foreach (Project project in containingProjects)
                {
                    Project? liveProject = current.GetProject(project.Id);
                    if (liveProject is null)
                    {
                        continue;
                    }

                    Document added = liveProject.AddDocument(Path.GetFileName(entry.Key), text, filePath: entry.Key);
                    current = added.Project.Solution;
                }
            }
        }

        return current;
    }

    private static List<ProjectSelection> SelectTargetProjects(
        Solution solution,
        string? effectiveWorkspacePath,
        string? effectiveDirectoryPath,
        HashSet<string> explicitFileSet,
        bool recursive)
    {
        Project[] allProjects = solution.Projects
            .OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Explicit single project file (.csproj / .vbproj): analyze only that project.
        if (!string.IsNullOrWhiteSpace(effectiveWorkspacePath) &&
            File.Exists(effectiveWorkspacePath) &&
            (effectiveWorkspacePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
             effectiveWorkspacePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)))
        {
            string targetProjectPath = Path.GetFullPath(effectiveWorkspacePath);
            bool wholeProject = explicitFileSet.Count == 0;
            return allProjects
                .Where(project => !string.IsNullOrWhiteSpace(project.FilePath) &&
                                  PathComparer.Equals(Path.GetFullPath(project.FilePath), targetProjectPath))
                .Select(project => new ProjectSelection(project, wholeProject))
                .ToList();
        }

        // Directory scope: cover every project under the directory (or owning files in a subdirectory of a project).
        if (!string.IsNullOrWhiteSpace(effectiveDirectoryPath))
        {
            List<ProjectSelection> selections = new();
            HashSet<ProjectId> selectedIds = new();

            foreach (Project project in allProjects)
            {
                if (string.IsNullOrWhiteSpace(project.FilePath))
                {
                    continue;
                }

                string? projectDir = Path.GetDirectoryName(Path.GetFullPath(project.FilePath));
                if (!string.IsNullOrWhiteSpace(projectDir) &&
                    IsPathUnderDirectory(NormalizeDirectoryPath(projectDir), effectiveDirectoryPath, recursive))
                {
                    // If explicit files were also supplied, narrow diagnostics to those files + directory.
                    bool wholeProject = explicitFileSet.Count == 0 && recursive;
                    selections.Add(new ProjectSelection(project, wholeProject));
                    selectedIds.Add(project.Id);
                }
            }

            // Handle subdirectories inside a project (e.g., src/MyProject/Subfolder) or explicit files in other projects.
            foreach (Project project in allProjects)
            {
                if (selectedIds.Contains(project.Id))
                {
                    continue;
                }

                bool hasDocumentInScope = project.Documents.Any(document =>
                {
                    if (string.IsNullOrWhiteSpace(document.FilePath))
                    {
                        return false;
                    }

                    string docFullPath = Path.GetFullPath(document.FilePath);
                    if (explicitFileSet.Contains(docFullPath))
                    {
                        return true;
                    }

                    string? docDir = Path.GetDirectoryName(docFullPath);
                    return !string.IsNullOrWhiteSpace(docDir) &&
                           IsPathUnderDirectory(NormalizeDirectoryPath(docDir), effectiveDirectoryPath, recursive);
                });

                if (hasDocumentInScope)
                {
                    selections.Add(new ProjectSelection(project, WholeProject: false));
                    selectedIds.Add(project.Id);
                }
            }

            return selections;
        }

        // Explicit file list / overrides without directory_path.
        if (explicitFileSet.Count > 0)
        {
            List<ProjectSelection> selections = new();
            foreach (Project project in allProjects)
            {
                bool ownsAny = project.Documents.Any(document =>
                    !string.IsNullOrWhiteSpace(document.FilePath) &&
                    explicitFileSet.Contains(Path.GetFullPath(document.FilePath)));
                if (ownsAny)
                {
                    selections.Add(new ProjectSelection(project, WholeProject: false));
                }
            }

            return selections;
        }

        // Solution file or bare workspace_handle: cover every project in the solution.
        return allProjects
            .Select(project => new ProjectSelection(project, WholeProject: true))
            .ToList();
    }

    private static HashSet<string> CollectProjectTargetFiles(
        Project project,
        bool wholeProject,
        string? effectiveDirectoryPath,
        HashSet<string> explicitFileSet,
        bool recursive,
        bool includeGenerated,
        ref int skippedGeneratedFiles)
    {
        HashSet<string> targetFiles = new(PathComparer);
        foreach (Document document in project.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(document.FilePath);
            if (!CommandLanguageServices.IsSupportedSourceFile(fullPath))
            {
                continue;
            }

            if (!includeGenerated && IsGeneratedPath(fullPath))
            {
                skippedGeneratedFiles++;
                continue;
            }

            if (wholeProject)
            {
                targetFiles.Add(fullPath);
                continue;
            }

            bool inExplicit = explicitFileSet.Contains(fullPath);
            bool inDirectory = false;
            if (!string.IsNullOrWhiteSpace(effectiveDirectoryPath))
            {
                string? docDir = Path.GetDirectoryName(fullPath);
                inDirectory = !string.IsNullOrWhiteSpace(docDir) &&
                    IsPathUnderDirectory(NormalizeDirectoryPath(docDir), effectiveDirectoryPath, recursive);
            }

            if (inExplicit || inDirectory)
            {
                targetFiles.Add(fullPath);
            }
        }

        return targetFiles;
    }

    private static bool IsDiagnosticInProjectScope(
        Diagnostic diagnostic,
        bool wholeProject,
        bool includeProjectLevelDiagnostics,
        bool includeGenerated,
        HashSet<string> projectTargetFiles)
    {
        if (diagnostic.Location == Location.None || !diagnostic.Location.IsInSource)
        {
            return includeProjectLevelDiagnostics;
        }

        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        string candidatePath = string.IsNullOrWhiteSpace(span.Path)
            ? diagnostic.Location.SourceTree?.FilePath ?? string.Empty
            : span.Path;

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return includeProjectLevelDiagnostics;
        }

        string fullPath = Path.GetFullPath(candidatePath);
        if (!includeGenerated && IsGeneratedPath(fullPath))
        {
            return false;
        }

        if (wholeProject)
        {
            return true;
        }

        return projectTargetFiles.Contains(fullPath);
    }

    private static string[] EnumerateSolutionFiles(string directoryPath)
    {
        return Directory.EnumerateFiles(directoryPath, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directoryPath, "*.slnx", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] EnumerateProjectFiles(string directoryPath, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(directoryPath, "*.csproj", searchOption)
            .Concat(Directory.EnumerateFiles(directoryPath, "*.vbproj", searchOption))
            .Select(Path.GetFullPath)
            .Where(path => !IsIgnoredBuildDirectoryPath(path))
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FindAncestorSolution(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            string[] solutions = EnumerateSolutionFiles(current.FullName);
            if (solutions.Length > 0)
            {
                return solutions[0];
            }

            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                break;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? FindAncestorProject(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            string[] projects = EnumerateProjectFiles(current.FullName, SearchOption.TopDirectoryOnly);
            if (projects.Length > 0)
            {
                return projects[0];
            }

            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                break;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsPathUnderDirectory(string candidateDirectory, string rootDirectory, bool recursive)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidateDirectory, rootDirectory, comparison))
        {
            return true;
        }

        if (!recursive)
        {
            return false;
        }

        string prefix = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        return candidateDirectory.StartsWith(prefix, comparison);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (fullPath.Length > root.Length)
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return fullPath;
    }

    private static bool IsIgnoredBuildDirectoryPath(string filePath)
    {
        string[] segments = filePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetOptionalStringProperty(JsonElement input, string propertyName)
    {
        if (input.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            string? value = property.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    private sealed record ProjectSelection(Project Project, bool WholeProject);

    private sealed record ResolvedSnapshotWorkspace(
        Solution? Solution,
        IReadOnlyList<UnresolvedProjectInfo> UnresolvedProjects,
        IReadOnlyList<string> WorkspaceDiagnostics,
        string? ResolvedWorkspacePath,
        string? WorkspaceKind,
        string ResolutionSource,
        string CacheMode,
        bool CacheHit,
        string? WorkspaceHandle,
        IReadOnlyList<string> AttemptedWorkspacePaths,
        string? FailureReason)
    {
        public static ResolvedSnapshotWorkspace Failed(string reason)
            => new(null, Array.Empty<UnresolvedProjectInfo>(), Array.Empty<string>(), null, null, "failed", "none", false, null, Array.Empty<string>(), reason);
    }

    private sealed record SnapshotProjectResult(
        string project_name,
        string? project_path,
        bool project_resolved,
        IReadOnlyList<string> project_resolution_issues,
        int analyzed_files,
        int total,
        int errors,
        int warnings,
        int info,
        int hidden,
        IReadOnlyList<string> top_diagnostic_ids);

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
            return new Dictionary<string, string>(PathComparer);
        }

        Dictionary<string, string> overrides = new(PathComparer);
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

        return IsIgnoredBuildDirectoryPath(filePath);
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
