using System.Security.Cryptography;
using System.Text;

namespace RoslynSkills.Core.Commands;

internal static class WorkspaceHostStore
{
    private static readonly Dictionary<string, HostedWorkspace> Workspaces = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Workspaces.Count;
            }
        }
    }

    public static HostedWorkspace Add(StaticAnalysisWorkspace workspace, string mode, bool includeGenerated)
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

        lock (Gate)
        {
            Workspaces[handle] = hosted;
        }

        return hosted;
    }

    public static bool TryGet(string handle, out HostedWorkspace? workspace)
    {
        lock (Gate)
        {
            return Workspaces.TryGetValue(handle, out workspace);
        }
    }

    public static bool TryRemove(string handle, out HostedWorkspace? workspace)
    {
        lock (Gate)
        {
            return Workspaces.Remove(handle, out workspace);
        }
    }

    public static WorkspaceStatus BuildStatus(HostedWorkspace hosted)
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
