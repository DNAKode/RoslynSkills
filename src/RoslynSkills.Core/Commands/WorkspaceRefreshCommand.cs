using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class WorkspaceRefreshCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "workspace.refresh",
        Summary: "Refresh known dirty source files incrementally and classify reload-required hot-workspace changes.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "workspace_handle", errors, out _);
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

        WorkspaceStatus statusBefore = workspaceStore.BuildStatus(hosted);
        WorkspaceRefreshResult? refreshResult = null;
        WorkspaceStatus statusAfter = statusBefore;
        if (statusBefore.CanIncrementallyUpdate)
        {
            refreshResult = await workspaceStore.ApplyIncrementalSourceRefreshAsync(
                    hosted.Handle,
                    statusBefore,
                    cancellationToken)
                .ConfigureAwait(false);
            if (refreshResult.Error is not null)
            {
                return new CommandExecutionResult(null, new[] { refreshResult.Error });
            }

            statusAfter = refreshResult.StatusAfter;
        }

        string refreshAction = refreshResult?.RefreshAction ??
                               (statusBefore.RequiresReload ? "reload_required" : "none");
        HostedWorkspace displayHosted = workspaceStore.TryGet(handle, out HostedWorkspace? latestHosted) && latestHosted is not null
            ? latestHosted
            : hosted;
        object data = new
        {
            workspace_handle = displayHosted.Handle,
            loaded = statusAfter.Loaded,
            dirty = statusAfter.Dirty,
            dirty_before = statusBefore.Dirty,
            dirty_after = statusAfter.Dirty,
            invalidated_paths = statusAfter.InvalidatedPaths,
            invalidated_paths_before = statusBefore.InvalidatedPaths,
            dirty_kinds = WorkspaceStatusData.BuildDirtyKinds(statusAfter),
            dirty_entries = WorkspaceStatusData.BuildDirtyEntries(statusAfter),
            dirty_entries_before = WorkspaceStatusData.BuildDirtyEntries(statusBefore),
            can_incrementally_update = statusAfter.CanIncrementallyUpdate,
            refresh_action = refreshAction,
            updated_paths = refreshResult?.UpdatedPaths ?? Array.Empty<string>(),
            requires_reload = statusAfter.RequiresReload,
            note = string.Equals(refreshAction, "incremental_document_update", StringComparison.Ordinal)
                ? "Known source file changes were applied to the hot Roslyn solution."
                : statusAfter.Dirty
                ? "Workspace membership or structural files changed. Reload handling is implemented in a later hot-server sequence item."
                : "No tracked file changes detected.",
            mode = displayHosted.Mode,
            include_generated = displayHosted.IncludeGenerated,
            workspace_fingerprint = displayHosted.WorkspaceFingerprint,
            requested_workspace_path = displayHosted.Workspace.WorkspacePath,
            resolved_workspace_path = displayHosted.Workspace.ResolvedWorkspacePath,
            analysis_mode = displayHosted.Workspace.AnalysisMode,
            workspace_kind = displayHosted.Workspace.WorkspaceKind,
            solution_scoped = string.Equals(displayHosted.Workspace.WorkspaceKind, "solution", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(displayHosted.Workspace.WorkspaceKind, "slnx", StringComparison.OrdinalIgnoreCase),
            projects_loaded = displayHosted.Workspace.ProjectCount,
            documents_loaded = displayHosted.Workspace.DocumentCount,
            tracked_paths = displayHosted.TrackedPaths.Count,
            loaded_at_utc = displayHosted.LoadedAtUtc,
            last_refresh_utc = displayHosted.LastRefreshUtc,
            store_workspace_count = workspaceStore.Count,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }
}
