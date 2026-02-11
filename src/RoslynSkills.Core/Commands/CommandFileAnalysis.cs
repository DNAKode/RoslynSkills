using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSkills.Core.Commands;

internal sealed class CommandFileAnalysis
{
    public string FilePath { get; }
    public string Source { get; }
    public SyntaxTree SyntaxTree { get; }
    public SyntaxNode Root { get; }
    public SourceText SourceText { get; }
    public CSharpCompilation Compilation { get; }
    public SemanticModel SemanticModel { get; }
    public WorkspaceContextInfo WorkspaceContext { get; }

    private CommandFileAnalysis(
        string filePath,
        string source,
        SyntaxTree syntaxTree,
        SyntaxNode root,
        SourceText sourceText,
        CSharpCompilation compilation,
        SemanticModel semanticModel,
        WorkspaceContextInfo workspaceContext)
    {
        FilePath = filePath;
        Source = source;
        SyntaxTree = syntaxTree;
        Root = root;
        SourceText = sourceText;
        Compilation = compilation;
        SemanticModel = semanticModel;
        WorkspaceContext = workspaceContext;
    }

    public static async Task<CommandFileAnalysis> LoadAsync(
        string filePath,
        CancellationToken cancellationToken,
        string? workspacePath = null)
    {
        WorkspaceSemanticLoadResult result = await WorkspaceSemanticLoader.LoadForFileAsync(
            filePath,
            workspacePath,
            cancellationToken).ConfigureAwait(false);

        return new CommandFileAnalysis(
            result.file_path,
            result.source,
            result.syntax_tree,
            result.root,
            result.source_text,
            result.compilation,
            result.semantic_model,
            result.workspace_context);
    }

    public static CSharpCompilation CreateCompilation(string assemblyName, IReadOnlyList<SyntaxTree> syntaxTrees)
    {
        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: syntaxTrees,
            references: CompilationReferenceBuilder.BuildMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    public int GetPositionFromLineColumn(int line, int column)
    {
        TextLine textLine = SourceText.Lines[line - 1];
        int requestedOffset = Math.Max(0, column - 1);
        int maxOffset = Math.Max(0, textLine.Span.Length - 1);
        int clampedOffset = Math.Min(requestedOffset, maxOffset);
        return textLine.Start + clampedOffset;
    }

    public SyntaxToken FindAnchorToken(int line, int column)
    {
        int position = GetPositionFromLineColumn(line, column);
        return FindAnchorToken(Root, position);
    }

    public static SyntaxToken FindAnchorToken(SyntaxNode root, int position)
    {
        SyntaxToken token = root.FindToken(position);
        if (!token.IsKind(SyntaxKind.None))
        {
            return token;
        }

        if (position > 0)
        {
            return root.FindToken(position - 1);
        }

        return token;
    }
}

