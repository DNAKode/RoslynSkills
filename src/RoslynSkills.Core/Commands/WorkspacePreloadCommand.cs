using RoslynSkills.Contracts;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class WorkspacePreloadCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "workspace.preload",
        Summary: "Load a hot workspace from a .sln/.slnx solution path, or an explicit project path for project-scoped work.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "workspace_path", errors, out _);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);
        InputParsing.ValidateOptionalBool(input, "require_solution", errors);
        InputParsing.ValidateOptionalInt(input, "max_files", errors, minValue: 1, maxValue: 100_000);

        if (input.TryGetProperty("mode", out JsonElement modeProperty) &&
            modeProperty.ValueKind == JsonValueKind.String)
        {
            string? mode = modeProperty.GetString();
            if (!string.Equals(mode, "balanced", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "strict", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new CommandError("invalid_input", "Property 'mode' must be 'balanced' or 'strict'."));
            }
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "workspace_path", errors, out string workspacePath))
        {
            return new CommandExecutionResult(null, errors);
        }

        string mode = input.TryGetProperty("mode", out JsonElement modeProperty) &&
                      modeProperty.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(modeProperty.GetString())
            ? modeProperty.GetString()!
            : "balanced";
        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        bool requireSolution = InputParsing.GetOptionalBool(input, "require_solution", defaultValue: false);
        int maxFiles = InputParsing.GetOptionalInt(input, "max_files", defaultValue: 10_000, minValue: 1, maxValue: 100_000);

        Stopwatch stopwatch = Stopwatch.StartNew();
        (StaticAnalysisWorkspace? Workspace, CommandError? Error) loaded = await StaticAnalysisWorkspace.LoadAsync(
                workspacePath,
                includeGenerated,
                maxFiles,
                cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        if (loaded.Error is not null || loaded.Workspace is null)
        {
            return new CommandExecutionResult(null, new[] { loaded.Error ?? new CommandError("workspace_load_failed", "Workspace load failed.") });
        }

        if (requireSolution &&
            !string.Equals(loaded.Workspace.WorkspaceKind, "solution", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(loaded.Workspace.WorkspaceKind, "slnx", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError(
                        "solution_required",
                        $"Workspace preload resolved to '{loaded.Workspace.WorkspaceKind}' at '{loaded.Workspace.ResolvedWorkspacePath}', but require_solution was true."),
                });
        }

        IWorkspaceHostStore workspaceStore = WorkspaceHostStoreProvider.Current;
        HostedWorkspace hosted = workspaceStore.Add(loaded.Workspace, mode, includeGenerated);
        WorkspaceStatus status = workspaceStore.BuildStatus(hosted);

        object data = new
        {
            workspace_handle = hosted.Handle,
            loaded = true,
            cache_mode = "process_hot",
            mode,
            include_generated = includeGenerated,
            require_solution = requireSolution,
            workspace_fingerprint = hosted.WorkspaceFingerprint,
            workspace_load_duration_ms = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            requested_workspace_path = Path.GetFullPath(workspacePath),
            resolved_workspace_path = loaded.Workspace.ResolvedWorkspacePath,
            analysis_mode = loaded.Workspace.AnalysisMode,
            workspace_kind = loaded.Workspace.WorkspaceKind,
            project_scoped = string.Equals(loaded.Workspace.WorkspaceKind, "project", StringComparison.OrdinalIgnoreCase),
            solution_scoped = string.Equals(loaded.Workspace.WorkspaceKind, "solution", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(loaded.Workspace.WorkspaceKind, "slnx", StringComparison.OrdinalIgnoreCase),
            projects_loaded = loaded.Workspace.ProjectCount,
            documents_loaded = loaded.Workspace.DocumentCount,
            tracked_paths = hosted.TrackedPaths.Count,
            dirty = status.Dirty,
            invalidated_paths = status.InvalidatedPaths,
            dirty_kinds = WorkspaceStatusData.BuildDirtyKinds(status),
            dirty_entries = WorkspaceStatusData.BuildDirtyEntries(status),
            can_incrementally_update = status.CanIncrementallyUpdate,
            requires_reload = status.RequiresReload,
            loaded_at_utc = hosted.LoadedAtUtc,
            workspace_diagnostics = loaded.Workspace.WorkspaceDiagnostics,
            store_workspace_count = workspaceStore.Count,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }
}
