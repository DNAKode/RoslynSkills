using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

internal sealed class StaticAnalysisWorkspace
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public string WorkspacePath { get; }
    public string RootDirectory { get; }
    public IReadOnlyList<SyntaxTree> SyntaxTrees { get; }
    public IReadOnlyDictionary<SyntaxTree, SemanticModel> SemanticModelsByTree { get; }
    public IReadOnlyDictionary<SyntaxTree, SourceText> SourceTextsByTree { get; }
    public IReadOnlyDictionary<string, SyntaxTree> SyntaxTreesByPath { get; }

    private StaticAnalysisWorkspace(
        string workspacePath,
        string rootDirectory,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        IReadOnlyDictionary<SyntaxTree, SemanticModel> semanticModelsByTree,
        IReadOnlyDictionary<SyntaxTree, SourceText> sourceTextsByTree,
        IReadOnlyDictionary<string, SyntaxTree> syntaxTreesByPath)
    {
        WorkspacePath = workspacePath;
        RootDirectory = rootDirectory;
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
            rootDirectory: rootDirectory,
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
}
