using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

internal static class HotWorkspaceEditRefresh
{
    public static Task<object> RefreshAfterWriteAsync(
        string filePath,
        bool wroteFile,
        CancellationToken cancellationToken)
        => RefreshAfterWriteAsync(
            new[] { filePath },
            wroteFile,
            allowAddDocuments: false,
            cancellationToken);

    public static async Task<object> RefreshAfterWriteAsync(
        IReadOnlyList<string> filePaths,
        bool wroteFile,
        bool allowAddDocuments,
        CancellationToken cancellationToken)
    {
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

        string[] normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return new
            {
                attempted = false,
                reason = "no_paths",
                matched_workspace_count = 0,
                refreshed_workspace_count = 0,
                refreshes = Array.Empty<object>(),
            };
        }

        IWorkspaceHostStore store = WorkspaceHostStoreProvider.Current;
        List<(HostedWorkspace Workspace, string[] Paths)> matches = new();
        foreach (HostedWorkspace workspace in store.List())
        {
            string[] matchingPaths = normalizedPaths
                .Where(path => workspace.TrackedSourcePathSet.Contains(path) ||
                               (allowAddDocuments &&
                                workspace.Workspace.Solution is { } solution &&
                                StaticAnalysisWorkspace.FindContainingProjectsForNewSourceFile(
                                    solution,
                                    path,
                                    workspace.IncludeGenerated,
                                    workspace.ProjectSourceRules).Count > 0))
                .ToArray();
            if (matchingPaths.Length > 0)
            {
                matches.Add((workspace, matchingPaths));
            }
        }

        List<object> refreshes = new();
        foreach ((HostedWorkspace workspace, string[] matchingPaths) in matches)
        {
            WorkspaceStatus statusBefore = store.BuildStatus(workspace);
            WorkspaceRefreshResult result = await store.ApplyIncrementalSourceRefreshForPathsAsync(
                    workspace.Handle,
                    statusBefore,
                    matchingPaths,
                    cancellationToken,
                    allowAddDocuments)
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
            reason = matches.Count == 0 ? "no_matching_hot_workspace" : string.Empty,
            matched_workspace_count = matches.Count,
            refreshed_workspace_count = refreshes.Count,
            refreshes = refreshes.ToArray(),
        };
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
