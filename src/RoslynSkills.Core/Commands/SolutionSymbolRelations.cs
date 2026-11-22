using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSkills.Core.Commands;

/// <summary>A declaration related to an anchor symbol (an implementation or an override), with its source position.</summary>
internal sealed record RelatedDeclaration(
    string Kind,
    string FilePath,
    int Line,
    int Column,
    string SymbolDisplay,
    string Relationship);

/// <summary>
/// Implementation and override lookup across every project of a loaded solution. The commands used to compare the
/// declarations of the anchor file only, so an interface implemented in another file or project reported no
/// implementers at all.
/// </summary>
internal static class SolutionSymbolRelations
{
    public static async Task<IReadOnlyList<RelatedDeclaration>> FindImplementationsAsync(
        Solution solution,
        ISymbol target,
        CancellationToken cancellationToken)
    {
        List<RelatedDeclaration> found = new();
        switch (target)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceType:
                Add(found, await SymbolFinder.FindImplementationsAsync(interfaceType, solution, transitive: true, projects: null, cancellationToken).ConfigureAwait(false), "implements");
                Add(found, await SymbolFinder.FindDerivedInterfacesAsync(interfaceType, solution, transitive: true, projects: null, cancellationToken).ConfigureAwait(false), "implements");
                break;
            case INamedTypeSymbol type:
                Add(found, await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, projects: null, cancellationToken).ConfigureAwait(false), "derives");
                break;
            default:
                string relationship = target.ContainingType?.TypeKind == TypeKind.Interface ? "implements" : "overrides";
                Add(found, await SymbolFinder.FindImplementationsAsync(target, solution, projects: null, cancellationToken).ConfigureAwait(false), relationship);
                if (relationship == "overrides")
                {
                    Add(found, await SymbolFinder.FindOverridesAsync(target, solution, projects: null, cancellationToken).ConfigureAwait(false), relationship);
                }

                break;
        }

        return Order(found);
    }

    public static async Task<IReadOnlyList<RelatedDeclaration>> FindOverridesAsync(
        Solution solution,
        ISymbol target,
        CancellationToken cancellationToken)
    {
        // Overrides of any link in the chain are overrides of the root virtual member.
        ISymbol root = target switch
        {
            IMethodSymbol method => method.OverriddenMethod ?? method,
            IPropertySymbol property => property.OverriddenProperty ?? property,
            IEventSymbol @event => @event.OverriddenEvent ?? @event,
            _ => target,
        };

        List<RelatedDeclaration> found = new();
        Add(found, await SymbolFinder.FindOverridesAsync(root, solution, projects: null, cancellationToken).ConfigureAwait(false), "overrides");
        return Order(found);
    }

    private static void Add(List<RelatedDeclaration> found, IEnumerable<ISymbol> symbols, string relationship)
    {
        foreach (ISymbol symbol in symbols)
        {
            SyntaxReference? reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (reference is null)
            {
                continue;
            }

            SyntaxNode node = reference.GetSyntax();
            Location location = SymbolResolution.GetDeclarationIdentifierLocation(symbol, node) ?? node.GetLocation();
            Microsoft.CodeAnalysis.FileLinePositionSpan span = location.GetLineSpan();
            string filePath = string.IsNullOrWhiteSpace(location.SourceTree?.FilePath)
                ? (string.IsNullOrWhiteSpace(reference.SyntaxTree.FilePath)
                    ? "<unknown>"
                    : Path.GetFullPath(reference.SyntaxTree.FilePath))
                : Path.GetFullPath(location.SourceTree.FilePath);
            found.Add(new RelatedDeclaration(
                node.GetType().Name,
                filePath,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                symbol.ToDisplayString(),
                relationship));
        }
    }

    private static IReadOnlyList<RelatedDeclaration> Order(List<RelatedDeclaration> found)
        => found
            .DistinctBy(declaration => (declaration.FilePath, declaration.Line, declaration.Column, declaration.SymbolDisplay))
            .OrderBy(declaration => declaration.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(declaration => declaration.Line)
            .ThenBy(declaration => declaration.Column)
            .ToList();
}
