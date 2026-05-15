using System.Security.Cryptography;
using System.Text;

namespace RoslynSkills.Core.Commands;

internal interface IWorkspaceHostStore
{
    int Count { get; }

    HostedWorkspace Add(StaticAnalysisWorkspace workspace, string mode, bool includeGenerated);

    bool TryGet(string handle, out HostedWorkspace? workspace);

    bool TryRemove(string handle, out HostedWorkspace? workspace);

    WorkspaceStatus BuildStatus(HostedWorkspace hosted);
}

internal static class WorkspaceHostStoreProvider
{
    public static IWorkspaceHostStore Current { get; } = new InMemoryWorkspaceHostStore();
}

internal sealed class InMemoryWorkspaceHostStore : IWorkspaceHostStore
{
    private readonly Dictionary<string, HostedWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _workspaces.Count;
            }
        }
    }

    public HostedWorkspace Add(StaticAnalysisWorkspace workspace, string mode, bool includeGenerated)
    {
        DateTimeOffset loadedAt = DateTimeOffset.UtcNow;
        string[] trackedPaths = workspace.SyntaxTrees
            .Select(tree => tree.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Append(workspace.ResolvedWorkspacePath)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyDictionary<string, DateTimeOffset?> trackedWriteTimes = SnapshotWriteTimes(trackedPaths);
        string fingerprint = ComputeFingerprint(workspace, trackedWriteTimes);
        string handle = $"ws_{Guid.NewGuid():N}";

        HostedWorkspace hosted = new(
            Handle: handle,
            Workspace: workspace,
            Mode: mode,
            IncludeGenerated: includeGenerated,
            LoadedAtUtc: loadedAt,
            LastRefreshUtc: loadedAt,
            WorkspaceFingerprint: fingerprint,
            TrackedPaths: trackedPaths,
            TrackedWriteTimes: trackedWriteTimes);

        lock (_gate)
        {
            _workspaces[handle] = hosted;
        }

        return hosted;
    }

    public bool TryGet(string handle, out HostedWorkspace? workspace)
    {
        lock (_gate)
        {
            return _workspaces.TryGetValue(handle, out workspace);
        }
    }

    public bool TryRemove(string handle, out HostedWorkspace? workspace)
    {
        lock (_gate)
        {
            return _workspaces.Remove(handle, out workspace);
        }
    }

    public WorkspaceStatus BuildStatus(HostedWorkspace hosted)
    {
        List<string> invalidatedPaths = new();
        foreach (string path in hosted.TrackedPaths)
        {
            DateTimeOffset? previous = hosted.TrackedWriteTimes.TryGetValue(path, out DateTimeOffset? value)
                ? value
                : null;
            DateTimeOffset? current = GetLastWriteTime(path);
            if (previous != current)
            {
                invalidatedPaths.Add(path);
            }
        }

        return new WorkspaceStatus(
            Loaded: true,
            Dirty: invalidatedPaths.Count > 0,
            InvalidatedPaths: invalidatedPaths.ToArray());
    }

    private static IReadOnlyDictionary<string, DateTimeOffset?> SnapshotWriteTimes(IEnumerable<string> paths)
    {
        Dictionary<string, DateTimeOffset?> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            snapshot[path] = GetLastWriteTime(path);
        }

        return snapshot;
    }

    private static DateTimeOffset? GetLastWriteTime(string path)
        => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    private static string ComputeFingerprint(
        StaticAnalysisWorkspace workspace,
        IReadOnlyDictionary<string, DateTimeOffset?> trackedWriteTimes)
    {
        StringBuilder builder = new();
        builder.AppendLine(workspace.ResolvedWorkspacePath);
        builder.AppendLine(workspace.AnalysisMode);
        builder.AppendLine(workspace.WorkspaceKind);
        builder.AppendLine(workspace.ProjectCount.ToString());
        builder.AppendLine(workspace.DocumentCount.ToString());

        foreach ((string path, DateTimeOffset? writeTime) in trackedWriteTimes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(path);
            builder.Append('|');
            builder.AppendLine(writeTime?.UtcTicks.ToString() ?? "missing");
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal sealed record HostedWorkspace(
    string Handle,
    StaticAnalysisWorkspace Workspace,
    string Mode,
    bool IncludeGenerated,
    DateTimeOffset LoadedAtUtc,
    DateTimeOffset LastRefreshUtc,
    string WorkspaceFingerprint,
    IReadOnlyList<string> TrackedPaths,
    IReadOnlyDictionary<string, DateTimeOffset?> TrackedWriteTimes);

internal sealed record WorkspaceStatus(
    bool Loaded,
    bool Dirty,
    IReadOnlyList<string> InvalidatedPaths);
