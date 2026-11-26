using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Runtime.CompilerServices;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Moves the cold-compilation, Roslyn symbol-index, and call-site cache cost of the first semantic query out of
/// that query: after a workspace is preloaded the host builds every project's compilation, warms Roslyn's
/// declaration and reference syntax-tree indices, and populates <see cref="IdentifierIndex"/> and
/// <see cref="CallSiteAnalysis"/> in the background. A query that arrives meanwhile shares the same cached work
/// instead of repeating it. <c>ROSLYNSKILLS_WARMUP=off</c> disables it.
/// </summary>
internal static class HotWorkspaceWarmup
{
    private static readonly ConditionalWeakTable<Solution, WarmupState> ActiveWarmups = new();

    public static void Start(Solution? solution, CancellationToken cancellationToken = default)
    {
        if (solution is null ||
            string.Equals(Environment.GetEnvironmentVariable("ROSLYNSKILLS_WARMUP")?.Trim(), "off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = ActiveWarmups.GetValue(solution, s =>
        {
            CancellationTokenSource cts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
            Task task = Task.Run(() => WarmSolutionAsync(s, cts.Token), CancellationToken.None);
            return new WarmupState(task, cts);
        });
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

    /// <summary>Awaits the background warm-up task associated with <paramref name="solution"/> (for diagnostics/tests).</summary>
    internal static Task WaitForWarmupAsync(Solution? solution, CancellationToken cancellationToken = default)
    {
        if (solution is null || !ActiveWarmups.TryGetValue(solution, out WarmupState? state))
        {
            return Task.CompletedTask;
        }

        return state.Task.WaitAsync(cancellationToken);
    }

    private sealed record WarmupState(Task Task, CancellationTokenSource Cts);

    private static async Task WarmSolutionAsync(Solution solution, CancellationToken cancellationToken)
    {
        try
        {
            int maxParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
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

            // 2. Warm Roslyn's internal SymbolFinder declaration index and per-document SyntaxTreeIndex so the first
            //    `nav.find_references` (positional or --symbol) does not pay the cold multi-second index build.
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
        }
        catch (Exception)
        {
            // Warm-up is best effort; a failing project surfaces on the first real query.
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
