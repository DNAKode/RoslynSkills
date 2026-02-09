using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class FindOverridesCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_overrides",
        Summary: "Find overrides for a virtual/abstract member anchored by line/column.",
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
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
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

        List<OverrideMatch> matches = new();

        switch (anchorSymbol)
        {
            case IMethodSymbol methodSymbol:
                AddMethodOverrides(methodSymbol, analysis, matches, maxResults, cancellationToken);
                break;
            case IPropertySymbol propertySymbol:
                AddPropertyOverrides(propertySymbol, analysis, matches, maxResults, cancellationToken);
                break;
            case IEventSymbol eventSymbol:
                AddEventOverrides(eventSymbol, analysis, matches, maxResults, cancellationToken);
                break;
            default:
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_target", $"Symbol kind '{anchorSymbol.Kind}' does not support override lookup.") });
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

    private static void AddMethodOverrides(
        IMethodSymbol anchorMethod,
        CommandFileAnalysis analysis,
        List<OverrideMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IMethodSymbol baseMethod = anchorMethod.OverriddenMethod ?? anchorMethod;
        foreach (MethodDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IMethodSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
            if (candidate?.OverriddenMethod is null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(candidate.OverriddenMethod, baseMethod) &&
                !SymbolEqualityComparer.Default.Equals(candidate.OverriddenMethod.OriginalDefinition, baseMethod.OriginalDefinition))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
            matches.Add(new OverrideMatch(
                kind: "MethodDeclaration",
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString()));
        }
    }

    private static void AddPropertyOverrides(
        IPropertySymbol anchorProperty,
        CommandFileAnalysis analysis,
        List<OverrideMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IPropertySymbol baseProperty = anchorProperty.OverriddenProperty ?? anchorProperty;
        foreach (PropertyDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IPropertySymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IPropertySymbol;
            if (candidate?.OverriddenProperty is null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(candidate.OverriddenProperty, baseProperty) &&
                !SymbolEqualityComparer.Default.Equals(candidate.OverriddenProperty.OriginalDefinition, baseProperty.OriginalDefinition))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
            matches.Add(new OverrideMatch(
                kind: "PropertyDeclaration",
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString()));
        }
    }

    private static void AddEventOverrides(
        IEventSymbol anchorEvent,
        CommandFileAnalysis analysis,
        List<OverrideMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IEventSymbol baseEvent = anchorEvent.OverriddenEvent ?? anchorEvent;
        foreach (EventDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<EventDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IEventSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IEventSymbol;
            if (candidate?.OverriddenEvent is null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(candidate.OverriddenEvent, baseEvent) &&
                !SymbolEqualityComparer.Default.Equals(candidate.OverriddenEvent.OriginalDefinition, baseEvent.OriginalDefinition))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
            matches.Add(new OverrideMatch(
                kind: "EventDeclaration",
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString()));
        }
    }

    private sealed record OverrideMatch(
        string kind,
        int line,
        int column,
        string symbol_display);
}
