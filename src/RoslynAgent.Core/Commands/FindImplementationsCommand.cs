using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class FindImplementationsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_implementations",
        Summary: "Find type/member implementations in the current file anchored by line/column.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) ||
            !InputParsing.TryGetRequiredInt(input, "line", errors, out int line, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "column", errors, out int column, minValue: 1, maxValue: 1_000_000))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(null, new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 200, minValue: 1, maxValue: 2_000);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        ISymbol? anchorSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        if (anchorSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", "Unable to resolve a semantic symbol at the provided location.") });
        }

        List<ImplementationMatch> matches = new();

        if (anchorSymbol is INamedTypeSymbol namedType)
        {
            AddTypeImplementations(namedType, analysis, matches, maxResults, cancellationToken);
        }
        else if (anchorSymbol is IMethodSymbol methodSymbol)
        {
            AddMethodImplementations(methodSymbol, analysis, matches, maxResults, cancellationToken);
        }
        else if (anchorSymbol is IPropertySymbol propertySymbol)
        {
            AddPropertyImplementations(propertySymbol, analysis, matches, maxResults, cancellationToken);
        }
        else if (anchorSymbol is IEventSymbol eventSymbol)
        {
            AddEventImplementations(eventSymbol, analysis, matches, maxResults, cancellationToken);
        }
        else
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", $"Symbol kind '{anchorSymbol.Kind}' does not have implementation semantics.") });
        }

        object data = new
        {
            file_path = filePath,
            query = new { line, column, max_results = maxResults },
            symbol = new
            {
                symbol_display = anchorSymbol.ToDisplayString(),
                symbol_kind = anchorSymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(anchorSymbol),
            },
            total_matches = matches.Count,
            matches,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static void AddTypeImplementations(
        INamedTypeSymbol targetType,
        CommandFileAnalysis analysis,
        List<ImplementationMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        foreach (BaseTypeDeclarationSyntax typeDeclaration in analysis.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            INamedTypeSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
            if (candidate is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(candidate, targetType))
            {
                continue;
            }

            bool isImplementation = targetType.TypeKind switch
            {
                TypeKind.Interface => candidate.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, targetType)),
                _ => IsDerivedFrom(candidate, targetType),
            };

            if (!isImplementation)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = typeDeclaration.GetLocation().GetLineSpan();
            matches.Add(new ImplementationMatch(
                kind: "TypeDeclaration",
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString(),
                relationship: targetType.TypeKind == TypeKind.Interface ? "implements" : "derives"));
        }
    }

    private static void AddMethodImplementations(
        IMethodSymbol targetMethod,
        CommandFileAnalysis analysis,
        List<ImplementationMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        foreach (MethodDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IMethodSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
            if (candidate is null)
            {
                continue;
            }

            bool isImplementation =
                SymbolEqualityComparer.Default.Equals(candidate.OverriddenMethod, targetMethod) ||
                SymbolEqualityComparer.Default.Equals(candidate.OverriddenMethod?.OriginalDefinition, targetMethod.OriginalDefinition) ||
                candidate.ExplicitInterfaceImplementations.Any(m => SymbolEqualityComparer.Default.Equals(m, targetMethod)) ||
                candidate.ExplicitInterfaceImplementations.Any(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, targetMethod.OriginalDefinition));

            if (!isImplementation && targetMethod.ContainingType.TypeKind == TypeKind.Interface)
            {
                ISymbol? mapped = candidate.ContainingType.FindImplementationForInterfaceMember(targetMethod);
                isImplementation = SymbolEqualityComparer.Default.Equals(mapped, candidate);
            }

            if (!isImplementation)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
            matches.Add(new ImplementationMatch(
                kind: "MethodDeclaration",
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString(),
                relationship: targetMethod.ContainingType.TypeKind == TypeKind.Interface ? "implements" : "overrides"));
        }
    }

    private static void AddPropertyImplementations(
        IPropertySymbol targetProperty,
        CommandFileAnalysis analysis,
        List<ImplementationMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        foreach (PropertyDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IPropertySymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IPropertySymbol;
            if (candidate is null)
            {
                continue;
            }

            bool isImplementation =
                SymbolEqualityComparer.Default.Equals(candidate.OverriddenProperty, targetProperty) ||
                candidate.ExplicitInterfaceImplementations.Any(p => SymbolEqualityComparer.Default.Equals(p, targetProperty));

            if (!isImplementation && targetProperty.ContainingType.TypeKind == TypeKind.Interface)
            {
                ISymbol? mapped = candidate.ContainingType.FindImplementationForInterfaceMember(targetProperty);
                isImplementation = SymbolEqualityComparer.Default.Equals(mapped, candidate);
            }

            if (!isImplementation)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
            matches.Add(new ImplementationMatch(
                kind: "PropertyDeclaration",
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString(),
                relationship: targetProperty.ContainingType.TypeKind == TypeKind.Interface ? "implements" : "overrides"));
        }
    }

    private static void AddEventImplementations(
        IEventSymbol targetEvent,
        CommandFileAnalysis analysis,
        List<ImplementationMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        foreach (EventDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<EventDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IEventSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IEventSymbol;
            if (candidate is null)
            {
                continue;
            }

            bool isImplementation =
                SymbolEqualityComparer.Default.Equals(candidate.OverriddenEvent, targetEvent) ||
                candidate.ExplicitInterfaceImplementations.Any(e => SymbolEqualityComparer.Default.Equals(e, targetEvent));

            if (!isImplementation && targetEvent.ContainingType.TypeKind == TypeKind.Interface)
            {
                ISymbol? mapped = candidate.ContainingType.FindImplementationForInterfaceMember(targetEvent);
                isImplementation = SymbolEqualityComparer.Default.Equals(mapped, candidate);
            }

            if (!isImplementation)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
            matches.Add(new ImplementationMatch(
                kind: "EventDeclaration",
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString(),
                relationship: targetEvent.ContainingType.TypeKind == TypeKind.Interface ? "implements" : "overrides"));
        }
    }

    private static bool IsDerivedFrom(INamedTypeSymbol candidate, INamedTypeSymbol targetBaseType)
    {
        INamedTypeSymbol? current = candidate.BaseType;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, targetBaseType))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private sealed record ImplementationMatch(
        string kind,
        int line,
        int column,
        string symbol_display,
        string relationship);
}
