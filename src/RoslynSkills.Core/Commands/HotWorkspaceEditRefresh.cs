using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

internal static class HotWorkspaceEditRefresh
{
    public static async Task<object> RefreshAfterWriteAsync(
        string filePath,
        bool wroteFile,
        CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        if (!wroteFile)
        {
            return new
            {
                attempted = false,
                reason = "file_not_written",
                matched_workspace_count = 0,
                refreshed_workspace_count = 0,
                refreshes = Array.Empty<object>(),
            };
        }

        IWorkspaceHostStore store = WorkspaceHostStoreProvider.Current;
        HostedWorkspace[] matches = store.List()
            .Where(workspace => workspace.TrackedSourcePaths.Contains(normalizedPath, PathComparer))
            .ToArray();
        List<object> refreshes = new();
        foreach (HostedWorkspace workspace in matches)
        {
            WorkspaceStatus statusBefore = store.BuildStatus(workspace);
            WorkspaceRefreshResult result = await store.ApplyIncrementalSourceRefreshForPathsAsync(
                    workspace.Handle,
                    statusBefore,
                    new[] { normalizedPath },
                    cancellationToken)
                .ConfigureAwait(false);

            refreshes.Add(new
            {
                workspace_handle = workspace.Handle,
                applied = result.Applied,
                refresh_action = result.RefreshAction,
                updated_paths = result.UpdatedPaths,
                dirty_before = result.StatusBefore.Dirty,
                dirty_after = result.StatusAfter.Dirty,
                errors = result.Error is null
                    ? Array.Empty<CommandError>()
                    : new[] { result.Error },
            });
        }

        return new
        {
            attempted = true,
            reason = matches.Length == 0 ? "no_matching_hot_workspace" : string.Empty,
            matched_workspace_count = matches.Length,
            refreshed_workspace_count = refreshes.Count,
            refreshes = refreshes.ToArray(),
        };
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
