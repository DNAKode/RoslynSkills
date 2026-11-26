using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class WorkspaceStatusCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "workspace.status",
        Summary: "Report hot workspace binding, fingerprint, warmup readiness, overlay index stats, dirty state, and loaded solution/project counts.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "workspace_handle", errors, out _);
        InputParsing.ValidateOptionalBool(input, "wait_warmup", errors);
        InputParsing.ValidateOptionalInt(input, "wait_warmup_timeout_ms", errors, minValue: 100, maxValue: 600_000);
        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "workspace_handle", errors, out string handle))
        {
            return new CommandExecutionResult(null, errors);
        }

        IWorkspaceHostStore workspaceStore = WorkspaceHostStoreProvider.Current;
        if (!workspaceStore.TryGet(handle, out HostedWorkspace? hosted) || hosted is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("workspace_not_found", $"Workspace handle '{handle}' was not found.") });
        }

        bool waitWarmup = InputParsing.GetOptionalBool(input, "wait_warmup", defaultValue: false);
        int waitWarmupTimeoutMs = InputParsing.GetOptionalInt(input, "wait_warmup_timeout_ms", defaultValue: 180_000, minValue: 100, maxValue: 600_000);
        bool warmupTimedOut = false;
        if (waitWarmup)
        {
            bool completed = await HotWorkspaceWarmup
                .WaitForWarmupAsync(hosted.Workspace.Solution, TimeSpan.FromMilliseconds(waitWarmupTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
            warmupTimedOut = !completed;
        }

        WarmupProgressSnapshot warmup = HotWorkspaceWarmup.GetSnapshot(hosted.Workspace.Solution);
        WorkspaceStatus status = workspaceStore.BuildStatus(hosted);
        object data = new
        {
            workspace_handle = hosted.Handle,
            loaded = status.Loaded,
            dirty = status.Dirty,
            invalidated_paths = status.InvalidatedPaths,
            dirty_kinds = WorkspaceStatusData.BuildDirtyKinds(status),
            dirty_entries = WorkspaceStatusData.BuildDirtyEntries(status),
            can_incrementally_update = status.CanIncrementallyUpdate,
            requires_reload = status.RequiresReload,
            mode = hosted.Mode,
            include_generated = hosted.IncludeGenerated,
            warmup_state = warmup.State,
            projects_warmed = warmup.ProjectsWarmed,
            warmup_timed_out = warmupTimedOut,
            warmup = warmup.ToPayload(),
            overlay_index = hosted.OverlayIndex.GetStatsPayload(),
            workspace_fingerprint = hosted.WorkspaceFingerprint,
            requested_workspace_path = hosted.Workspace.WorkspacePath,
            resolved_workspace_path = hosted.Workspace.ResolvedWorkspacePath,
            analysis_mode = hosted.Workspace.AnalysisMode,
            workspace_kind = hosted.Workspace.WorkspaceKind,
            project_scoped = string.Equals(hosted.Workspace.WorkspaceKind, "project", StringComparison.OrdinalIgnoreCase),
            solution_scoped = string.Equals(hosted.Workspace.WorkspaceKind, "solution", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(hosted.Workspace.WorkspaceKind, "slnx", StringComparison.OrdinalIgnoreCase),
            projects_loaded = hosted.Workspace.ProjectCount,
            documents_loaded = hosted.Workspace.DocumentCount,
            tracked_project_count = hosted.Workspace.ProjectCount,
            tracked_document_count = hosted.Workspace.DocumentCount,
            tracked_paths = hosted.TrackedPaths.Count,
            loaded_at_utc = hosted.LoadedAtUtc,
            last_refresh_utc = hosted.LastRefreshUtc,
            unresolved_project_count = hosted.Workspace.UnresolvedProjects.Count,
            unresolved_projects = hosted.Workspace.UnresolvedProjects,
            workspace_diagnostics = hosted.Workspace.WorkspaceDiagnostics,
            store_workspace_count = workspaceStore.Count,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }
}
