using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Xml.Linq;

namespace RoslynSkills.Core.Commands;

internal sealed record WorkspaceContextInfo(
    string mode,
    string resolution_source,
    string? requested_workspace_path,
    string? resolved_workspace_path,
    string? project_path,
    string? fallback_reason,
    IReadOnlyList<string> attempted_workspace_paths,
    IReadOnlyList<string> workspace_diagnostics,
    int workspace_load_duration_ms = 0,
    int msbuild_registration_duration_ms = 0,
    string workspace_cache_mode = "none",
    bool workspace_cache_hit = false,
    string? workspace_kind = null,
    int project_count = 0,
    int document_count = 0);

internal sealed record WorkspaceSemanticLoadResult(
    string file_path,
    string source,
    SyntaxTree syntax_tree,
    SyntaxNode root,
    SourceText source_text,
    Compilation compilation,
    SemanticModel semantic_model,
    string language,
    WorkspaceContextInfo workspace_context);

internal static class WorkspaceSemanticLoader
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly object MsBuildRegistrationLock = new();
    private static readonly object WorkspaceCacheLock = new();
    private static readonly Dictionary<string, CachedWorkspaceEntry> WorkspaceCache = new(PathComparer);
    private const int MaxCachedWorkspaces = 8;
    private static bool _msBuildRegistrationAttempted;
    private static string? _msBuildRegistrationError;

    public static async Task<WorkspaceSemanticLoadResult> LoadForFileAsync(
        string filePath,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        Stopwatch workspaceLoadTimer = Stopwatch.StartNew();
        string normalizedFilePath = NormalizePath(filePath);
        WorkspaceCandidatePlan candidatePlan = BuildCandidatePlan(normalizedFilePath, workspacePath);
        List<string> attemptedWorkspacePaths = new();
        List<string> workspaceDiagnostics = new();
        string? fallbackReason = candidatePlan.plan_error;
        int msbuildRegistrationDurationMs = 0;

        if (candidatePlan.candidates.Count > 0)
        {
            Stopwatch registrationTimer = Stopwatch.StartNew();
            if (TryEnsureMsBuildRegistered(out string? registrationError))
            {
                registrationTimer.Stop();
                msbuildRegistrationDurationMs = (int)registrationTimer.ElapsedMilliseconds;
                foreach (WorkspaceCandidate candidate in candidatePlan.candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attemptedWorkspacePaths.Add(candidate.path);
                    try
                    {
                        CachedWorkspaceLoadResult cachedWorkspace = await LoadWorkspaceStateAsync(
                            candidate,
                            normalizedFilePath,
                            cancellationToken).ConfigureAwait(false);
                        Document? document = FindDocument(cachedWorkspace.state.Solution, normalizedFilePath);
                        if (document is null)
                        {
                            fallbackReason = $"File '{normalizedFilePath}' was not found in workspace candidate '{candidate.path}'.";
                            AddDistinctLimited(workspaceDiagnostics, cachedWorkspace.state.Diagnostics, maxCount: 30);
                            continue;
                        }

                        SyntaxTree? syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                        SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                        if (syntaxTree is null || semanticModel is null)
                        {
                            fallbackReason = $"Workspace candidate '{candidate.path}' could not provide semantic context for '{normalizedFilePath}'.";
                            AddDistinctLimited(workspaceDiagnostics, cachedWorkspace.state.Diagnostics, maxCount: 30);
                            continue;
                        }

                        Compilation? compilation = semanticModel.Compilation;
                        compilation ??= await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

                        if (compilation is null)
                        {
                            fallbackReason = $"Workspace candidate '{candidate.path}' returned no compilation for '{normalizedFilePath}'.";
                            AddDistinctLimited(workspaceDiagnostics, cachedWorkspace.state.Diagnostics, maxCount: 30);
                            continue;
                        }

                        SourceText sourceText = await syntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                        WorkspaceContextInfo workspaceContext = new(
                            mode: "workspace",
                            resolution_source: candidatePlan.resolution_source,
                            requested_workspace_path: candidatePlan.requested_workspace_path,
                            resolved_workspace_path: candidate.path,
                            project_path: document.Project.FilePath,
                            fallback_reason: null,
                            attempted_workspace_paths: attemptedWorkspacePaths.ToArray(),
                            workspace_diagnostics: cachedWorkspace.state.Diagnostics,
                            workspace_load_duration_ms: (int)workspaceLoadTimer.ElapsedMilliseconds,
                            msbuild_registration_duration_ms: msbuildRegistrationDurationMs,
                            workspace_cache_mode: cachedWorkspace.workspace_cache_mode,
                            workspace_cache_hit: cachedWorkspace.workspace_cache_hit,
                            workspace_kind: candidate.kind,
                            project_count: cachedWorkspace.state.ProjectCount,
                            document_count: cachedWorkspace.state.DocumentCount);

                        return new WorkspaceSemanticLoadResult(
                            file_path: normalizedFilePath,
                            source: sourceText.ToString(),
                            syntax_tree: syntaxTree,
                            root: root,
                            source_text: sourceText,
                            compilation: compilation,
                            semantic_model: semanticModel,
                            language: document.Project.Language,
                            workspace_context: workspaceContext);
                    }
                    catch (Exception ex)
                    {
                        fallbackReason = $"Workspace candidate '{candidate.path}' failed to load: {ex.Message}";
                        AddDistinctLimited(workspaceDiagnostics, new[] { ex.Message }, maxCount: 30);
                    }
                }
            }
            else
            {
                registrationTimer.Stop();
                msbuildRegistrationDurationMs = (int)registrationTimer.ElapsedMilliseconds;
                fallbackReason = registrationError;
            }
        }

        string source = await File.ReadAllTextAsync(normalizedFilePath, cancellationToken).ConfigureAwait(false);
        string fallbackLanguage = CommandLanguageServices.DetectLanguageFromFilePath(normalizedFilePath);
        SyntaxTree fallbackTree = CommandLanguageServices.ParseSyntaxTree(
            source,
            normalizedFilePath,
            fallbackLanguage,
            cancellationToken);
        SyntaxNode fallbackRoot = await fallbackTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        SourceText fallbackSourceText = fallbackTree.GetText(cancellationToken);
        Compilation fallbackCompilation = CommandFileAnalysis.CreateCompilation(
            "RoslynSkills.Command",
            new[] { fallbackTree },
            fallbackLanguage);
        SemanticModel fallbackSemanticModel = fallbackCompilation.GetSemanticModel(fallbackTree);

        if (string.IsNullOrWhiteSpace(fallbackReason))
        {
            fallbackReason = candidatePlan.candidates.Count == 0
                ? "No workspace candidate (.csproj/.vbproj/.sln/.slnx) could be inferred for this file path."
                : "Workspace resolution failed; using ad-hoc file compilation.";
        }

        WorkspaceContextInfo fallbackContext = new(
            mode: "ad_hoc",
            resolution_source: candidatePlan.resolution_source,
            requested_workspace_path: candidatePlan.requested_workspace_path,
            resolved_workspace_path: null,
            project_path: null,
            fallback_reason: fallbackReason,
            attempted_workspace_paths: attemptedWorkspacePaths.ToArray(),
            workspace_diagnostics: workspaceDiagnostics.ToArray(),
            workspace_load_duration_ms: (int)workspaceLoadTimer.ElapsedMilliseconds,
            msbuild_registration_duration_ms: msbuildRegistrationDurationMs);

        return new WorkspaceSemanticLoadResult(
            file_path: normalizedFilePath,
            source: source,
            syntax_tree: fallbackTree,
            root: fallbackRoot,
            source_text: fallbackSourceText,
            compilation: fallbackCompilation,
            semantic_model: fallbackSemanticModel,
            language: fallbackLanguage,
            workspace_context: fallbackContext);
    }

    internal static void ClearProcessWorkspaceCacheForTests()
    {
        List<LoadedWorkspaceState> statesToDispose = new();
        lock (WorkspaceCacheLock)
        {
            statesToDispose.AddRange(WorkspaceCache.Values.Select(entry => entry.State));
            WorkspaceCache.Clear();
        }

        foreach (LoadedWorkspaceState state in statesToDispose)
        {
            state.Dispose();
        }
    }

    internal static string GetProcessWorkspaceCacheDebugInfoForTests(string candidatePath, string requestedFilePath)
    {
        string normalizedCandidatePath = NormalizePath(candidatePath);
        string normalizedRequestedFilePath = NormalizePath(requestedFilePath);

        lock (WorkspaceCacheLock)
        {
            if (!WorkspaceCache.TryGetValue(normalizedCandidatePath, out CachedWorkspaceEntry? entry))
            {
                return "cache-miss";
            }

            string? stalePath = entry.State.GetFirstStalePath(normalizedRequestedFilePath);
            return stalePath is null ? "cache-hit" : $"stale:{stalePath}";
        }
    }

    private static WorkspaceCandidatePlan BuildCandidatePlan(string filePath, string? workspacePath)
    {
        string fileDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
        HashSet<string> seen = new(PathComparer);
        List<WorkspaceCandidate> candidates = new();
        string resolutionSource = string.IsNullOrWhiteSpace(workspacePath) ? "auto" : "explicit";
        string? requestedWorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? null : NormalizePath(workspacePath!);
        string? planError = null;

        if (!string.IsNullOrWhiteSpace(requestedWorkspacePath))
        {
            if (File.Exists(requestedWorkspacePath))
            {
                string extension = Path.GetExtension(requestedWorkspacePath);
                if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCandidate(candidates, seen, requestedWorkspacePath, "project");
                }
                else if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCandidate(candidates, seen, requestedWorkspacePath, "solution");
                }
                else if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCandidate(candidates, seen, requestedWorkspacePath, "slnx");
                }
                else
                {
                    planError = $"Explicit workspace_path '{requestedWorkspacePath}' is not a .csproj/.vbproj/.sln/.slnx file.";
                }
            }
            else if (Directory.Exists(requestedWorkspacePath))
            {
                bool fileUnderWorkspaceRoot = IsPathUnderRoot(filePath, requestedWorkspacePath);
                IReadOnlyList<string> ancestors = fileUnderWorkspaceRoot
                    ? GetAncestorDirectories(fileDirectory, requestedWorkspacePath)
                    : Array.Empty<string>();

                AddCandidatesFromAncestors(ancestors, candidates, seen);

                if (candidates.Count == 0)
                {
                    AddTopLevelDirectoryCandidates(requestedWorkspacePath, candidates, seen);
                }

                if (candidates.Count == 0)
                {
                    AddRecursiveDirectoryCandidates(requestedWorkspacePath, candidates, seen);
                }

                if (candidates.Count == 0)
                {
                    planError = $"No .csproj/.vbproj/.sln/.slnx files were found under explicit workspace_path '{requestedWorkspacePath}'.";
                }
            }
            else
            {
                planError = $"Explicit workspace_path '{requestedWorkspacePath}' does not exist.";
            }

            return new WorkspaceCandidatePlan(resolutionSource, requestedWorkspacePath, planError, candidates);
        }

        string? autoStop = FindNearestGitRoot(fileDirectory);
        // If no git root is present, do not traverse arbitrarily to the filesystem root. In that case
        // workspace inference is ambiguous and we should avoid accidentally binding to unrelated parent
        // projects/solutions higher up the directory tree.
        IReadOnlyList<string> autoAncestors = string.IsNullOrWhiteSpace(autoStop)
            ? new[] { NormalizePath(fileDirectory) }
            : GetAncestorDirectories(fileDirectory, stopAtDirectoryInclusive: autoStop);
        AddCandidatesFromAncestors(autoAncestors, candidates, seen);
        if (candidates.Count == 0)
        {
            planError = $"No .csproj/.vbproj/.sln/.slnx files were found while traversing parent directories from '{fileDirectory}'.";
        }

        return new WorkspaceCandidatePlan(resolutionSource, requestedWorkspacePath, planError, candidates);
    }

    private static void AddCandidatesFromAncestors(
        IReadOnlyList<string> ancestors,
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen)
    {
        foreach (string directory in ancestors)
        {
            foreach (string solutionPath in EnumerateSolutions(directory))
            {
                TryAddCandidate(candidates, seen, solutionPath, "solution");
            }
        }

        foreach (string directory in ancestors)
        {
            foreach (string projectPath in EnumerateProjectFiles(directory, SearchOption.TopDirectoryOnly))
            {
                TryAddCandidate(candidates, seen, projectPath, "project");
            }
        }
    }

    private static void AddTopLevelDirectoryCandidates(
        string directoryPath,
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen)
    {
        foreach (string solutionPath in EnumerateSolutions(directoryPath, SearchOption.TopDirectoryOnly))
        {
            TryAddCandidate(candidates, seen, solutionPath, "solution");
        }

        foreach (string projectPath in EnumerateProjectFiles(directoryPath, SearchOption.TopDirectoryOnly))
        {
            TryAddCandidate(candidates, seen, projectPath, "project");
        }
    }

    private static void AddRecursiveDirectoryCandidates(
        string directoryPath,
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen)
    {
        foreach (string solutionPath in EnumerateSolutions(directoryPath, SearchOption.AllDirectories))
        {
            TryAddCandidate(candidates, seen, solutionPath, "solution");
        }

        foreach (string projectPath in EnumerateProjectFiles(directoryPath, SearchOption.AllDirectories))
        {
            TryAddCandidate(candidates, seen, projectPath, "project");
        }
    }

    private static IEnumerable<string> EnumerateSolutions(string directoryPath, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.EnumerateFiles(directoryPath, "*.sln", searchOption)
            .Concat(Directory.EnumerateFiles(directoryPath, "*.slnx", searchOption))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string directoryPath, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(directoryPath, "*.csproj", searchOption)
            .Concat(Directory.EnumerateFiles(directoryPath, "*.vbproj", searchOption))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryAddCandidate(
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen,
        string candidatePath,
        string kind)
    {
        string normalized = NormalizePath(candidatePath);
        if (!seen.Add(normalized))
        {
            return;
        }

        string extension = Path.GetExtension(normalized);
        if (string.Equals(kind, "solution", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            kind = "slnx";
        }

        candidates.Add(new WorkspaceCandidate(normalized, kind));
    }

    internal static bool TryEnsureMsBuildRegistered(out string? error)
    {
        lock (MsBuildRegistrationLock)
        {
            if (!_msBuildRegistrationAttempted)
            {
                _msBuildRegistrationAttempted = true;
                try
                {
                    if (!MSBuildLocator.IsRegistered)
                    {
                        VisualStudioInstance[] instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();

                        // Prefer .NET SDK MSBuild for the current runtime, since VS MSBuild can be behind preview TFMs
                        // and yield workspaces missing reference assemblies (e.g., CS0518 for System.String).
                        VisualStudioInstance? instance = instances
                            .Where(candidate => candidate.DiscoveryType == DiscoveryType.DotNetSdk)
                            .OrderByDescending(candidate => candidate.Version)
                            .FirstOrDefault()
                            ?? instances.OrderByDescending(candidate => candidate.Version).FirstOrDefault();

                        if (instance is not null)
                        {
                            MSBuildLocator.RegisterInstance(instance);
                        }
                        else
                        {
                            MSBuildLocator.RegisterDefaults();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _msBuildRegistrationError = $"MSBuild locator registration failed: {ex.Message}";
                }
            }
        }

        error = _msBuildRegistrationError;
        return string.IsNullOrWhiteSpace(error);
    }

    private static async Task<Solution> OpenSolutionAsync(
        MSBuildWorkspace workspace,
        WorkspaceCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (string.Equals(candidate.kind, "project", StringComparison.OrdinalIgnoreCase))
        {
            Project project = await workspace.OpenProjectAsync(candidate.path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return project.Solution;
        }

        if (string.Equals(candidate.kind, "slnx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(candidate.path), ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenSlnxAsSolutionAsync(workspace, candidate.path, cancellationToken).ConfigureAwait(false);
        }

        return await workspace.OpenSolutionAsync(candidate.path, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Solution> OpenSlnxAsSolutionAsync(
        MSBuildWorkspace workspace,
        string slnxPath,
        CancellationToken cancellationToken)
    {
        string fullPath = NormalizePath(slnxPath);
        string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;

        XDocument doc = XDocument.Load(fullPath);
        string[] projectPaths = doc
            .Descendants("Project")
            .Select(e => (string?)e.Attribute("Path"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => NormalizePath(Path.GetFullPath(Path.Combine(directory, p!))))
            .Distinct(PathComparer)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (projectPaths.Length == 0)
        {
            throw new InvalidOperationException($".slnx file '{fullPath}' did not contain any <Project Path=...> entries.");
        }

        Solution? last = null;
        HashSet<string> loaded = workspace.CurrentSolution.Projects
            .Select(p => p.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => NormalizePath(p!))
            .ToHashSet(PathComparer);

        foreach (string projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (loaded.Contains(projectPath))
            {
                continue;
            }

            try
            {
                Project project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                last = project.Solution;
                if (!string.IsNullOrWhiteSpace(project.FilePath))
                {
                    loaded.Add(NormalizePath(project.FilePath));
                }
            }
            catch (Exception ex) when (ex.Message.Contains("already part of the workspace", StringComparison.OrdinalIgnoreCase))
            {
                // MSBuildWorkspace can load referenced projects transitively. If the .slnx lists those projects too,
                // subsequent explicit opens can fail; treat that as non-fatal.
            }
        }

        return last ?? workspace.CurrentSolution;
    }

    private static bool TryGetCachedWorkspaceState(string candidatePath, string requestedFilePath, out LoadedWorkspaceState? state)
    {
        LoadedWorkspaceState? staleState = null;

        lock (WorkspaceCacheLock)
        {
            if (WorkspaceCache.TryGetValue(candidatePath, out CachedWorkspaceEntry? entry))
            {
                if (!entry.State.IsStale(requestedFilePath))
                {
                    entry.Touch();
                    state = entry.State;
                    return true;
                }

                WorkspaceCache.Remove(candidatePath);
                staleState = entry.State;
            }
        }

        staleState?.Dispose();
        state = null;
        return false;
    }

    private static async Task<CachedWorkspaceLoadResult> LoadWorkspaceStateAsync(
        WorkspaceCandidate candidate,
        string requestedFilePath,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedWorkspaceState(candidate.path, requestedFilePath, out LoadedWorkspaceState? cachedState) && cachedState is not null)
        {
            return new CachedWorkspaceLoadResult(cachedState, workspace_cache_mode: "process_balanced", workspace_cache_hit: true);
        }

        LoadedWorkspaceState loadedState = await CreateLoadedWorkspaceStateAsync(candidate, cancellationToken).ConfigureAwait(false);
        LoadedWorkspaceState? staleState = null;
        LoadedWorkspaceState? redundantState = null;
        List<LoadedWorkspaceState>? evictedStates = null;
        LoadedWorkspaceState selectedState = loadedState;
        bool cacheHit = false;

        lock (WorkspaceCacheLock)
        {
            if (WorkspaceCache.TryGetValue(candidate.path, out CachedWorkspaceEntry? existing))
            {
                if (!existing.State.IsStale(requestedFilePath))
                {
                    existing.Touch();
                    selectedState = existing.State;
                    cacheHit = true;
                    redundantState = loadedState;
                }
                else
                {
                    WorkspaceCache.Remove(candidate.path);
                    staleState = existing.State;
                }
            }

            if (!cacheHit)
            {
                CachedWorkspaceEntry inserted = new(candidate.path, loadedState);
                WorkspaceCache[candidate.path] = inserted;
                selectedState = inserted.State;
                evictedStates = TrimWorkspaceCacheIfNeeded();
            }
        }

        redundantState?.Dispose();
        staleState?.Dispose();
        if (evictedStates is not null)
        {
            foreach (LoadedWorkspaceState evictedState in evictedStates)
            {
                evictedState.Dispose();
            }
        }

        return new CachedWorkspaceLoadResult(selectedState, workspace_cache_mode: "process_balanced", workspace_cache_hit: cacheHit);
    }

    private static List<LoadedWorkspaceState> TrimWorkspaceCacheIfNeeded()
    {
        if (WorkspaceCache.Count <= MaxCachedWorkspaces)
        {
            return new List<LoadedWorkspaceState>();
        }

        List<LoadedWorkspaceState> evictedStates = new();
        foreach (CachedWorkspaceEntry entry in WorkspaceCache.Values
            .OrderBy(value => value.LastAccessUtc)
            .Take(Math.Max(0, WorkspaceCache.Count - MaxCachedWorkspaces))
            .ToArray())
        {
            WorkspaceCache.Remove(entry.CandidatePath);
            evictedStates.Add(entry.State);
        }

        return evictedStates;
    }

    private static async Task<LoadedWorkspaceState> CreateLoadedWorkspaceStateAsync(
        WorkspaceCandidate candidate,
        CancellationToken cancellationToken)
    {
        List<string> diagnostics = new();
        MSBuildWorkspace workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, args) =>
        {
            if (args.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
            {
                return;
            }

            string message = args.Diagnostic.Message;
            if (ShouldIncludeWorkspaceDiagnostic(message))
            {
                diagnostics.Add(message);
            }
        };

        try
        {
            Solution solution = await OpenSolutionAsync(workspace, candidate, cancellationToken).ConfigureAwait(false);
            int projectCount = solution.Projects.Count();
            int documentCount = solution.Projects.Sum(project => project.Documents.Count());
            IReadOnlyDictionary<string, long> trackedFileWriteTimesUtc = CaptureTrackedFileWriteTimes(candidate.path, solution);
            return new LoadedWorkspaceState(
                workspace,
                solution,
                trackedFileWriteTimesUtc,
                diagnostics.Distinct(StringComparer.Ordinal).Take(30).ToArray(),
                projectCount,
                documentCount);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }


    private static string? FindNearestGitRoot(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string candidate = NormalizePath(current.FullName);
            string dotGit = Path.Combine(candidate, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
    private static Document? FindDocument(Solution solution, string filePath)
    {
        string normalizedFilePath = NormalizePath(filePath);
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                string? candidatePath = document.FilePath;
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                if (PathComparer.Equals(NormalizePath(candidatePath), normalizedFilePath))
                {
                    return document;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetAncestorDirectories(string startDirectory, string? stopAtDirectoryInclusive)
    {
        List<string> directories = new();
        string? normalizedStop = string.IsNullOrWhiteSpace(stopAtDirectoryInclusive)
            ? null
            : NormalizePath(stopAtDirectoryInclusive);

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string currentPath = NormalizePath(current.FullName);
            directories.Add(currentPath);

            if (!string.IsNullOrWhiteSpace(normalizedStop) &&
                PathComparer.Equals(currentPath, normalizedStop))
            {
                break;
            }

            current = current.Parent;
        }

        return directories;
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        string normalizedPath = NormalizePath(path);
        string normalizedRoot = NormalizePath(root);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return true;
        }

        string rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        string rootWithAltSeparator = normalizedRoot + Path.AltDirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, comparison) ||
               normalizedPath.StartsWith(rootWithAltSeparator, comparison);
    }

    private static void AddDistinctLimited(List<string> destination, IEnumerable<string> source, int maxCount)
    {
        foreach (string value in source)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (destination.Contains(value, StringComparer.Ordinal))
            {
                continue;
            }

            destination.Add(value);
            if (destination.Count >= maxCount)
            {
                return;
            }
        }
    }

    private static bool ShouldIncludeWorkspaceDiagnostic(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (message.IndexOf("known high severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("known moderate severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("known low severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("known critical severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        if (message.IndexOf("NU1901", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("NU1902", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("NU1903", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("NU1904", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (fullPath.Length > root.Length)
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return fullPath;
    }

    private static IReadOnlyDictionary<string, long> CaptureTrackedFileWriteTimes(string workspacePath, Solution solution)
    {
        HashSet<string> trackedPaths = new(PathComparer)
        {
            NormalizePath(workspacePath),
        };

        foreach (Project project in solution.Projects)
        {
            if (!string.IsNullOrWhiteSpace(project.FilePath))
            {
                trackedPaths.Add(NormalizePath(project.FilePath));
            }

            AddTrackedDocumentPaths(trackedPaths, project.Documents);
        }

        Dictionary<string, long> snapshot = new(PathComparer);
        foreach (string path in trackedPaths)
        {
            if (!ShouldTrackWorkspaceFile(path))
            {
                continue;
            }

            snapshot[path] = GetTrackedFileWriteTimeUtcTicks(path);
        }

        return snapshot;
    }

    private static void AddTrackedDocumentPaths(HashSet<string> trackedPaths, IEnumerable<TextDocument> documents)
    {
        foreach (TextDocument document in documents)
        {
            if (!string.IsNullOrWhiteSpace(document.FilePath))
            {
                trackedPaths.Add(NormalizePath(document.FilePath));
            }
        }
    }

    private static long GetTrackedFileWriteTimeUtcTicks(string path)
    {
        return File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).Ticks
            : long.MinValue;
    }

    private static bool ShouldTrackWorkspaceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalizedPath = NormalizePath(path);
        string extension = Path.GetExtension(normalizedPath);
        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".editorconfig", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedPath.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedPath.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedPath.IndexOf($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedPath.IndexOf($"{Path.AltDirectorySeparatorChar}bin{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return true;
    }

    private sealed record WorkspaceCandidate(string path, string kind);

    private sealed record WorkspaceCandidatePlan(
        string resolution_source,
        string? requested_workspace_path,
        string? plan_error,
        IReadOnlyList<WorkspaceCandidate> candidates);

    private sealed record CachedWorkspaceLoadResult(
        LoadedWorkspaceState state,
        string workspace_cache_mode,
        bool workspace_cache_hit);

    private sealed class CachedWorkspaceEntry
    {
        public CachedWorkspaceEntry(string candidatePath, LoadedWorkspaceState state)
        {
            CandidatePath = candidatePath;
            State = state;
            LastAccessUtc = DateTime.UtcNow;
        }

        public string CandidatePath { get; }
        public LoadedWorkspaceState State { get; }
        public DateTime LastAccessUtc { get; private set; }

        public void Touch()
        {
            LastAccessUtc = DateTime.UtcNow;
        }
    }

    private sealed class LoadedWorkspaceState : IDisposable
    {
        public LoadedWorkspaceState(
            MSBuildWorkspace workspace,
            Solution solution,
            IReadOnlyDictionary<string, long> trackedFileWriteTimesUtc,
            IReadOnlyList<string> diagnostics,
            int projectCount,
            int documentCount)
        {
            Workspace = workspace;
            Solution = solution;
            TrackedFileWriteTimesUtc = trackedFileWriteTimesUtc;
            Diagnostics = diagnostics;
            ProjectCount = projectCount;
            DocumentCount = documentCount;
        }

        public MSBuildWorkspace Workspace { get; }
        public Solution Solution { get; }
        public IReadOnlyDictionary<string, long> TrackedFileWriteTimesUtc { get; }
        public IReadOnlyList<string> Diagnostics { get; }
        public int ProjectCount { get; }
        public int DocumentCount { get; }

        public bool IsStale(string requestedFilePath)
        {
            return GetFirstStalePath(requestedFilePath) is not null;
        }

        public string? GetFirstStalePath(string requestedFilePath)
        {
            foreach ((string path, long expectedTicks) in TrackedFileWriteTimesUtc)
            {
                bool mustValidate = IsAlwaysValidatedWorkspaceFile(path) ||
                    PathComparer.Equals(path, requestedFilePath);
                if (!mustValidate)
                {
                    continue;
                }

                if (GetTrackedFileWriteTimeUtcTicks(path) != expectedTicks)
                {
                    return path;
                }
            }

            return null;
        }

        public void Dispose()
        {
            Workspace.Dispose();
        }
    }

    private static bool IsAlwaysValidatedWorkspaceFile(string path)
    {
        string extension = Path.GetExtension(path);
        return !string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".csx", StringComparison.OrdinalIgnoreCase);
    }
}



