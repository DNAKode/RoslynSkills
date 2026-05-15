using System.Security.Cryptography;
using System.Text;
using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

internal interface IWorkspaceHostStore
{
    int Count { get; }

    HostedWorkspace Add(StaticAnalysisWorkspace workspace, string mode, bool includeGenerated, int maxFiles);

    bool TryGet(string handle, out HostedWorkspace? workspace);

    bool TryRemove(string handle, out HostedWorkspace? workspace);

    IReadOnlyList<HostedWorkspace> List();

    WorkspaceStatus BuildStatus(HostedWorkspace hosted);

    Task<WorkspaceRefreshResult> ApplyIncrementalSourceRefreshAsync(
        string handle,
        WorkspaceStatus status,
        CancellationToken cancellationToken);

    Task<WorkspaceRefreshResult> ApplyIncrementalSourceRefreshForPathsAsync(
        string handle,
        WorkspaceStatus status,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken);

    Task<WorkspaceRefreshResult> ReloadAsync(
        string handle,
        WorkspaceStatus statusBefore,
        string mode,
        CancellationToken cancellationToken);
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

    public HostedWorkspace Add(StaticAnalysisWorkspace workspace, string mode, bool includeGenerated, int maxFiles)
    {
        string handle = $"ws_{Guid.NewGuid():N}";
        HostedWorkspace hosted = CreateHostedWorkspace(handle, workspace, mode, includeGenerated, maxFiles, DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _workspaces[handle] = hosted;
        }

        return hosted;
    }

    private static HostedWorkspace CreateHostedWorkspace(
        string handle,
        StaticAnalysisWorkspace workspace,
        string mode,
        bool includeGenerated,
        int maxFiles,
        DateTimeOffset loadedAt)
    {
        string[] sourcePaths = workspace.SyntaxTrees
            .Select(tree => tree.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] projectPaths = workspace.ProjectFilePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] trackedPaths = sourcePaths
            .Concat(projectPaths)
            .Append(workspace.ResolvedWorkspacePath)
            .Concat(EnumerateExistingStructuralConfigPaths(workspace.RootDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyDictionary<string, DateTimeOffset?> trackedWriteTimes = SnapshotWriteTimes(trackedPaths);
        string fingerprint = ComputeFingerprint(workspace, trackedWriteTimes);
        WorkspaceChangeTracker changeTracker = WorkspaceChangeTracker.Start(
            workspace.RootDirectory,
            sourcePaths,
            projectPaths,
            workspace.ResolvedWorkspacePath,
            includeGenerated);

        HostedWorkspace hosted = new(
            Handle: handle,
            Workspace: workspace,
            Mode: mode,
            IncludeGenerated: includeGenerated,
            MaxFiles: maxFiles,
            LoadedAtUtc: loadedAt,
            LastRefreshUtc: loadedAt,
            WorkspaceFingerprint: fingerprint,
            TrackedPaths: trackedPaths,
            TrackedSourcePaths: sourcePaths,
            TrackedProjectPaths: projectPaths,
            TrackedWriteTimes: trackedWriteTimes,
            ChangeTracker: changeTracker);

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
            bool removed = _workspaces.Remove(handle, out workspace);
            if (removed)
            {
                workspace?.Dispose();
            }

            return removed;
        }
    }

    public IReadOnlyList<HostedWorkspace> List()
    {
        lock (_gate)
        {
            return _workspaces.Values
                .OrderBy(workspace => workspace.LoadedAtUtc)
                .ToArray();
        }
    }

    public WorkspaceStatus BuildStatus(HostedWorkspace hosted)
    {
        Dictionary<string, WorkspaceChange> changes = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceChange change in hosted.ChangeTracker.SnapshotChanges())
        {
            changes[change.Path] = change;
        }

        foreach (string path in hosted.TrackedPaths)
        {
            DateTimeOffset? previous = hosted.TrackedWriteTimes.TryGetValue(path, out DateTimeOffset? value)
                ? value
                : null;
            DateTimeOffset? current = GetLastWriteTime(path);
            if (previous != current)
            {
                WorkspaceChange change = WorkspaceChangeClassifier.Classify(
                    path,
                    hosted.TrackedSourcePaths,
                    hosted.TrackedProjectPaths,
                    hosted.Workspace.ResolvedWorkspacePath,
                    "snapshot",
                    current is not null,
                    current);
                if (!change.Ignored)
                {
                    changes[path] = change;
                }
            }
        }

