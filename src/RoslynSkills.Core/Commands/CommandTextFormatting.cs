using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSkills.Core.Commands;

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

    public static bool IsDeclarationToken(
        SyntaxToken token,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, semanticModel, cancellationToken);
        if (symbol is null)
        {
            return false;
        }

        return symbol.Locations.Any(location =>
            location.IsInSource &&
            location.SourceTree == token.SyntaxTree &&
            location.SourceSpan.IntersectsWith(token.Span));
    }

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

    public static string[] GetContainingTypes(
        SyntaxToken token,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, semanticModel, cancellationToken);
        if (symbol is null)
        {
            return Array.Empty<string>();
        }

        List<string> containingTypes = new();
        INamedTypeSymbol? current = symbol.ContainingType;
        while (current is not null)
        {
            containingTypes.Add(current.Name);
            current = current.ContainingType;
        }

        containingTypes.Reverse();
        return containingTypes.ToArray();
    }
}

