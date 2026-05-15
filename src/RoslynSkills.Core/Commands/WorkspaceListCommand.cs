using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class WorkspaceListCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "workspace.list",
        Summary: "List hot workspace handles loaded in the current process.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        _ = input;
        return Array.Empty<CommandError>();
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        _ = input;
        _ = cancellationToken;
        IWorkspaceHostStore workspaceStore = WorkspaceHostStoreProvider.Current;
        object[] workspaces = workspaceStore.List()
            .Select(hosted =>
            {
                WorkspaceStatus status = workspaceStore.BuildStatus(hosted);
                return new
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
                };
            })
            .Cast<object>()
            .ToArray();

        object data = new
        {
            total = workspaces.Length,
            workspaces,
            store_workspace_count = workspaceStore.Count,
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
