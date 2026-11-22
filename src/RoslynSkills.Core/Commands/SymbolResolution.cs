using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

        // Primary constructor parameter list: `class Foo(int x)` when anchored on `(` or `)`.
        if (node is ParameterListSyntax parameterList &&
            parameterList.Parent is TypeDeclarationSyntax typeDeclaration &&
            semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is INamedTypeSymbol primaryType)
        {
            IMethodSymbol? primaryConstructor = FindPrimaryConstructor(primaryType, typeDeclaration);
            return primaryConstructor ?? (ISymbol)primaryType;
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

    /// <summary>
    /// Returns the declaration's identifier location (not the start of modifiers/attributes) so coordinates returned
    /// by navigation commands can be passed straight into other <c>nav.*</c> commands.
    /// </summary>
    public static Location? GetDeclarationIdentifierLocation(ISymbol symbol, SyntaxNode? declarationNode = null)
    {
        if (declarationNode is not null)
        {
            Location? matching = symbol.Locations.FirstOrDefault(location =>
                location.IsInSource &&
                location.SourceTree == declarationNode.SyntaxTree &&
                declarationNode.Span.Contains(location.SourceSpan));
            if (matching is not null)
            {
                return matching;
            }
        }

        return symbol.Locations.FirstOrDefault(location => location.IsInSource)
            ?? declarationNode?.GetLocation();
    }

    /// <summary>
    /// Returns a stable, collision-free identifier for call-graph nodes (handles methods, fields, properties,
    /// and local functions whose documentation comment ID is null).
    /// </summary>
    public static string GetUniqueGraphSymbolId(ISymbol symbol)
    {
        string? docId = symbol.GetDocumentationCommentId();
        if (!string.IsNullOrWhiteSpace(docId))
        {
            return docId;
        }

        string display = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        Location? sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (sourceLocation?.SourceTree is not null)
        {
            string filePath = string.IsNullOrWhiteSpace(sourceLocation.SourceTree.FilePath)
                ? "<unknown>"
                : Path.GetFullPath(sourceLocation.SourceTree.FilePath);
            return $"{symbol.Kind}:{display}@{filePath}:{sourceLocation.SourceSpan.Start}";
        }

        return $"{symbol.Kind}:{display}";
    }

    public static IMethodSymbol? FindPrimaryConstructor(INamedTypeSymbol type, SyntaxNode? typeDeclaration = null)
    {
        foreach (IMethodSymbol ctor in type.InstanceConstructors)
        {
            if (ctor.IsImplicitlyDeclared)
            {
                continue;
            }

            foreach (SyntaxReference syntaxRef in ctor.DeclaringSyntaxReferences)
            {
                SyntaxNode syntax = syntaxRef.GetSyntax();
                if (typeDeclaration is not null)
                {
                    if (syntax == typeDeclaration)
                    {
                        return ctor;
                    }
                }
                else if (syntax is TypeDeclarationSyntax)
                {
                    return ctor;
                }
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