        WorkspaceChange[] dirtyEntries = changes.Values
            .Where(change => !change.Ignored)
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool requiresReload = dirtyEntries.Any(change => change.RequiresReload);
        return new WorkspaceStatus(
            Loaded: true,
            Dirty: dirtyEntries.Length > 0,
            InvalidatedPaths: dirtyEntries.Select(change => change.Path).ToArray(),
            Changes: dirtyEntries,
            RequiresReload: requiresReload,
            CanIncrementallyUpdate: dirtyEntries.Length > 0 && !requiresReload);
    }

    public async Task<WorkspaceRefreshResult> ApplyIncrementalSourceRefreshAsync(
        string handle,
        WorkspaceStatus status,
        CancellationToken cancellationToken)
    {
        string[] sourcePaths = status.Changes
            .Where(change => string.Equals(change.DirtyKind, "source_change", StringComparison.OrdinalIgnoreCase) &&
                             change.CanIncrementallyUpdate &&
                             !change.RequiresReload &&
                             change.Exists)
            .Select(change => change.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return await ApplyIncrementalSourceRefreshForPathsAsync(
                handle,
                status,
                sourcePaths,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkspaceRefreshResult> ApplyIncrementalSourceRefreshForPathsAsync(
        string handle,
        WorkspaceStatus status,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        HostedWorkspace hosted;
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(handle, out HostedWorkspace? current))
            {
                return new WorkspaceRefreshResult(
                    Applied: false,
                    RefreshAction: "none",
                    UpdatedPaths: Array.Empty<string>(),
                    StatusBefore: status,
                    StatusAfter: status,
                    Error: new CommandError("workspace_not_found", $"Workspace handle '{handle}' was not found."));
            }

            hosted = current;
        }

        string[] normalizedSourcePaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedSourcePaths.Length == 0)
        {
            return new WorkspaceRefreshResult(
                Applied: false,
                RefreshAction: "none",
                UpdatedPaths: Array.Empty<string>(),
                StatusBefore: status,
                StatusAfter: status);
        }

        (StaticAnalysisWorkspace? updatedWorkspace, IReadOnlyList<string> updatedPaths, CommandError? error) =
            await hosted.Workspace.ApplyDocumentTextUpdatesAsync(
                    normalizedSourcePaths,
                    hosted.IncludeGenerated,
                    Math.Max(1, hosted.TrackedSourcePaths.Count),
                    cancellationToken)
                .ConfigureAwait(false);
        if (error is not null || updatedWorkspace is null)
        {
            return new WorkspaceRefreshResult(
                Applied: false,
                RefreshAction: "none",
                UpdatedPaths: updatedPaths,
                StatusBefore: status,
                StatusAfter: status,
                Error: error ?? new CommandError("workspace_update_failed", "Incremental source refresh failed."));
        }

        IReadOnlyDictionary<string, DateTimeOffset?> trackedWriteTimes = SnapshotWriteTimes(hosted.TrackedPaths);
        string fingerprint = ComputeFingerprint(updatedWorkspace, trackedWriteTimes);
        hosted.ChangeTracker.ClearPaths(updatedPaths);
        HostedWorkspace refreshed = hosted with
        {
            Workspace = updatedWorkspace,
            LastRefreshUtc = DateTimeOffset.UtcNow,
            WorkspaceFingerprint = fingerprint,
            TrackedWriteTimes = trackedWriteTimes,
        };

        lock (_gate)
        {
            _workspaces[handle] = refreshed;
        }

        WorkspaceStatus statusAfter = BuildStatus(refreshed);
        return new WorkspaceRefreshResult(
            Applied: true,
            RefreshAction: "incremental_document_update",
            UpdatedPaths: updatedPaths,
            StatusBefore: status,
            StatusAfter: statusAfter);
    }

    public async Task<WorkspaceRefreshResult> ReloadAsync(
        string handle,
        WorkspaceStatus statusBefore,
        string mode,
        CancellationToken cancellationToken)
    {
        HostedWorkspace hosted;
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(handle, out HostedWorkspace? current))
            {
                return new WorkspaceRefreshResult(
                    Applied: false,
                    RefreshAction: "none",
                    UpdatedPaths: Array.Empty<string>(),
                    StatusBefore: statusBefore,
                    StatusAfter: statusBefore,
                    Error: new CommandError("workspace_not_found", $"Workspace handle '{handle}' was not found."));
            }

            hosted = current;
        }

        (StaticAnalysisWorkspace? workspace, CommandError? error) = await StaticAnalysisWorkspace.LoadAsync(
                hosted.Workspace.WorkspacePath,
                hosted.IncludeGenerated,
                hosted.MaxFiles,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null || workspace is null)
        {
            return new WorkspaceRefreshResult(
                Applied: false,
                RefreshAction: "none",
                UpdatedPaths: Array.Empty<string>(),
                StatusBefore: statusBefore,
                StatusAfter: statusBefore,
                Error: error ?? new CommandError("workspace_reload_failed", "Workspace reload failed."));
        }

        HostedWorkspace reloaded = CreateHostedWorkspace(
            hosted.Handle,
            workspace,
            mode,
            hosted.IncludeGenerated,
            hosted.MaxFiles,
            DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _workspaces[handle] = reloaded;
        }

        hosted.Dispose();
        WorkspaceStatus statusAfter = BuildStatus(reloaded);
        return new WorkspaceRefreshResult(
            Applied: true,
            RefreshAction: "reload",
            UpdatedPaths: Array.Empty<string>(),
            StatusBefore: statusBefore,
            StatusAfter: statusAfter);
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

    private static IEnumerable<string> EnumerateExistingStructuralConfigPaths(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            yield break;
        }

        foreach (string fileName in WorkspaceChangeClassifier.StructuralConfigFileNames)
        {
            string path = Path.Combine(rootDirectory, fileName);
            if (File.Exists(path))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

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
    int MaxFiles,
    DateTimeOffset LoadedAtUtc,
    DateTimeOffset LastRefreshUtc,
    string WorkspaceFingerprint,
    IReadOnlyList<string> TrackedPaths,
    IReadOnlyList<string> TrackedSourcePaths,
    IReadOnlyList<string> TrackedProjectPaths,
    IReadOnlyDictionary<string, DateTimeOffset?> TrackedWriteTimes,
    WorkspaceChangeTracker ChangeTracker) : IDisposable
{
    public void Dispose()
    {
        ChangeTracker.Dispose();
    }
}

