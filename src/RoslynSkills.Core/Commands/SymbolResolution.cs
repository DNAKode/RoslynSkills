using Microsoft.CodeAnalysis;

namespace RoslynSkills.Core.Commands;

internal static class SymbolResolution
{
    public static ISymbol? GetSymbolForToken(
        SyntaxToken token,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        SyntaxNode? node = token.Parent;
        if (node is null)
        {
            return null;
        }

        foreach (SyntaxNode candidateNode in node.AncestorsAndSelf())
        {
            SymbolInfo candidateSymbolInfo = semanticModel.GetSymbolInfo(candidateNode, cancellationToken);
            if (candidateSymbolInfo.Symbol is not null)
            {
                return candidateSymbolInfo.Symbol;
            }

            ISymbol? candidate = candidateSymbolInfo.CandidateSymbols.FirstOrDefault();
            if (candidate is not null)
            {
                return candidate;
            }
        }

        foreach (SyntaxNode candidateNode in node.AncestorsAndSelf())
        {
            ISymbol? declaredSymbol = semanticModel.GetDeclaredSymbol(candidateNode, cancellationToken);
            if (declaredSymbol is not null && IsDeclarationLocationForToken(declaredSymbol, token))
            {
                return declaredSymbol;
            }
        }

        return null;
    }

    private static bool IsDeclarationLocationForToken(ISymbol symbol, SyntaxToken token)
    {
        return symbol.Locations.Any(location =>
            location.IsInSource &&
            location.SourceTree == token.SyntaxTree &&
            location.SourceSpan.IntersectsWith(token.Span));
    }
}

