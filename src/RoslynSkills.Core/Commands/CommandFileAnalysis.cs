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

    private CommandFileAnalysis(
        string filePath,
        string source,
        SyntaxTree syntaxTree,
        SyntaxNode root,
        SourceText sourceText,
        CSharpCompilation compilation,
        SemanticModel semanticModel)
    {
        FilePath = filePath;
        Source = source;
        SyntaxTree = syntaxTree;
        Root = root;
        SourceText = sourceText;
        Compilation = compilation;
        SemanticModel = semanticModel;
    }

    public static async Task<CommandFileAnalysis> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        SourceText sourceText = syntaxTree.GetText(cancellationToken);
        CSharpCompilation compilation = CreateCompilation("RoslynSkills.Command", new[] { syntaxTree });
        SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);

        return new CommandFileAnalysis(
            filePath,
            source,
            syntaxTree,
            root,
            sourceText,
            compilation,
            semanticModel);
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

