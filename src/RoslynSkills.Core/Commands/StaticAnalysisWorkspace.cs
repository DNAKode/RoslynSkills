using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Xml.Linq;

namespace RoslynSkills.Core.Commands;

internal sealed class StaticAnalysisWorkspace
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly SemaphoreSlim MsBuildLoadGate = new(1, 1);

    public string WorkspacePath { get; }
    public string ResolvedWorkspacePath { get; }
    public string RootDirectory { get; }
    public string AnalysisMode { get; }
    public string WorkspaceKind { get; }
    public int ProjectCount { get; }
    public int DocumentCount { get; }
    public IReadOnlyList<string> WorkspaceDiagnostics { get; }
    public IReadOnlyList<UnresolvedProjectInfo> UnresolvedProjects { get; }
    public IReadOnlyList<SyntaxTree> SyntaxTrees { get; }
    public IReadOnlyList<string> ProjectFilePaths { get; }
    public IReadOnlyDictionary<SyntaxTree, SemanticModel> SemanticModelsByTree { get; }
    public IReadOnlyDictionary<SyntaxTree, SourceText> SourceTextsByTree { get; }
    public IReadOnlyDictionary<string, SyntaxTree> SyntaxTreesByPath { get; }
    public Solution? Solution { get; }

    private StaticAnalysisWorkspace(
        string workspacePath,
        string resolvedWorkspacePath,
        string rootDirectory,
        string analysisMode,
        string workspaceKind,
        int projectCount,
        int documentCount,
        IReadOnlyList<string> workspaceDiagnostics,
        IReadOnlyList<UnresolvedProjectInfo> unresolvedProjects,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        IReadOnlyList<string> projectFilePaths,
        IReadOnlyDictionary<SyntaxTree, SemanticModel> semanticModelsByTree,
        IReadOnlyDictionary<SyntaxTree, SourceText> sourceTextsByTree,
        IReadOnlyDictionary<string, SyntaxTree> syntaxTreesByPath,
        Solution? solution)
    {
        WorkspacePath = workspacePath;
        ResolvedWorkspacePath = resolvedWorkspacePath;
        RootDirectory = rootDirectory;
        AnalysisMode = analysisMode;
        WorkspaceKind = workspaceKind;
        ProjectCount = projectCount;
        DocumentCount = documentCount;
        WorkspaceDiagnostics = workspaceDiagnostics;
        UnresolvedProjects = unresolvedProjects;
        SyntaxTrees = syntaxTrees;
        ProjectFilePaths = projectFilePaths;
        SemanticModelsByTree = semanticModelsByTree;
        SourceTextsByTree = sourceTextsByTree;
        SyntaxTreesByPath = syntaxTreesByPath;
        Solution = solution;
    }

    /// <summary>
    /// Returns the hot workspace named by <c>workspace_handle</c> in <paramref name="input"/> when the host holds it,
    /// otherwise loads <paramref name="workspacePath"/> from disk.
    /// </summary>
    public static Task<(StaticAnalysisWorkspace? Workspace, CommandError? Error)> ResolveAsync(
        System.Text.Json.JsonElement input,
        string workspacePath,
        bool includeGenerated,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        if (workspaceHandle is not null &&
            WorkspaceHostStoreProvider.Current.TryGet(workspaceHandle, out HostedWorkspace? hosted) &&
            hosted is not null)
        {
            return Task.FromResult<(StaticAnalysisWorkspace?, CommandError?)>((hosted.Workspace, null));
        }

        return LoadAsync(workspacePath, includeGenerated, maxFiles, cancellationToken);
    }

    public static async Task<(StaticAnalysisWorkspace? Workspace, CommandError? Error)> LoadAsync(
        string workspacePath,
        bool includeGenerated,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        string normalizedWorkspacePath = Path.GetFullPath(workspacePath);
        if (!File.Exists(normalizedWorkspacePath) && !Directory.Exists(normalizedWorkspacePath))
        {
            return (null, new CommandError("workspace_not_found", $"Workspace path '{workspacePath}' does not exist."));
        }

        string? workspaceFilePath = ResolveMsBuildWorkspacePath(normalizedWorkspacePath);
        if (!string.IsNullOrWhiteSpace(workspaceFilePath))
        {
            return await LoadMsBuildWorkspaceAsync(
                    normalizedWorkspacePath,
                    workspaceFilePath,
                    includeGenerated,
                    maxFiles,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string rootDirectory = ResolveWorkspaceRoot(normalizedWorkspacePath);
        if (!Directory.Exists(rootDirectory))
        {
            return (null, new CommandError("directory_not_found", $"Resolved workspace root '{rootDirectory}' does not exist."));
        }

        string[] filePaths = Directory.EnumerateFiles(rootDirectory, "*.*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(CommandLanguageServices.IsSupportedSourceFile)
            .Where(path => includeGenerated || !CommandFileFilters.IsGeneratedPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxFiles))
            .ToArray();

        if (filePaths.Length == 0)
        {
            return (null, new CommandError("no_input_files", $"No C# or VB source files were found under '{rootDirectory}'."));
        }

        List<SyntaxTree> trees = new(filePaths.Length);
        Dictionary<string, SyntaxTree> treesByPath = new(PathComparer);
        foreach (string filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            SyntaxTree tree = CommandLanguageServices.ParseSyntaxTree(source, filePath, cancellationToken);
            trees.Add(tree);
            treesByPath[filePath] = tree;
        }

        Dictionary<SyntaxTree, SemanticModel> semanticModelsByTree = new();
        Dictionary<SyntaxTree, SourceText> sourceTextsByTree = new();
        foreach (IGrouping<string, SyntaxTree> languageGroup in trees.GroupBy(
                     tree => string.IsNullOrWhiteSpace(tree.Options.Language)
                         ? CommandLanguageServices.DetectLanguageFromFilePath(tree.FilePath)
                         : tree.Options.Language,
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxTree[] languageTrees = languageGroup.ToArray();
            Compilation compilation = CommandFileAnalysis.CreateCompilation(
                $"RoslynSkills.StaticAnalysis.{languageGroup.Key}",
                languageTrees,
                languageGroup.Key);

            foreach (SyntaxTree tree in languageTrees)
            {
                semanticModelsByTree[tree] = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                sourceTextsByTree[tree] = tree.GetText(cancellationToken);
            }
        }

        StaticAnalysisWorkspace workspace = new(
            workspacePath: normalizedWorkspacePath,
            resolvedWorkspacePath: normalizedWorkspacePath,
            rootDirectory: rootDirectory,
            analysisMode: "directory_scan",
            workspaceKind: Directory.Exists(normalizedWorkspacePath) ? "directory" : "file",
            projectCount: 0,
            documentCount: filePaths.Length,
            workspaceDiagnostics: Array.Empty<string>(),
            unresolvedProjects: Array.Empty<UnresolvedProjectInfo>(),
            syntaxTrees: trees,
            projectFilePaths: Array.Empty<string>(),
            semanticModelsByTree: semanticModelsByTree,
            sourceTextsByTree: sourceTextsByTree,
            syntaxTreesByPath: treesByPath,
            solution: null);
        return (workspace, null);
    }

    private static async Task<(StaticAnalysisWorkspace? Workspace, CommandError? Error)> LoadMsBuildWorkspaceAsync(
        string requestedWorkspacePath,
        string workspaceFilePath,
        bool includeGenerated,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceSemanticLoader.TryEnsureMsBuildRegistered(out string? registrationError))
        {
            return (null, new CommandError("msbuild_registration_failed", registrationError ?? "MSBuild registration failed."));
        }

        string extension = Path.GetExtension(workspaceFilePath);
        string workspaceKind = ResolveWorkspaceKind(workspaceFilePath);
        string analysisMode = string.Equals(workspaceKind, "project", StringComparison.OrdinalIgnoreCase)
            ? "msbuild_project"
            : "msbuild_solution";
        string rootDirectory = Path.GetDirectoryName(workspaceFilePath) ?? requestedWorkspacePath;
        List<string> diagnostics = new();

        await MsBuildLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MSBuildWorkspace msbuildWorkspace = WorkspaceSemanticLoader.CreateTrackedMsBuildWorkspace(
                diagnostics,
                includeAllFailureMessages: true);

            async Task<Solution> OpenSolutionForWorkspacePathAsync(MSBuildWorkspace workspace)
            {
                if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    Project project = await workspace.OpenProjectAsync(
                            workspaceFilePath,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return project.Solution;
                }

                if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    return await OpenSlnxAsSolutionAsync(workspace, workspaceFilePath, cancellationToken)
                        .ConfigureAwait(false);
                }

                return await workspace.OpenSolutionAsync(workspaceFilePath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            Solution solution;
            try
            {
                solution = await OpenSolutionForWorkspacePathAsync(msbuildWorkspace).ConfigureAwait(false);
                if (WorkspaceAutoRestore.IsEnabled)
                {
                    IReadOnlyList<UnresolvedProjectInfo> projectsNeedingRestore = WorkspaceAutoRestore.GetProjectsNeedingRestore(solution);
                    if (projectsNeedingRestore.Count > 0)
                    {
                        bool restored = await WorkspaceAutoRestore
                            .TryRestoreAsync(workspaceFilePath, diagnostics, cancellationToken, projectsNeedingRestore)
                            .ConfigureAwait(false);
                        if (restored)
                        {
                            msbuildWorkspace.Dispose();
                            msbuildWorkspace = WorkspaceSemanticLoader.CreateTrackedMsBuildWorkspace(
                                diagnostics,
                                includeAllFailureMessages: true);
                            solution = await OpenSolutionForWorkspacePathAsync(msbuildWorkspace).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                msbuildWorkspace.Dispose();
                return (null, new CommandError("workspace_load_failed", $"Failed to load workspace '{workspaceFilePath}': {ex.Message}"));
            }

            try
            {
                solution = SolutionEditSupport.EnsureDocumentationCommentParsing(solution);
                Project[] projects = solution.Projects
                    .OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (projects.Length == 0)
                {
                    return (null, new CommandError("workspace_load_failed", $"Workspace '{workspaceFilePath}' did not load any projects."));
                }

                return await FromSolutionAsync(
                        solution,
                        requestedWorkspacePath,
                        workspaceFilePath,
                        rootDirectory,
                        analysisMode,
                        workspaceKind,
                        diagnostics,
                        includeGenerated,
                        maxFiles,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                msbuildWorkspace.Dispose();
            }
        }
        finally
        {
            MsBuildLoadGate.Release();
        }
    }

    public static async Task<(StaticAnalysisWorkspace? Workspace, CommandError? Error)> LoadProjectsAsync(
        string rootDirectory,
        IReadOnlyList<string> projectFilePaths,
        bool includeGenerated,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        if (projectFilePaths.Count == 0)
        {
            return (null, new CommandError("workspace_not_found", $"No MSBuild project files were found under '{rootDirectory}'."));
        }

        if (!WorkspaceSemanticLoader.TryEnsureMsBuildRegistered(out string? registrationError))
        {
            return (null, new CommandError("msbuild_registration_failed", registrationError ?? "MSBuild registration failed."));
        }

        string normalizedRoot = Path.GetFullPath(rootDirectory);
        string[] orderedProjects = projectFilePaths
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (orderedProjects.Length == 1)
        {
            return await LoadMsBuildWorkspaceAsync(
                    normalizedRoot,
                    orderedProjects[0],
                    includeGenerated,
                    maxFiles,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await MsBuildLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<string> diagnostics = new();
            MSBuildWorkspace msbuildWorkspace = WorkspaceSemanticLoader.CreateTrackedMsBuildWorkspace(
                diagnostics,
                includeAllFailureMessages: true);

            async Task<Solution> OpenAllProjectsAsync(MSBuildWorkspace workspace)
            {
                Solution? last = null;
                HashSet<string> loaded = new(PathComparer);
                foreach (string projectPath in orderedProjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (loaded.Contains(projectPath))
                    {
                        continue;
                    }

                    try
                    {
                        Project project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        last = project.Solution;
                        foreach (Project p in last.Projects)
                        {
                            if (!string.IsNullOrWhiteSpace(p.FilePath))
                            {
                                loaded.Add(Path.GetFullPath(p.FilePath));
                            }
                        }
                    }
                    catch (Exception ex) when (ex.Message.Contains("already part of the workspace", StringComparison.OrdinalIgnoreCase))
                    {
                        // Referenced projects can be loaded transitively before their explicit entry is processed.
                    }
                }

                return last ?? workspace.CurrentSolution;
            }

            Solution solution;
            try
            {
                solution = await OpenAllProjectsAsync(msbuildWorkspace).ConfigureAwait(false);
                if (WorkspaceAutoRestore.IsEnabled)
                {
                    IReadOnlyList<UnresolvedProjectInfo> projectsNeedingRestore = WorkspaceAutoRestore.GetProjectsNeedingRestore(solution);
                    if (projectsNeedingRestore.Count > 0)
                    {
                        bool restoredAny = false;
                        foreach (UnresolvedProjectInfo unresolved in projectsNeedingRestore)
                        {
                            if (!string.IsNullOrWhiteSpace(unresolved.project_path) &&
                                await WorkspaceAutoRestore.TryRestoreAsync(unresolved.project_path, diagnostics, cancellationToken, new[] { unresolved }).ConfigureAwait(false))
                            {
                                restoredAny = true;
                            }
                        }

                        if (restoredAny)
                        {
                            msbuildWorkspace.Dispose();
                            msbuildWorkspace = WorkspaceSemanticLoader.CreateTrackedMsBuildWorkspace(
                                diagnostics,
                                includeAllFailureMessages: true);
                            solution = await OpenAllProjectsAsync(msbuildWorkspace).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                msbuildWorkspace.Dispose();
                return (null, new CommandError("workspace_load_failed", $"Failed to load projects under '{normalizedRoot}': {ex.Message}"));
            }

            try
            {
                solution = SolutionEditSupport.EnsureDocumentationCommentParsing(solution);
                if (!solution.Projects.Any())
                {
                    return (null, new CommandError("workspace_load_failed", $"Directory '{normalizedRoot}' did not load any MSBuild projects."));
                }

                return await FromSolutionAsync(
                        solution,
                        normalizedRoot,
                        normalizedRoot,
                        normalizedRoot,
                        analysisMode: "msbuild_directory",
                        workspaceKind: "directory",
                        diagnostics,
                        includeGenerated,
                        maxFiles,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                msbuildWorkspace.Dispose();
            }
        }
        finally
        {
            MsBuildLoadGate.Release();
        }
    }

    public static IReadOnlyList<Project> FindContainingProjectsForNewSourceFile(
        Solution solution,
        string filePath,
        bool includeGenerated = false,
        IReadOnlyList<ProjectSourceRules>? cachedRules = null)
    {
        IReadOnlyList<ProjectSourceRules> rules = cachedRules ?? ProjectSourceRules.FromSolution(solution);
        return ProjectSourceRules.FindMatchingProjects(rules, solution, filePath, includeGenerated);
    }

    public async Task<(StaticAnalysisWorkspace? Workspace, IReadOnlyList<string> UpdatedPaths, IReadOnlyList<string> AddedPaths, IReadOnlyList<string> RemovedPaths, CommandError? Error)> ApplyDocumentChangesAsync(
        IEnumerable<string> modifiedOrAddedPaths,
        IEnumerable<string> removedPaths,
        bool includeGenerated,
        int maxFiles,
        bool allowAddDocuments,
        IReadOnlyList<ProjectSourceRules>? projectSourceRules,
        CancellationToken cancellationToken)
    {
        if (Solution is null)
        {
            return (null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new CommandError("workspace_update_not_supported", "Incremental document update requires a solution-backed hot workspace."));
        }

        IReadOnlyList<ProjectSourceRules> effectiveRules = projectSourceRules ?? ProjectSourceRules.FromSolution(Solution, RootDirectory);
        Solution updatedSolution = Solution;
        List<string> updatedPaths = new();
        List<string> addedPaths = new();
        List<string> appliedRemovedPaths = new();

        HashSet<string> removedSet = new(PathComparer);
        foreach (string removedPath in removedPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(PathComparer))
        {
            Document[] existingDocs = FindDocuments(updatedSolution, removedPath).ToArray();
            if (existingDocs.Length > 0)
            {
                foreach (Document document in existingDocs)
                {
                    updatedSolution = updatedSolution.RemoveDocument(document.Id);
                }

                appliedRemovedPaths.Add(removedPath);
                removedSet.Add(removedPath);
            }
            else
            {
                // Still track as removed so the host store cleans up its tracking tables idempotently.
                appliedRemovedPaths.Add(removedPath);
                removedSet.Add(removedPath);
            }
        }

        foreach (string sourcePath in modifiedOrAddedPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(PathComparer))
        {
            if (removedSet.Contains(sourcePath))
            {
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                // A file queued for update/add disappeared before we read it: remove if tracked, otherwise skip.
                Document[] vanishedDocs = FindDocuments(updatedSolution, sourcePath).ToArray();
                if (vanishedDocs.Length > 0)
                {
                    foreach (Document document in vanishedDocs)
                    {
                        updatedSolution = updatedSolution.RemoveDocument(document.Id);
                    }

                    appliedRemovedPaths.Add(sourcePath);
                    removedSet.Add(sourcePath);
                }

                continue;
            }

            FileTextSnapshot snapshot = await EditFileText.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            SourceText newText = SourceText.From(snapshot.Text, snapshot.Encoding);

            Document[] documents = FindDocuments(updatedSolution, sourcePath).ToArray();
            if (documents.Length > 0)
            {
                foreach (Document document in documents)
                {
                    updatedSolution = updatedSolution.WithDocumentText(document.Id, newText);
                }

                updatedPaths.Add(sourcePath);
                continue;
            }

            if (!allowAddDocuments)
            {
                return (null, updatedPaths, addedPaths, appliedRemovedPaths, new CommandError("document_not_found", $"Document '{sourcePath}' was not found in the loaded solution."));
            }

            IReadOnlyList<Project> containingProjects = FindContainingProjectsForNewSourceFile(
                updatedSolution,
                sourcePath,
                includeGenerated,
                effectiveRules);
            if (containingProjects.Count == 0)
            {
                continue;
            }

            string fileName = Path.GetFileName(sourcePath);
            foreach (Project targetProject in containingProjects)
            {
                Project? currentProject = updatedSolution.GetProject(targetProject.Id);
                if (currentProject is null || string.IsNullOrWhiteSpace(currentProject.FilePath))
                {
                    continue;
                }

                string projectDir = Path.GetDirectoryName(Path.GetFullPath(currentProject.FilePath)) ?? string.Empty;
                string fileDir = Path.GetDirectoryName(sourcePath) ?? projectDir;
                string relativeDir = Path.GetRelativePath(projectDir, fileDir);
                string[] folders = string.IsNullOrWhiteSpace(relativeDir) ||
                                   string.Equals(relativeDir, ".", StringComparison.Ordinal) ||
                                   relativeDir.StartsWith("..", StringComparison.Ordinal)
                    ? Array.Empty<string>()
                    : relativeDir.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

                Document addedDocument = currentProject.AddDocument(fileName, newText, folders, sourcePath);
                updatedSolution = addedDocument.Project.Solution;
            }

            updatedPaths.Add(sourcePath);
            addedPaths.Add(sourcePath);
        }

        if (updatedPaths.Count == 0 && appliedRemovedPaths.Count == 0)
        {
            return (this, updatedPaths, addedPaths, appliedRemovedPaths, null);
        }

        (StaticAnalysisWorkspace? workspace, CommandError? error) = await FromSolutionAsync(
                updatedSolution,
                WorkspacePath,
                ResolvedWorkspacePath,
                RootDirectory,
                AnalysisMode,
                WorkspaceKind,
                WorkspaceDiagnostics,
                includeGenerated,
                maxFiles,
                cancellationToken)
            .ConfigureAwait(false);
        return (workspace, updatedPaths, addedPaths, appliedRemovedPaths, error);
    }

    private static async Task<(StaticAnalysisWorkspace? Workspace, CommandError? Error)> FromSolutionAsync(
        Solution solution,
        string requestedWorkspacePath,
        string workspaceFilePath,
        string rootDirectory,
        string analysisMode,
        string workspaceKind,
        IReadOnlyList<string> diagnostics,
        bool includeGenerated,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        List<SyntaxTree> trees = new();
        Dictionary<string, SyntaxTree> treesByPath = new(PathComparer);
        Dictionary<SyntaxTree, SemanticModel> semanticModelsByTree = new();
        Dictionary<SyntaxTree, SourceText> sourceTextsByTree = new();
        Dictionary<ProjectId, Compilation> compilationsByProject = new();
        Project[] projects = solution.Projects
            .OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] projectFilePaths = projects
            .Select(project => project.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();
        int documentCount = projects.Sum(project => project.Documents.Count());

        foreach (Project project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            compilationsByProject[project.Id] = compilation;

            foreach (Document document in project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (trees.Count >= Math.Max(1, maxFiles))
                {
                    break;
                }

                string? filePath = document.FilePath;
                if (string.IsNullOrWhiteSpace(filePath) ||
                    !CommandLanguageServices.IsSupportedSourceFile(filePath) ||
                    (!includeGenerated && CommandFileFilters.IsGeneratedPath(filePath)))
                {
                    continue;
                }

                SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                SourceText? sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (tree is null || sourceText is null)
                {
                    continue;
                }

                SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                trees.Add(tree);
                treesByPath[Path.GetFullPath(filePath)] = tree;
                semanticModelsByTree[tree] = semanticModel;
                sourceTextsByTree[tree] = sourceText;
            }

            if (trees.Count >= Math.Max(1, maxFiles))
            {
                break;
            }
        }

        if (trees.Count == 0)
        {
            return (null, new CommandError("no_input_files", $"No C# or VB source files were found in workspace '{workspaceFilePath}'."));
        }

        IReadOnlyList<UnresolvedProjectInfo> unresolvedProjects = WorkspaceProjectResolution.InspectSolution(
            solution,
            compilationsByProject);
        IReadOnlyList<string> mergedDiagnostics = WorkspaceProjectResolution.MergeDiagnostics(
            diagnostics,
            unresolvedProjects);

        StaticAnalysisWorkspace workspace = new(
            workspacePath: requestedWorkspacePath,
            resolvedWorkspacePath: workspaceFilePath,
            rootDirectory: rootDirectory,
            analysisMode: analysisMode,
            workspaceKind: workspaceKind,
            projectCount: projects.Length,
            documentCount: documentCount,
            workspaceDiagnostics: mergedDiagnostics,
            unresolvedProjects: unresolvedProjects,
            syntaxTrees: trees,
            projectFilePaths: projectFilePaths,
            semanticModelsByTree: semanticModelsByTree,
            sourceTextsByTree: sourceTextsByTree,
            syntaxTreesByPath: treesByPath,
            solution: solution);
        return (workspace, null);
    }

    private static IEnumerable<Document> FindDocuments(Solution solution, string filePath)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                if (!string.IsNullOrWhiteSpace(document.FilePath) &&
                    PathComparer.Equals(Path.GetFullPath(document.FilePath), normalizedPath))
                {
                    yield return document;
                }
            }
        }
    }

    public bool TryGetTreeByPath(string filePath, out SyntaxTree? syntaxTree)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        if (SyntaxTreesByPath.TryGetValue(normalizedPath, out SyntaxTree? tree))
        {
            syntaxTree = tree;
            return true;
        }

        syntaxTree = null;
        return false;
    }

    public bool TryGetPosition(string filePath, int line, int column, out int position, out string? error)
    {
        position = 0;
        error = null;

        if (!TryGetTreeByPath(filePath, out SyntaxTree? tree) || tree is null)
        {
            error = $"File '{Path.GetFullPath(filePath)}' was not found in analysis workspace scope '{RootDirectory}'.";
            return false;
        }

        if (!SourceTextsByTree.TryGetValue(tree, out SourceText? sourceText))
        {
            error = $"Source text is unavailable for '{Path.GetFullPath(filePath)}'.";
            return false;
        }

        if (line < 1 || line > sourceText.Lines.Count)
        {
            error = $"Requested line '{line}' is outside file bounds (1..{sourceText.Lines.Count}) for '{Path.GetFullPath(filePath)}'.";
            return false;
        }

        TextLine textLine = sourceText.Lines[line - 1];
        int requestedOffset = Math.Max(0, column - 1);
        int maxOffset = Math.Max(0, textLine.Span.Length - 1);
        int clampedOffset = Math.Min(requestedOffset, maxOffset);
        position = textLine.Start + clampedOffset;
        return true;
    }

    private static string ResolveWorkspaceRoot(string normalizedWorkspacePath)
    {
        if (Directory.Exists(normalizedWorkspacePath))
        {
            return normalizedWorkspacePath;
        }

        string extension = Path.GetExtension(normalizedWorkspacePath);
        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(normalizedWorkspacePath) ?? normalizedWorkspacePath;
        }

        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(normalizedWorkspacePath) ?? normalizedWorkspacePath;
        }

        return Path.GetDirectoryName(normalizedWorkspacePath) ?? normalizedWorkspacePath;
    }

    private static string? ResolveMsBuildWorkspacePath(string normalizedWorkspacePath)
    {
        if (File.Exists(normalizedWorkspacePath) && IsMsBuildWorkspaceFile(normalizedWorkspacePath))
        {
            return normalizedWorkspacePath;
        }

        if (!Directory.Exists(normalizedWorkspacePath))
        {
            return null;
        }

        string[] solutionCandidates = Directory.EnumerateFiles(normalizedWorkspacePath, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(normalizedWorkspacePath, "*.slnx", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (solutionCandidates.Length > 0)
        {
            return Path.GetFullPath(solutionCandidates[0]);
        }

        string[] projectCandidates = Directory.EnumerateFiles(normalizedWorkspacePath, "*.csproj", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(normalizedWorkspacePath, "*.vbproj", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return projectCandidates.Length == 0 ? null : Path.GetFullPath(projectCandidates[0]);
    }

    public static bool IsMsBuildWorkspaceFile(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWorkspaceKind(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase))
        {
            return "project";
        }

        return string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            ? "slnx"
            : "solution";
    }

    private static async Task<Solution> OpenSlnxAsSolutionAsync(
        MSBuildWorkspace workspace,
        string slnxPath,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(slnxPath);
        string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;

        XDocument doc = XDocument.Load(fullPath);
        string[] projectPaths = doc
            .Descendants("Project")
            .Select(e => (string?)e.Attribute("Path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.Combine(directory, path!)))
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (projectPaths.Length == 0)
        {
            throw new InvalidOperationException($".slnx file '{fullPath}' did not contain any <Project Path=...> entries.");
        }

        Solution? last = null;
        HashSet<string> loaded = workspace.CurrentSolution.Projects
            .Select(project => project.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
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
                Project project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                last = project.Solution;
                if (!string.IsNullOrWhiteSpace(project.FilePath))
                {
                    loaded.Add(Path.GetFullPath(project.FilePath));
                }
            }
            catch (Exception ex) when (ex.Message.Contains("already part of the workspace", StringComparison.OrdinalIgnoreCase))
            {
                // Referenced projects can be loaded transitively before their explicit .slnx entry is processed.
            }
        }

        return last ?? workspace.CurrentSolution;
    }
}
