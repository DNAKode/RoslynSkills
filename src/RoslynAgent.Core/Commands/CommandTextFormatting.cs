using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynAgent.Core.Commands;

internal static class CommandTextFormatting
{
    public static string BuildSnippet(SourceText sourceText, int centerLine, int contextLines)
    {
        int startLine = Math.Max(centerLine - contextLines, 1);
        int endLine = Math.Min(centerLine + contextLines, sourceText.Lines.Count);
        return BuildRangeSnippet(sourceText, startLine, endLine);
    }

    public static string BuildRangeSnippet(SourceText sourceText, int startLine, int endLine)
    {
        List<string> lines = new();
        for (int line = startLine; line <= endLine; line++)
        {
            string lineText = sourceText.Lines[line - 1].ToString();
            lines.Add($"{line,4}: {lineText}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static bool IsDeclarationToken(SyntaxToken token)
        => token.Parent switch
        {
            ClassDeclarationSyntax classDecl => classDecl.Identifier == token,
            StructDeclarationSyntax structDecl => structDecl.Identifier == token,
            InterfaceDeclarationSyntax interfaceDecl => interfaceDecl.Identifier == token,
            EnumDeclarationSyntax enumDecl => enumDecl.Identifier == token,
            RecordDeclarationSyntax recordDecl => recordDecl.Identifier == token,
            MethodDeclarationSyntax methodDecl => methodDecl.Identifier == token,
            ConstructorDeclarationSyntax ctorDecl => ctorDecl.Identifier == token,
            PropertyDeclarationSyntax propertyDecl => propertyDecl.Identifier == token,
            VariableDeclaratorSyntax variableDecl => variableDecl.Identifier == token,
            ParameterSyntax parameter => parameter.Identifier == token,
            DelegateDeclarationSyntax delegateDecl => delegateDecl.Identifier == token,
            EventDeclarationSyntax eventDecl => eventDecl.Identifier == token,
            NamespaceDeclarationSyntax namespaceDecl => namespaceDecl.Name.ToString().EndsWith(token.ValueText, StringComparison.Ordinal),
            FileScopedNamespaceDeclarationSyntax fileNamespaceDecl => fileNamespaceDecl.Name.ToString().EndsWith(token.ValueText, StringComparison.Ordinal),
            _ => false,
        };

    public static string? GetStableSymbolId(ISymbol symbol)
    {
        string? symbolId = symbol.GetDocumentationCommentId();
        if (!string.IsNullOrWhiteSpace(symbolId))
        {
            return symbolId;
        }

        string fallback = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    public static string[] GetContainingTypes(SyntaxToken token)
        => token.Parent?
            .Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.ValueText)
            .Reverse()
            .ToArray() ?? Array.Empty<string>();
}
