using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RoslynSkills.Core.Commands;

internal sealed record WarmupProgressSnapshot(
    string State,
    string Phase,
    int ProjectsWarmed,
    int ProjectsTotal,
    int DocumentsIndexed,
    int DocumentsTotal,
    double ElapsedMs,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Error)
{
    public object ToPayload() => new
    {
        state = State,
        phase = Phase,
        projects_warmed = ProjectsWarmed,
        projects_total = ProjectsTotal,
        documents_indexed = DocumentsIndexed,
        documents_total = DocumentsTotal,
        elapsed_ms = Math.Round(ElapsedMs, 3),
        started_at_utc = StartedAtUtc,
        completed_at_utc = CompletedAtUtc,
        error = Error,
    };
}

/// <summary>
/// Moves the cold-compilation, Roslyn symbol-index, overlay text/trigram index, and call-site cache cost of the first
/// semantic query out of that query: after a workspace is preloaded the host builds every project's compilation, warms
/// Roslyn's declaration and reference syntax-tree indices, populates <see cref="WorkspaceOverlayIndex"/>,
/// <see cref="IdentifierIndex"/>, and <see cref="CallSiteAnalysis"/> in the background. A query that arrives meanwhile
/// shares the same cached work instead of repeating it. <c>ROSLYNSKILLS_WARMUP=off</c> disables it.
/// </summary>
internal static class HotWorkspaceWarmup
{
    private static readonly ConditionalWeakTable<Solution, WarmupState> ActiveWarmups = new();

