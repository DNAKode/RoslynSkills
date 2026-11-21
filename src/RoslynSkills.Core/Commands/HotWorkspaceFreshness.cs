using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

internal sealed record HotWorkspaceFreshnessResult(
    string WorkspaceHandle,
    string RefreshPolicy,
    string RefreshAction,
    bool DirtyBefore,
    bool DirtyAfter,
    bool RequiresReload,
    IReadOnlyList<string> UpdatedPaths,
    IReadOnlyList<string> InvalidatedPaths,
    CommandError? Error);

/// <summary>
/// Brings a hot workspace up to date with the files on disk before a command reads it, so a query always sees
/// the edits made since the last call (by roscli edit commands, an editor or another agent). The hot workspace
/// otherwise only changed through its own edit commands or an explicit <c>workspace.refresh</c>.
/// </summary>
internal static class HotWorkspaceFreshness
{
    /// <summary>
    /// Applies pending source changes to the workspace named by <paramref name="workspaceHandle"/>.
    /// <c>auto</c>/<c>balanced</c> (default) applies known source-file edits incrementally; <c>strict</c> also reloads the
    /// solution when project or membership changes need it; <c>none</c> leaves the workspace as it is.
    /// Returns null when nothing was checked (policy <c>none</c> or unknown handle).
    /// </summary>
    public static async Task<HotWorkspaceFreshnessResult?> EnsureFreshAsync(
        string workspaceHandle,
        string? refreshPolicy,
        CancellationToken cancellationToken)
    {
        string policy = NormalizePolicy(refreshPolicy);
        if (string.Equals(policy, WorkspaceHostProtocol.RefreshPolicy.None, StringComparison.Ordinal))
        {
            return null;
        }

        IWorkspaceHostStore store = WorkspaceHostStoreProvider.Current;
        if (!store.TryGet(workspaceHandle, out HostedWorkspace? hosted) || hosted is null)
        {
            return null;
        }

        WorkspaceStatus statusBefore = store.BuildStatus(hosted);
        if (!statusBefore.Dirty)
        {
            return new HotWorkspaceFreshnessResult(
                workspaceHandle,
                policy,
                WorkspaceHostProtocol.RefreshAction.None,
                DirtyBefore: false,
                DirtyAfter: false,
                RequiresReload: false,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Error: null);
        }

        WorkspaceRefreshResult result;
        if (statusBefore.RequiresReload &&
            string.Equals(policy, WorkspaceHostProtocol.RefreshPolicy.Strict, StringComparison.Ordinal))
        {
            result = await store.ReloadAsync(workspaceHandle, statusBefore, mode: "strict", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await store.ApplyIncrementalSourceRefreshAsync(workspaceHandle, statusBefore, cancellationToken).ConfigureAwait(false);
        }

        WorkspaceStatus statusAfter = result.StatusAfter;
        return new HotWorkspaceFreshnessResult(
            workspaceHandle,
            policy,
            result.Applied ? result.RefreshAction : WorkspaceHostProtocol.RefreshAction.None,
            DirtyBefore: statusBefore.Dirty,
            DirtyAfter: statusAfter.Dirty,
            RequiresReload: statusAfter.RequiresReload,
            result.UpdatedPaths,
            statusAfter.InvalidatedPaths,
            result.Error);
    }

    private static string NormalizePolicy(string? refreshPolicy)
        => refreshPolicy?.Trim().ToLowerInvariant() switch
        {
            "none" => WorkspaceHostProtocol.RefreshPolicy.None,
            "strict" => WorkspaceHostProtocol.RefreshPolicy.Strict,
            _ => WorkspaceHostProtocol.RefreshPolicy.Auto,
        };
}
