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
    CommandError? Error,
    string? Mode = null);

/// <summary>Tuning for the zero-stat freshness fast path.</summary>
internal static class HotWorkspaceFreshnessSettings
{
    public const string WatcherFastPathMode = "watcher_fast_path";
    public const string FullScanMode = "full_scan";

    private const int DefaultSafetySweepSeconds = 15;

    /// <summary>How long a call waits for the watcher to prove it is caught up before it falls back to a full scan.</summary>
    public static TimeSpan BarrierTimeout { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// <c>ROSLYNSKILLS_FRESHNESS=sweep</c> (or <c>full_scan</c>) restores the pre-call stat sweep on every call.
    /// </summary>
    public static bool AlwaysFullScan { get; } = IsSweepRequested(Environment.GetEnvironmentVariable("ROSLYNSKILLS_FRESHNESS"));

    /// <summary>
    /// Longest time the host trusts the file watcher without a full disk reconciliation, a backstop against change
    /// events the OS never delivered. <c>ROSLYNSKILLS_FRESHNESS_SAFETY_SWEEP_SECONDS</c> overrides the default.
    /// </summary>
    public static TimeSpan SafetySweepInterval { get; } = ResolveSafetyInterval(
        Environment.GetEnvironmentVariable("ROSLYNSKILLS_FRESHNESS_SAFETY_SWEEP_SECONDS"));

    internal static bool IsSweepRequested(string? value)
        => string.Equals(value?.Trim(), "sweep", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value?.Trim(), FullScanMode, StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan ResolveSafetyInterval(string? value)
        => double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(DefaultSafetySweepSeconds);
}

/// <summary>
/// Brings a hot workspace up to date with the files on disk before a command reads it, so a query always sees
/// the edits made since the last call (by roscli edit commands, an editor or another agent). The hot workspace
/// otherwise only changed through its own edit commands or an explicit <c>workspace.refresh</c>.
/// </summary>
internal static class HotWorkspaceFreshness
{
    /// <summary>
    /// Applies pending source changes (edits, additions matching project include rules, and removals) to the workspace
    /// named by <paramref name="workspaceHandle"/>. <c>auto</c>/<c>balanced</c> (default) applies source changes
    /// incrementally and reports <c>requires_reload</c> only when project or solution files change; <c>strict</c> also
    /// reloads the solution when project or solution files change; <c>none</c> leaves the workspace as it is.
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

        WorkspaceStatus statusBefore = store.BuildFreshnessStatus(
            hosted,
            forceFullScan: string.Equals(policy, WorkspaceHostProtocol.RefreshPolicy.Strict, StringComparison.Ordinal),
            out string freshnessMode);
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
                Error: null,
                Mode: freshnessMode);
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
            result.Error,
            Mode: freshnessMode);
    }

    private static string NormalizePolicy(string? refreshPolicy)
        => refreshPolicy?.Trim().ToLowerInvariant() switch
        {
            "none" => WorkspaceHostProtocol.RefreshPolicy.None,
            "strict" => WorkspaceHostProtocol.RefreshPolicy.Strict,
            _ => WorkspaceHostProtocol.RefreshPolicy.Auto,
        };
}
