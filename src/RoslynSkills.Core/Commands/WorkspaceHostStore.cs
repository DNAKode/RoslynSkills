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
        CancellationToken cancellationToken,
        bool allowAddDocuments = false);

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
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly Dictionary<string, HostedWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _handleGates = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private SemaphoreSlim GetHandleGate(string handle)
        => _handleGates.GetOrAdd(handle, static _ => new SemaphoreSlim(1, 1));

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
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();
        HashSet<string> sourcePathSet = new(sourcePaths, PathComparer);

        string[] projectPaths = workspace.ProjectFilePaths
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();
        HashSet<string> projectPathSet = new(projectPaths, PathComparer);

        IReadOnlyList<ProjectSourceRules> projectSourceRules = workspace.Solution is { } solution
            ? ProjectSourceRules.FromSolution(solution, workspace.RootDirectory)
            : Array.Empty<ProjectSourceRules>();

        // Snapshot source files that already existed on disk inside project folders at load time
        // yet were not included by MSBuild (e.g., conditional removes or external targets), so
        // pre-call disk reconciliation never spuriously adds them as "new" files.
        HashSet<string> solutionDocumentPaths = new(sourcePathSet, PathComparer);
        if (workspace.Solution is { } loadedSolution)
        {
            foreach (Microsoft.CodeAnalysis.Project project in loadedSolution.Projects)
            {
                foreach (Microsoft.CodeAnalysis.Document doc in project.Documents)
                {
                    if (!string.IsNullOrWhiteSpace(doc.FilePath))
                    {
                        solutionDocumentPaths.Add(Path.GetFullPath(doc.FilePath));
                    }
                }
            }
        }

        HashSet<string> baselineExcludedSourcePaths = new(PathComparer);
        foreach (string candidate in ProjectSourceRules.EnumerateCandidateSourceFiles(projectSourceRules, includeGenerated))
        {
            if (!solutionDocumentPaths.Contains(candidate))
            {
                baselineExcludedSourcePaths.Add(candidate);
            }
        }

        string[] trackedPaths = sourcePaths
            .Concat(projectPaths)
            .Append(Path.GetFullPath(workspace.ResolvedWorkspacePath))
            .Concat(EnumerateExistingStructuralConfigPaths(workspace.RootDirectory, projectPaths))
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();
        IReadOnlyDictionary<string, DateTimeOffset?> trackedWriteTimes = SnapshotWriteTimes(trackedPaths);
        string fingerprint = ComputeFingerprint(workspace, trackedWriteTimes);
        WorkspaceChangeTracker changeTracker = WorkspaceChangeTracker.Start(
            workspace.RootDirectory,
            sourcePaths,
            projectPaths,
            workspace.ResolvedWorkspacePath,
            projectSourceRules,
            baselineExcludedSourcePaths,
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
            TrackedSourcePathSet: sourcePathSet,
            TrackedProjectPaths: projectPaths,
            TrackedProjectPathSet: projectPathSet,
            ProjectSourceRules: projectSourceRules,
            BaselineExcludedSourcePaths: baselineExcludedSourcePaths,
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
        Dictionary<string, WorkspaceChange> changes = new(PathComparer);
        List<string>? staleWatcherPaths = null;

        foreach (WorkspaceChange change in hosted.ChangeTracker.SnapshotChanges())
        {
            bool existsNow = File.Exists(change.Path);
            DateTimeOffset? currentWriteTime = existsNow ? GetLastWriteTime(change.Path) : null;
            WorkspaceChange verified = WorkspaceChangeClassifier.Classify(
                change.Path,
                hosted.Workspace.RootDirectory,
                hosted.TrackedSourcePathSet.Contains(change.Path),
                hosted.TrackedProjectPathSet.Contains(change.Path),
                hosted.Workspace.ResolvedWorkspacePath,
                hosted.ProjectSourceRules,
                hosted.BaselineExcludedSourcePaths,
                change.ChangeType,
                existsNow,
                currentWriteTime,
                hosted.IncludeGenerated);
            if (verified.Ignored)
            {
                staleWatcherPaths ??= new List<string>();
                staleWatcherPaths.Add(change.Path);
                continue;
            }

            changes[verified.Path] = verified;
        }

        if (staleWatcherPaths is { Count: > 0 })
        {
            hosted.ChangeTracker.ClearPaths(staleWatcherPaths);
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
                    hosted.Workspace.RootDirectory,
                    hosted.TrackedSourcePathSet.Contains(path),
                    hosted.TrackedProjectPathSet.Contains(path),
                    hosted.Workspace.ResolvedWorkspacePath,
                    hosted.ProjectSourceRules,
                    hosted.BaselineExcludedSourcePaths,
                    "snapshot",
                    current is not null,
                    current,
                    hosted.IncludeGenerated);
                if (!change.Ignored)
                {
                    changes[path] = change;
                }
            }
        }

        // Catch newly added structural config files (e.g., Directory.Build.props) even if watcher lagged.
        foreach (string configPath in EnumerateExistingStructuralConfigPaths(hosted.Workspace.RootDirectory, hosted.TrackedProjectPaths))
        {
            if (hosted.TrackedWriteTimes.ContainsKey(configPath) || changes.ContainsKey(configPath))
            {
                continue;
            }

            DateTimeOffset? current = GetLastWriteTime(configPath);
            WorkspaceChange change = WorkspaceChangeClassifier.Classify(
                configPath,
                hosted.Workspace.RootDirectory,
                isTrackedSource: false,
                isKnownProject: false,
                hosted.Workspace.ResolvedWorkspacePath,
                hosted.ProjectSourceRules,
                hosted.BaselineExcludedSourcePaths,
                "disk_scan",
                current is not null,
                current,
                hosted.IncludeGenerated);
            if (!change.Ignored)
            {
                changes[configPath] = change;
            }
        }

        // Reconcile new source files under loaded project folders directly from disk so creations by external
        // tools, git checkout, or git stash pop are visible immediately without relying on watcher timing.
        if (hosted.ProjectSourceRules.Count > 0)
        {
            DateTimeOffset observedUtc = DateTimeOffset.UtcNow;
            foreach (string candidatePath in ProjectSourceRules.EnumerateCandidateSourceFiles(
                         hosted.ProjectSourceRules,
                         hosted.IncludeGenerated))
            {
                if (hosted.TrackedSourcePathSet.Contains(candidatePath) ||
                    hosted.BaselineExcludedSourcePaths.Contains(candidatePath) ||
                    changes.ContainsKey(candidatePath))
                {
                    continue;
                }

                changes[candidatePath] = new WorkspaceChange(
                    Path: candidatePath,
                    DirtyKind: "source_added",
                    CanIncrementallyUpdate: true,
                    RequiresReload: false,
                    Exists: true,
                    LastWriteUtc: GetLastWriteTime(candidatePath),
                    ObservedUtc: observedUtc,
                    ChangeType: "disk_scan",
                    Ignored: false);
            }
        }

        WorkspaceChange[] dirtyEntries = changes.Values
            .Where(change => !change.Ignored)
            .OrderBy(change => change.Path, PathComparer)
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
        string[] modifiedOrAddedPaths = status.Changes
            .Where(change => change.CanIncrementallyUpdate &&
                             !change.RequiresReload &&
                             change.Exists)
            .Select(change => change.Path)
            .Distinct(PathComparer)
            .ToArray();

        string[] removedPaths = status.Changes
            .Where(change => change.CanIncrementallyUpdate &&
                             !change.RequiresReload &&
                             !change.Exists)
            .Select(change => change.Path)
            .Distinct(PathComparer)
            .ToArray();

        return await ApplyIncrementalSourceChangesAsync(
                handle,
                status,
                modifiedOrAddedPaths,
                removedPaths,
                cancellationToken,
                allowAddDocuments: true)
            .ConfigureAwait(false);
    }

    public Task<WorkspaceRefreshResult> ApplyIncrementalSourceRefreshForPathsAsync(
        string handle,
        WorkspaceStatus status,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken,
        bool allowAddDocuments = false)
        => ApplyIncrementalSourceChangesAsync(
            handle,
            status,
            sourcePaths,
            Array.Empty<string>(),
            cancellationToken,
            allowAddDocuments);

    private async Task<WorkspaceRefreshResult> ApplyIncrementalSourceChangesAsync(
        string handle,
        WorkspaceStatus status,
        IReadOnlyList<string> modifiedOrAddedPaths,
        IReadOnlyList<string> removedPaths,
        CancellationToken cancellationToken,
        bool allowAddDocuments)
    {
        SemaphoreSlim handleGate = GetHandleGate(handle);
        await handleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyIncrementalSourceChangesCoreAsync(
                    handle,
                    status,
                    modifiedOrAddedPaths,
                    removedPaths,
                    cancellationToken,
                    allowAddDocuments)
                .ConfigureAwait(false);
        }
        finally
        {
            handleGate.Release();
        }
    }

    private async Task<WorkspaceRefreshResult> ApplyIncrementalSourceChangesCoreAsync(
        string handle,
        WorkspaceStatus status,
        IReadOnlyList<string> modifiedOrAddedPaths,
        IReadOnlyList<string> removedPaths,
        CancellationToken cancellationToken,
        bool allowAddDocuments)
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

        string[] normalizedModifiedOrAdded = modifiedOrAddedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        string[] normalizedRemoved = removedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();

        if (normalizedModifiedOrAdded.Length == 0 && normalizedRemoved.Length == 0)
        {
            return new WorkspaceRefreshResult(
                Applied: false,
                RefreshAction: "none",
                UpdatedPaths: Array.Empty<string>(),
                StatusBefore: status,
                StatusAfter: status);
        }

        // Record write times before reading file contents so an edit arriving mid-refresh is still caught next call.
        Dictionary<string, DateTimeOffset?> preReadWriteTimes = new(PathComparer);
        foreach (string path in normalizedModifiedOrAdded)
        {
            preReadWriteTimes[path] = GetLastWriteTime(path);
        }

        int effectiveMaxFiles = Math.Max(hosted.MaxFiles, hosted.TrackedSourcePaths.Count + normalizedModifiedOrAdded.Length);
        (StaticAnalysisWorkspace? updatedWorkspace, IReadOnlyList<string> updatedPaths, IReadOnlyList<string> addedPaths, IReadOnlyList<string> appliedRemovedPaths, CommandError? error) =
            await hosted.Workspace.ApplyDocumentChangesAsync(
                    normalizedModifiedOrAdded,
                    normalizedRemoved,
                    hosted.IncludeGenerated,
                    effectiveMaxFiles,
                    allowAddDocuments,
                    hosted.ProjectSourceRules,
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

        if (updatedPaths.Count == 0 && appliedRemovedPaths.Count == 0)
        {
            hosted.ChangeTracker.ClearPaths(normalizedModifiedOrAdded.Concat(normalizedRemoved));
            WorkspaceStatus unchangedAfter = BuildStatus(hosted);
            return new WorkspaceRefreshResult(
                Applied: false,
                RefreshAction: "none",
                UpdatedPaths: Array.Empty<string>(),
                StatusBefore: status,
                StatusAfter: unchangedAfter);
        }

        HashSet<string> removedSet = new(appliedRemovedPaths, PathComparer);
        HashSet<string> nextSourceSet = new(hosted.TrackedSourcePathSet, PathComparer);
        foreach (string added in addedPaths)
        {
            nextSourceSet.Add(added);
        }

        foreach (string removed in appliedRemovedPaths)
        {
            nextSourceSet.Remove(removed);
        }

        string[] trackedSourcePaths = nextSourceSet
            .OrderBy(path => path, PathComparer)
            .ToArray();

        HashSet<string> nextTrackedSet = new(hosted.TrackedPaths, PathComparer);
        foreach (string added in addedPaths)
        {
            nextTrackedSet.Add(added);
        }

        foreach (string removed in appliedRemovedPaths)
        {
            nextTrackedSet.Remove(removed);
        }

        string[] trackedPaths = nextTrackedSet
            .OrderBy(path => path, PathComparer)
            .ToArray();

        Dictionary<string, DateTimeOffset?> trackedWriteTimes = new(PathComparer);
        foreach ((string path, DateTimeOffset? writeTime) in hosted.TrackedWriteTimes)
        {
            if (!removedSet.Contains(path))
            {
                trackedWriteTimes[path] = writeTime;
            }
        }

        foreach (string updated in updatedPaths)
        {
            if (preReadWriteTimes.TryGetValue(updated, out DateTimeOffset? preReadTime))
            {
                trackedWriteTimes[updated] = preReadTime;
            }
            else
            {
                trackedWriteTimes[updated] = GetLastWriteTime(updated);
            }
        }

        if (addedPaths.Count > 0)
        {
            hosted.ChangeTracker.TrackSourcePaths(addedPaths);
        }

        if (appliedRemovedPaths.Count > 0)
        {
            hosted.ChangeTracker.UntrackSourcePaths(appliedRemovedPaths);
        }

        hosted.ChangeTracker.ClearPaths(updatedPaths);

        string fingerprint = ComputeFingerprint(updatedWorkspace, trackedWriteTimes);
        HostedWorkspace refreshed = hosted with
        {
            Workspace = updatedWorkspace,
            MaxFiles = effectiveMaxFiles,
            LastRefreshUtc = DateTimeOffset.UtcNow,
            WorkspaceFingerprint = fingerprint,
            TrackedPaths = trackedPaths,
            TrackedSourcePaths = trackedSourcePaths,
            TrackedSourcePathSet = nextSourceSet,
            TrackedWriteTimes = trackedWriteTimes,
        };

        lock (_gate)
        {
            if (!_workspaces.ContainsKey(handle))
            {
                return new WorkspaceRefreshResult(
                    Applied: false,
                    RefreshAction: "none",
                    UpdatedPaths: Array.Empty<string>(),
                    StatusBefore: status,
                    StatusAfter: status,
                    Error: new CommandError("workspace_not_found", $"Workspace handle '{handle}' was closed before refresh completed."));
            }

            _workspaces[handle] = refreshed;
        }

        string[] allAppliedPaths = updatedPaths
            .Concat(appliedRemovedPaths)
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();

        WorkspaceStatus statusAfter = BuildStatus(refreshed);
        return new WorkspaceRefreshResult(
            Applied: true,
            RefreshAction: WorkspaceHostProtocol.RefreshAction.IncrementalDocumentUpdate,
            UpdatedPaths: allAppliedPaths,
            StatusBefore: status,
            StatusAfter: statusAfter,
            AddedPaths: addedPaths,
            RemovedPaths: appliedRemovedPaths);
    }

    public async Task<WorkspaceRefreshResult> ReloadAsync(
        string handle,
        WorkspaceStatus statusBefore,
        string mode,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim handleGate = GetHandleGate(handle);
        await handleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
                if (!_workspaces.ContainsKey(handle))
                {
                    reloaded.Dispose();
                    return new WorkspaceRefreshResult(
                        Applied: false,
                        RefreshAction: "none",
                        UpdatedPaths: Array.Empty<string>(),
                        StatusBefore: statusBefore,
                        StatusAfter: statusBefore,
                        Error: new CommandError("workspace_not_found", $"Workspace handle '{handle}' was closed during reload."));
                }

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
        finally
        {
            handleGate.Release();
        }
    }

    private static IReadOnlyDictionary<string, DateTimeOffset?> SnapshotWriteTimes(IEnumerable<string> paths)
    {
        Dictionary<string, DateTimeOffset?> snapshot = new(PathComparer);
        foreach (string path in paths)
        {
            snapshot[path] = GetLastWriteTime(path);
        }

        return snapshot;
    }

    private static DateTimeOffset? GetLastWriteTime(string path)
        => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    private static IEnumerable<string> EnumerateExistingStructuralConfigPaths(
        string rootDirectory,
        IEnumerable<string> projectPaths)
    {
        HashSet<string> candidateDirs = new(PathComparer);
        string? normalizedRoot = Directory.Exists(rootDirectory)
            ? Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : null;
        if (!string.IsNullOrWhiteSpace(normalizedRoot))
        {
            candidateDirs.Add(normalizedRoot);
        }

        foreach (string projectPath in projectPaths)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            while (!string.IsNullOrWhiteSpace(dir))
            {
                string trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!candidateDirs.Add(trimmed))
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(normalizedRoot) && PathComparer.Equals(trimmed, normalizedRoot))
                {
                    break;
                }

                dir = Path.GetDirectoryName(trimmed);
            }
        }

        HashSet<string> yielded = new(PathComparer);
        foreach (string dir in candidateDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string fileName in WorkspaceChangeClassifier.StructuralConfigFileNames)
            {
                string path = Path.GetFullPath(Path.Combine(dir, fileName));
                if (File.Exists(path) && yielded.Add(path))
                {
                    yield return path;
                }
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

        foreach ((string path, DateTimeOffset? writeTime) in trackedWriteTimes.OrderBy(pair => pair.Key, PathComparer))
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
    IReadOnlySet<string> TrackedSourcePathSet,
    IReadOnlyList<string> TrackedProjectPaths,
    IReadOnlySet<string> TrackedProjectPathSet,
    IReadOnlyList<ProjectSourceRules> ProjectSourceRules,
    IReadOnlySet<string> BaselineExcludedSourcePaths,
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
    CommandError? Error = null,
    IReadOnlyList<string>? AddedPaths = null,
    IReadOnlyList<string>? RemovedPaths = null);

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
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly FileSystemWatcher? _watcher;
    private readonly string _rootDirectory;
    private readonly HashSet<string> _trackedSourcePaths;
    private readonly HashSet<string> _trackedProjectPaths;
    private readonly string _workspacePath;
    private readonly IReadOnlyList<ProjectSourceRules> _projectSourceRules;
    private readonly IReadOnlySet<string> _baselineExcludedSourcePaths;
    private readonly bool _includeGenerated;
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkspaceChange> _changes = new(PathComparer);

    private WorkspaceChangeTracker(
        FileSystemWatcher? watcher,
        string rootDirectory,
        IEnumerable<string> trackedSourcePaths,
        IEnumerable<string> trackedProjectPaths,
        string workspacePath,
        IReadOnlyList<ProjectSourceRules> projectSourceRules,
        IReadOnlySet<string> baselineExcludedSourcePaths,
        bool includeGenerated)
    {
        _watcher = watcher;
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? string.Empty : Path.GetFullPath(rootDirectory);
        _trackedSourcePaths = new HashSet<string>(trackedSourcePaths.Select(Path.GetFullPath), PathComparer);
        _trackedProjectPaths = new HashSet<string>(trackedProjectPaths.Select(Path.GetFullPath), PathComparer);
        _workspacePath = Path.GetFullPath(workspacePath);
        _projectSourceRules = projectSourceRules;
        _baselineExcludedSourcePaths = baselineExcludedSourcePaths;
        _includeGenerated = includeGenerated;
    }

    public static WorkspaceChangeTracker Start(
        string rootDirectory,
        IEnumerable<string> trackedSourcePaths,
        IEnumerable<string> trackedProjectPaths,
        string workspacePath,
        IReadOnlyList<ProjectSourceRules> projectSourceRules,
        IReadOnlySet<string> baselineExcludedSourcePaths,
        bool includeGenerated)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return new WorkspaceChangeTracker(
                null,
                rootDirectory,
                trackedSourcePaths,
                trackedProjectPaths,
                workspacePath,
                projectSourceRules,
                baselineExcludedSourcePaths,
                includeGenerated);
        }

        FileSystemWatcher watcher = new(rootDirectory)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
        };
        WorkspaceChangeTracker tracker = new(
            watcher,
            rootDirectory,
            trackedSourcePaths,
            trackedProjectPaths,
            workspacePath,
            projectSourceRules,
            baselineExcludedSourcePaths,
            includeGenerated);
        watcher.Changed += (_, args) => tracker.Record(args.FullPath, args.ChangeType.ToString());
        watcher.Created += (_, args) => tracker.Record(args.FullPath, args.ChangeType.ToString());
        watcher.Deleted += (_, args) => tracker.Record(args.FullPath, args.ChangeType.ToString());
        watcher.Renamed += (_, args) =>
        {
            tracker.Record(args.OldFullPath, "RenamedFrom");
            tracker.Record(args.FullPath, "RenamedTo");
        };
        // A WatcherError (e.g. buffer overflow under heavy git/build churn) must not force a reload:
        // BuildStatus reconciles tracked paths and loaded project directories directly from disk.
        watcher.Error += (_, _) => { };
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

    public void TrackSourcePaths(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (string path in paths.Select(Path.GetFullPath))
            {
                _trackedSourcePaths.Add(path);
                _changes.Remove(path);
            }
        }
    }

    public void UntrackSourcePaths(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (string path in paths.Select(Path.GetFullPath))
            {
                _trackedSourcePaths.Remove(path);
                _changes.Remove(path);
            }
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        if (Directory.Exists(fullPath) ||
            WorkspaceChangeClassifier.ShouldIgnore(fullPath, _rootDirectory, _includeGenerated))
        {
            return;
        }

        bool exists = File.Exists(fullPath);
        DateTimeOffset? lastWriteUtc = exists ? File.GetLastWriteTimeUtc(fullPath) : null;
        bool isTrackedSource;
        lock (_gate)
        {
            isTrackedSource = _trackedSourcePaths.Contains(fullPath);
        }

        bool isKnownProject = _trackedProjectPaths.Contains(fullPath);
        WorkspaceChange change = WorkspaceChangeClassifier.Classify(
            fullPath,
            _rootDirectory,
            isTrackedSource,
            isKnownProject,
            _workspacePath,
            _projectSourceRules,
            _baselineExcludedSourcePaths,
            changeType,
            exists,
            lastWriteUtc,
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
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

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
        string rootDirectory,
        bool isTrackedSource,
        bool isKnownProject,
        string workspacePath,
        IReadOnlyList<ProjectSourceRules> projectSourceRules,
        IReadOnlySet<string> baselineExcludedSourcePaths,
        string eventName,
        bool exists,
        DateTimeOffset? lastWriteUtc,
        bool includeGenerated = false)
    {
        string fullPath = Path.GetFullPath(path);
        DateTimeOffset observedUtc = DateTimeOffset.UtcNow;
        if (ShouldIgnore(fullPath, rootDirectory, includeGenerated))
        {
            return new WorkspaceChange(fullPath, "ignored", false, false, exists, lastWriteUtc, observedUtc, eventName, Ignored: true);
        }

        bool isWorkspaceFile = PathComparer.Equals(fullPath, Path.GetFullPath(workspacePath));
        if (isWorkspaceFile || isKnownProject || IsProjectStructurePath(fullPath))
        {
            return new WorkspaceChange(fullPath, "project_structure_change", false, true, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        if (isTrackedSource)
        {
            return exists
                ? new WorkspaceChange(fullPath, "source_change", true, false, exists, lastWriteUtc, observedUtc, eventName, Ignored: false)
                : new WorkspaceChange(fullPath, "source_removed", true, false, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        if (exists &&
            CommandLanguageServices.IsSupportedSourceFile(fullPath) &&
            !baselineExcludedSourcePaths.Contains(fullPath) &&
            ProjectSourceRules.MatchesAnyProject(projectSourceRules, fullPath, includeGenerated))
        {
            return new WorkspaceChange(fullPath, "source_added", true, false, exists, lastWriteUtc, observedUtc, eventName, Ignored: false);
        }

        // Non-source files, excluded source files, directory events, and untracked deletions do not dirty or reload.
        return new WorkspaceChange(fullPath, "ignored", false, false, exists, lastWriteUtc, observedUtc, eventName, Ignored: true);
    }

    public static bool ShouldIgnore(string fullPath, string rootDirectory, bool includeGenerated)
    {
        string pathToInspect = fullPath;
        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            string normalizedRoot = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Length > normalizedRoot.Length &&
                fullPath.StartsWith(normalizedRoot, PathComparison) &&
                (fullPath[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
                 fullPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar))
            {
                pathToInspect = fullPath[(normalizedRoot.Length + 1)..];
            }
        }

        string[] segments = pathToInspect.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            if (ProjectSourceRules.IsIgnoredDirectorySegment(segments[i]))
            {
                return true;
            }
        }

        string extension = Path.GetExtension(fullPath);
        if (string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !includeGenerated && CommandFileFilters.IsGeneratedPath(fullPath);
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
