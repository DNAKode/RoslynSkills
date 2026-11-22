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

    /// <summary>
    /// Formats a short hint listing candidate symbols on <paramref name="line"/> (1-based) with their exact
    /// <c>line:column</c> coordinates so callers who anchored on a keyword or return type can retry directly.
    /// </summary>
    public static string FormatLineSymbolHint(
        CommandFileAnalysis analysis,
        int line,
        Func<ISymbol, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        if (line < 1 || line > analysis.SourceText.Lines.Count)
        {
            return string.Empty;
        }

        Microsoft.CodeAnalysis.Text.TextLine textLine = analysis.SourceText.Lines[line - 1];
        if (textLine.Span.IsEmpty)
        {
            return string.Empty;
        }

        List<(bool IsDeclaration, int Column, string Label)> candidates = new();
        HashSet<int> seenColumns = new();
        foreach (SyntaxToken token in analysis.Root.DescendantTokens(textLine.Span, descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!textLine.Span.IntersectsWith(token.Span) ||
                !CommandLanguageServices.IsIdentifierToken(token, analysis.Language))
            {
                continue;
            }

            ISymbol? symbol = GetSymbolForToken(token, analysis.SemanticModel, cancellationToken);
            if (symbol is null || (predicate is not null && !predicate(symbol)))
            {
                continue;
            }

            Microsoft.CodeAnalysis.Text.LinePosition start = analysis.SourceText.Lines.GetLinePosition(token.SpanStart);
            int tokenLine = start.Line + 1;
            if (tokenLine != line)
            {
                continue;
            }

            int tokenColumn = start.Character + 1;
            if (!seenColumns.Add(tokenColumn))
            {
                continue;
            }

            bool isDeclaration = CommandTextFormatting.IsDeclarationToken(token, analysis.SemanticModel, cancellationToken);
            string name = string.IsNullOrWhiteSpace(symbol.Name) ? symbol.ToDisplayString() : symbol.Name;
            candidates.Add((isDeclaration, tokenColumn, $"{name} ({symbol.Kind} at {tokenLine}:{tokenColumn})"));
        }

        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        string formatted = string.Join(
            ", ",
            candidates
                .OrderByDescending(c => c.IsDeclaration)
                .ThenBy(c => c.Column)
                .Take(4)
                .Select(c => c.Label));
        return $" Candidate symbols on line {line}: {formatted}.";
    }
}