    public static bool IsDisabled()
        => string.Equals(Environment.GetEnvironmentVariable("ROSLYNSKILLS_WARMUP")?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    public static void Start(HostedWorkspace hosted, CancellationToken cancellationToken = default)
        => StartCore(hosted.Workspace.Solution, hosted.Workspace, hosted.OverlayIndex, cancellationToken);

    public static void Start(Solution? solution, CancellationToken cancellationToken = default)
        => StartCore(solution, workspace: null, overlayIndex: null, cancellationToken);

    private static void StartCore(
        Solution? solution,
        StaticAnalysisWorkspace? workspace,
        WorkspaceOverlayIndex? overlayIndex,
        CancellationToken cancellationToken)
    {
        if (solution is null || IsDisabled())
        {
            return;
        }

        _ = ActiveWarmups.GetValue(solution, s =>
        {
            CancellationTokenSource cts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
            int totalProjects = s.Projects.Count();
            int totalDocuments = workspace?.SyntaxTreesByPath.Count ?? s.Projects.Sum(p => p.Documents.Count());
            WarmupTracker tracker = new(totalProjects, totalDocuments);
            Task task = Task.Run(() => WarmSolutionAsync(s, workspace, overlayIndex, tracker, cts.Token), CancellationToken.None);
            return new WarmupState(task, cts, tracker);
        });
    }

    public static WarmupProgressSnapshot GetSnapshot(Solution? solution)
    {
        if (IsDisabled())
        {
            int projCount = solution?.Projects.Count() ?? 0;
            return new WarmupProgressSnapshot(
                State: "disabled",
                Phase: "disabled",
                ProjectsWarmed: 0,
                ProjectsTotal: projCount,
                DocumentsIndexed: 0,
                DocumentsTotal: 0,
                ElapsedMs: 0,
                StartedAtUtc: null,
                CompletedAtUtc: null,
                Error: null);
        }

        if (solution is null || !ActiveWarmups.TryGetValue(solution, out WarmupState? state))
        {
            int projCount = solution?.Projects.Count() ?? 0;
            return new WarmupProgressSnapshot(
                State: "not_started",
                Phase: "idle",
                ProjectsWarmed: 0,
                ProjectsTotal: projCount,
                DocumentsIndexed: 0,
                DocumentsTotal: 0,
                ElapsedMs: 0,
                StartedAtUtc: null,
                CompletedAtUtc: null,
                Error: null);
        }

        return state.Tracker.CreateSnapshot();
    }

    /// <summary>Cancels any in-flight background warm-up for <paramref name="solution"/> (e.g. on workspace close/reload).</summary>
    public static void Cancel(Solution? solution)
    {
        if (solution is not null && ActiveWarmups.TryGetValue(solution, out WarmupState? state))
        {
            try
            {
                state.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Awaits the background warm-up task associated with <paramref name="solution"/> (for diagnostics/tests and --wait-warmup).</summary>
    internal static Task WaitForWarmupAsync(Solution? solution, CancellationToken cancellationToken = default)
    {
        if (solution is null || !ActiveWarmups.TryGetValue(solution, out WarmupState? state))
        {
            return Task.CompletedTask;
        }

        return state.Task.WaitAsync(cancellationToken);
    }

    internal static async Task<bool> WaitForWarmupAsync(Solution? solution, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (solution is null || !ActiveWarmups.TryGetValue(solution, out WarmupState? state))
        {
            return true;
        }

        try
        {
            await state.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private sealed record WarmupState(Task Task, CancellationTokenSource Cts, WarmupTracker Tracker);

    private sealed class WarmupTracker
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
        private readonly int _projectsTotal;
        private readonly int _documentsTotal;
        private int _projectsWarmed;
        private int _documentsIndexed;
        private string _state = "running";
        private string _phase = "compilations";
        private DateTimeOffset? _completedAtUtc;
        private double? _finalElapsedMs;
        private string? _error;

        public WarmupTracker(int projectsTotal, int documentsTotal)
        {
            _projectsTotal = projectsTotal;
            _documentsTotal = documentsTotal;
        }

        public void SetPhase(string phase) => Volatile.Write(ref _phase, phase);

        public void IncrementProjectsWarmed() => Interlocked.Increment(ref _projectsWarmed);

        public void SetDocumentsIndexed(int count) => Volatile.Write(ref _documentsIndexed, count);

        public void Complete()
        {
            _stopwatch.Stop();
            _finalElapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
            _completedAtUtc = DateTimeOffset.UtcNow;
            Volatile.Write(ref _phase, "complete");
            Volatile.Write(ref _state, "complete");
        }

        public void Cancel()
        {
            _stopwatch.Stop();
            _finalElapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
            _completedAtUtc = DateTimeOffset.UtcNow;
            Volatile.Write(ref _state, "canceled");
        }

        public void Fail(string message)
        {
            _stopwatch.Stop();
            _finalElapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
            _completedAtUtc = DateTimeOffset.UtcNow;
            Volatile.Write(ref _error, message);
            Volatile.Write(ref _state, "failed");
        }

        public WarmupProgressSnapshot CreateSnapshot()
        {
            string state = Volatile.Read(ref _state);
            string phase = Volatile.Read(ref _phase);
            int projectsWarmed = Volatile.Read(ref _projectsWarmed);
            int docsIndexed = Volatile.Read(ref _documentsIndexed);
            double elapsed = _finalElapsedMs ?? _stopwatch.Elapsed.TotalMilliseconds;
            return new WarmupProgressSnapshot(
                State: state,
                Phase: phase,
                ProjectsWarmed: projectsWarmed,
                ProjectsTotal: _projectsTotal,
                DocumentsIndexed: docsIndexed,
                DocumentsTotal: _documentsTotal,
                ElapsedMs: elapsed,
                StartedAtUtc: _startedAtUtc,
                CompletedAtUtc: _completedAtUtc,
                Error: Volatile.Read(ref _error));
        }
    }

    private static async Task WarmSolutionAsync(
        Solution solution,
        StaticAnalysisWorkspace? workspace,
        WorkspaceOverlayIndex? overlayIndex,
        WarmupTracker tracker,
        CancellationToken cancellationToken)
    {
        try
        {
            int maxParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

            // 0. Warm the in-memory overlay trigram + token index immediately (fast pure-text pass, ~30-80ms on 600 files)
            //    so ctx.search_text / ctx.read_window are zero-disk before semantic compilations even finish.
            if (workspace is not null && overlayIndex is not null)
            {
                tracker.SetPhase("overlay_index");
                int indexed = overlayIndex.Warm(workspace, cancellationToken);
                tracker.SetDocumentsIndexed(indexed);
            }

            tracker.SetPhase("compilations");
            ProjectDependencyGraph graph = solution.GetProjectDependencyGraph();
            Project[] orderedProjects = graph.GetTopologicallySortedProjects(cancellationToken)
                .Select(solution.GetProject)
                .Where(static p => p is not null)
                .Cast<Project>()
                .ToArray();

            // 1. Build compilations and bind declarations in topological waves so independent projects compile in
            //    parallel while downstream projects still share already-built upstream metadata.
            List<(Project Project, Compilation Compilation)> compiled = new(orderedProjects.Length);
            INamedTypeSymbol? referenceIndexProbe = null;
            HashSet<ProjectId> completedIds = new();
            List<Project> remaining = new(orderedProjects);
            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<Project> wave = new();
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    Project candidate = remaining[i];
                    bool ready = graph.GetProjectsThatThisProjectDirectlyDependsOn(candidate.Id)
                        .All(dep => completedIds.Contains(dep));
                    if (ready)
                    {
                        wave.Add(candidate);
                        remaining.RemoveAt(i);
                    }
                }

                if (wave.Count == 0)
                {
                    // Cycle fallback: drain remaining sequentially.
                    wave.Add(remaining[0]);
                    remaining.RemoveAt(0);
                }

                using SemaphoreSlim waveGate = new(maxParallelism, maxParallelism);
                Task<(Project Project, Compilation? Compilation)>[] waveTasks = wave.Select(async project =>
                {
                    await waveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                        _ = compilation?.GetDeclarationDiagnostics(cancellationToken);
                        tracker.IncrementProjectsWarmed();
                        return (project, compilation);
                    }
                    finally
                    {
                        waveGate.Release();
                    }
                }).ToArray();

                (Project Project, Compilation? Compilation)[] waveResults = await Task.WhenAll(waveTasks).ConfigureAwait(false);
                foreach ((Project project, Compilation? compilation) in waveResults)
                {
                    completedIds.Add(project.Id);
                    if (compilation is not null)
                    {
                        compiled.Add((project, compilation));
                        referenceIndexProbe ??= compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.IsVolatile");
                    }
                }
            }

            // 1b. Build the per-compilation SymbolNameIndex (type/member names, positional-record properties) so
            //     `--symbol Type.Member` resolves by dictionary lookup instead of a declaration scan.
            tracker.SetPhase("symbol_index");
            Parallel.ForEach(
                compiled,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellationToken },
                pair => _ = SymbolNameIndex.GetOrCreate(pair.Compilation, cancellationToken));

            // 2. Warm Roslyn's internal SymbolFinder declaration index and per-document SyntaxTreeIndex so the first
            //    `nav.find_references` (positional or --symbol) does not pay the cold multi-second index build.
            tracker.SetPhase("reference_index");
            using (SemaphoreSlim declGate = new(maxParallelism, maxParallelism))
            {
                Task[] declTasks = compiled.Select(async pair =>
                {
                    await declGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        _ = await SymbolFinder
                            .FindDeclarationsAsync(pair.Project, "__RoslynSkillsWarmupProbe__", ignoreCase: false, SymbolFilter.Type, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        declGate.Release();
                    }
                }).ToArray();
                await Task.WhenAll(declTasks).ConfigureAwait(false);
            }

            if (referenceIndexProbe is not null)
            {
                // A rare metadata type triggers SyntaxTreeIndex construction for every document in the solution
                // without matching user code or binding semantic models.
                _ = await SymbolFinder
                    .FindReferencesAsync(referenceIndexProbe, solution, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 3. Collect unique non-generated syntax trees (production projects first, then test projects) and warm
            //    IdentifierIndex (fast text scan) before CallSiteAnalysis (semantic operation cache).
            tracker.SetPhase("call_sites");
            StringComparer pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            HashSet<string> seenPaths = new(pathComparer);
            List<(Compilation Compilation, SyntaxTree Tree)> treesToWarm = new();

            foreach ((Project project, Compilation compilation) in compiled
                         .OrderBy(pair => IsLikelyTestProject(pair.Project) ? 1 : 0))
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string filePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);
                    if (CommandFileFilters.IsGeneratedPath(filePath) || !seenPaths.Add(filePath))
                    {
                        continue;
                    }

                    IdentifierIndex.Warm(tree);
                    treesToWarm.Add((compilation, tree));
                }
            }

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = maxParallelism,
                CancellationToken = cancellationToken,
            };

            Parallel.ForEach(treesToWarm, parallelOptions, item =>
            {
                _ = CallSiteAnalysis.GetCallSites(item.Compilation, item.Tree, includeObjectCreations: true, cancellationToken);
            });

            tracker.Complete();
        }
        catch (OperationCanceledException)
        {
            tracker.Cancel();
        }
        catch (Exception ex)
        {
            // Warm-up is best effort; a failing project surfaces on the first real query.
            tracker.Fail(ex.Message);
        }
    }

    private static bool IsLikelyTestProject(Project project)
    {
        string name = project.Name;
        return name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".Tests.", StringComparison.OrdinalIgnoreCase);
    }
}