internal sealed record WorkspaceStatus(
    bool Loaded,
    bool Dirty,
    IReadOnlyList<string> InvalidatedPaths,
    IReadOnlyList<WorkspaceChange> Changes,
    bool RequiresReload,
    bool CanIncrementallyUpdate);

internal sealed record WorkspaceRefreshResult(
    bool Applied,
    string RefreshAction,
    IReadOnlyList<string> UpdatedPaths,
    WorkspaceStatus StatusBefore,
    WorkspaceStatus StatusAfter,
    CommandError? Error = null);

internal sealed record WorkspaceChange(
    string Path,
    string DirtyKind,
    bool CanIncrementallyUpdate,
    bool RequiresReload,
    bool Exists,
    DateTimeOffset? LastWriteUtc,
    DateTimeOffset ObservedUtc,
    string ChangeType,
    bool Ignored);

internal sealed class WorkspaceChangeTracker : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly HashSet<string> _trackedSourcePaths;
    private readonly HashSet<string> _trackedProjectPaths;
    private readonly string _workspacePath;
    private readonly bool _includeGenerated;
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkspaceChange> _changes = new(StringComparer.OrdinalIgnoreCase);

    private WorkspaceChangeTracker(
        FileSystemWatcher? watcher,
        IEnumerable<string> trackedSourcePaths,
        IEnumerable<string> trackedProjectPaths,
        string workspacePath,
        bool includeGenerated)
    {
        _watcher = watcher;
        _trackedSourcePaths = new HashSet<string>(trackedSourcePaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        _trackedProjectPaths = new HashSet<string>(trackedProjectPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        _workspacePath = Path.GetFullPath(workspacePath);
        _includeGenerated = includeGenerated;
    }

    public static WorkspaceChangeTracker Start(
        string rootDirectory,
        IEnumerable<string> trackedSourcePaths,
        IEnumerable<string> trackedProjectPaths,
        string workspacePath,
        bool includeGenerated)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return new WorkspaceChangeTracker(null, trackedSourcePaths, trackedProjectPaths, workspacePath, includeGenerated);
        }

        FileSystemWatcher watcher = new(rootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
        };
        WorkspaceChangeTracker tracker = new(watcher, trackedSourcePaths, trackedProjectPaths, workspacePath, includeGenerated);
        watcher.Changed += (_, args) => tracker.Record(args.FullPath, args.ChangeType.ToString());
        watcher.Created += (_, args) => tracker.Record(args.FullPath, args.ChangeType.ToString());
        watcher.Deleted += (_, args) => tracker.Record(args.FullPath, args.ChangeType.ToString());
        watcher.Renamed += (_, args) =>
        {
            tracker.Record(args.OldFullPath, "RenamedFrom");
            tracker.Record(args.FullPath, "RenamedTo");
        };
        watcher.Error += (_, _) => tracker.Record(rootDirectory, "WatcherError");
        watcher.EnableRaisingEvents = true;
        return tracker;
    }

    public IReadOnlyList<WorkspaceChange> SnapshotChanges()
    {
        lock (_gate)
        {
            return _changes.Values.ToArray();
        }
    }

    public void ClearPaths(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (string path in paths.Select(Path.GetFullPath))
            {
                _changes.Remove(path);
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    private void Record(string path, string changeType)
    {
        string fullPath = Path.GetFullPath(path);
        bool exists = File.Exists(fullPath);
        WorkspaceChange change = WorkspaceChangeClassifier.Classify(
            fullPath,
            _trackedSourcePaths,
            _trackedProjectPaths,
            _workspacePath,
            changeType,
            exists,
            exists ? File.GetLastWriteTimeUtc(fullPath) : null,
            _includeGenerated);
        if (change.Ignored)
        {
            return;
        }

        lock (_gate)
        {
            _changes[fullPath] = change;
        }
    }
}

internal static class WorkspaceChangeClassifier
{
    public static readonly IReadOnlyList<string> StructuralConfigFileNames = new[]
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "NuGet.config",
    };

    public static WorkspaceChange Classify(
        string path,
        IEnumerable<string> trackedSourcePaths,
        IEnumerable<string> trackedProjectPaths,
        string workspacePath,
        string eventName,
        bool exists,
        DateTimeOffset? lastWriteUtc,
        bool includeGenerated = false)
    {
        string fullPath = Path.GetFullPath(path);
        DateTimeOffset observedUtc = DateTimeOffset.UtcNow;
        if (ShouldIgnore(fullPath, includeGenerated))
        {
            return new WorkspaceChange(fullPath, "ignored", false, false, exists, lastWriteUtc, observedUtc, eventName, Ignored: true);
        }

        HashSet<string> sourceSet = new(trackedSourcePaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        HashSet<string> projectSet = new(trackedProjectPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        bool isTrackedSource = sourceSet.Contains(fullPath);
        bool isKnownProject = projectSet.Contains(fullPath);
        bool isWorkspaceFile = string.Equals(fullPath, Path.GetFullPath(workspacePath), StringComparison.OrdinalIgnoreCase);
        bool isSourceFile = CommandLanguageServices.IsSupportedSourceFile(fullPath);

        if (isTrackedSource && exists)
        {
            return new WorkspaceChange(fullPath, "source_change", true, false, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        if (isTrackedSource && !exists)
        {
            return new WorkspaceChange(fullPath, "unknown_or_membership_change", false, true, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        if (isWorkspaceFile || isKnownProject || IsProjectStructurePath(fullPath))
        {
            return new WorkspaceChange(fullPath, "project_structure_change", false, true, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        if (IsAnalyzerConfigPath(fullPath))
        {
            return new WorkspaceChange(fullPath, "analyzer_config_change", false, true, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        if (isSourceFile)
        {
            return new WorkspaceChange(fullPath, "unknown_or_membership_change", false, true, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        return new WorkspaceChange(fullPath, "unknown_or_membership_change", false, true, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
    }

    private static bool ShouldIgnore(string path, bool includeGenerated)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".roslynskills", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "TestResults", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !includeGenerated && CommandFileFilters.IsGeneratedPath(path);
    }

    private static bool IsProjectStructurePath(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = Path.GetFileName(path);
        return StructuralConfigFileNames.Any(name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAnalyzerConfigPath(string path)
    {
        string extension = Path.GetExtension(path);
        string fileName = Path.GetFileName(path);
        return string.Equals(extension, ".editorconfig", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".globalconfig", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".ruleset", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("analyzer", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class WorkspaceStatusData
{
    public static object[] BuildDirtyEntries(WorkspaceStatus status)
        => status.Changes
            .Select(change => new
            {
                path = change.Path,
                dirty_kind = change.DirtyKind,
                can_incrementally_update = change.CanIncrementallyUpdate,
                requires_reload = change.RequiresReload,
                exists = change.Exists,
                last_write_utc = change.LastWriteUtc,
                observed_utc = change.ObservedUtc,
                change_type = change.ChangeType,
            })
            .Cast<object>()
            .ToArray();

    public static string[] BuildDirtyKinds(WorkspaceStatus status)
        => status.Changes
            .Select(change => change.DirtyKind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
