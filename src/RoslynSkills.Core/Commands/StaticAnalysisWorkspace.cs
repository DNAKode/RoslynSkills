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

    public string WorkspacePath { get; }
    public string ResolvedWorkspacePath { get; }
    public string RootDirectory { get; }
    public string AnalysisMode { get; }
    public string WorkspaceKind { get; }
    public int ProjectCount { get; }
    public int DocumentCount { get; }
    public IReadOnlyList<string> WorkspaceDiagnostics { get; }
    public IReadOnlyList<SyntaxTree> SyntaxTrees { get; }
    public IReadOnlyDictionary<SyntaxTree, SemanticModel> SemanticModelsByTree { get; }
    public IReadOnlyDictionary<SyntaxTree, SourceText> SourceTextsByTree { get; }
    public IReadOnlyDictionary<string, SyntaxTree> SyntaxTreesByPath { get; }

    private StaticAnalysisWorkspace(
        string workspacePath,
        string resolvedWorkspacePath,
        string rootDirectory,
        string analysisMode,
        string workspaceKind,
        int projectCount,
        int documentCount,
        IReadOnlyList<string> workspaceDiagnostics,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        IReadOnlyDictionary<SyntaxTree, SemanticModel> semanticModelsByTree,
        IReadOnlyDictionary<SyntaxTree, SourceText> sourceTextsByTree,
        IReadOnlyDictionary<string, SyntaxTree> syntaxTreesByPath)
    {
        WorkspacePath = workspacePath;
        ResolvedWorkspacePath = resolvedWorkspacePath;
        RootDirectory = rootDirectory;
        AnalysisMode = analysisMode;
        WorkspaceKind = workspaceKind;
        ProjectCount = projectCount;
        DocumentCount = documentCount;
        WorkspaceDiagnostics = workspaceDiagnostics;
        SyntaxTrees = syntaxTrees;
        SemanticModelsByTree = semanticModelsByTree;
        SourceTextsByTree = sourceTextsByTree;
        SyntaxTreesByPath = syntaxTreesByPath;
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
            syntaxTrees: trees,
            semanticModelsByTree: semanticModelsByTree,
            sourceTextsByTree: sourceTextsByTree,
            syntaxTreesByPath: treesByPath);
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

        using MSBuildWorkspace msbuildWorkspace = MSBuildWorkspace.Create();
        msbuildWorkspace.WorkspaceFailed += (_, args) =>
        {
            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure &&
                !string.IsNullOrWhiteSpace(args.Diagnostic.Message))
            {
                diagnostics.Add(args.Diagnostic.Message);
            }
        };

        Solution solution;
        try
        {
            if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                Project project = await msbuildWorkspace.OpenProjectAsync(
                        workspaceFilePath,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                solution = project.Solution;
            }
            else if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
            {
                solution = await OpenSlnxAsSolutionAsync(msbuildWorkspace, workspaceFilePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                solution = await msbuildWorkspace.OpenSolutionAsync(
                        workspaceFilePath,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return (null, new CommandError("workspace_load_failed", $"Failed to load workspace '{workspaceFilePath}': {ex.Message}"));
        }

        List<SyntaxTree> trees = new();
        Dictionary<string, SyntaxTree> treesByPath = new(PathComparer);
        Dictionary<SyntaxTree, SemanticModel> semanticModelsByTree = new();
        Dictionary<SyntaxTree, SourceText> sourceTextsByTree = new();

        Project[] projects = solution.Projects
            .OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase)
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

        StaticAnalysisWorkspace workspace = new(
            workspacePath: requestedWorkspacePath,
            resolvedWorkspacePath: workspaceFilePath,
            rootDirectory: rootDirectory,
            analysisMode: analysisMode,
            workspaceKind: workspaceKind,
            projectCount: projects.Length,
            documentCount: documentCount,
            workspaceDiagnostics: diagnostics.Distinct(StringComparer.Ordinal).Take(30).ToArray(),
            syntaxTrees: trees,
            semanticModelsByTree: semanticModelsByTree,
            sourceTextsByTree: sourceTextsByTree,
            syntaxTreesByPath: treesByPath);
        return (workspace, null);
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

    private static bool IsMsBuildWorkspaceFile(string path)
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
