using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class WorkspaceRefreshCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "workspace.refresh",
        Summary: "Classify dirty hot-workspace files without reloading or mutating Roslyn state yet.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "workspace_handle", errors, out _);
        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "workspace_handle", errors, out string handle))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        IWorkspaceHostStore workspaceStore = WorkspaceHostStoreProvider.Current;
        if (!workspaceStore.TryGet(handle, out HostedWorkspace? hosted) || hosted is null)
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("workspace_not_found", $"Workspace handle '{handle}' was not found.") }));
        }

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
            refresh_action = "none",
            requires_reload = status.RequiresReload,
            note = status.Dirty && status.CanIncrementallyUpdate
                ? "Known source files changed. Incremental source refresh is implemented in the next hot-server sequence item."
                : status.Dirty
                ? "Workspace membership or structural files changed. Reload handling is implemented in a later hot-server sequence item."
                : "No tracked file changes detected.",
            mode = hosted.Mode,
            include_generated = hosted.IncludeGenerated,
            workspace_fingerprint = hosted.WorkspaceFingerprint,
            requested_workspace_path = hosted.Workspace.WorkspacePath,
            resolved_workspace_path = hosted.Workspace.ResolvedWorkspacePath,
            analysis_mode = hosted.Workspace.AnalysisMode,
            workspace_kind = hosted.Workspace.WorkspaceKind,
            solution_scoped = string.Equals(hosted.Workspace.WorkspaceKind, "solution", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(hosted.Workspace.WorkspaceKind, "slnx", StringComparison.OrdinalIgnoreCase),
            projects_loaded = hosted.Workspace.ProjectCount,
            documents_loaded = hosted.Workspace.DocumentCount,
            tracked_paths = hosted.TrackedPaths.Count,
            loaded_at_utc = hosted.LoadedAtUtc,
            last_refresh_utc = hosted.LastRefreshUtc,
            store_workspace_count = workspaceStore.Count,
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
