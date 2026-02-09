using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class FindReferencesCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_references",
        Summary: "Find same-symbol references anchored by line/column with semantic matching.",
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
            errors.Add(new CommandError(
                "file_not_found",
                $"Input file '{filePath}' does not exist."));
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
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 2, minValue: 0, maxValue: 20);
        bool includeDeclaration = InputParsing.GetOptionalBool(input, "include_declaration", defaultValue: true);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        ISymbol? targetSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        if (targetSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", "Unable to resolve a semantic symbol at the provided location.") });
        }

        string? targetSymbolId = CommandTextFormatting.GetStableSymbolId(targetSymbol);
        List<ReferenceMatch> matches = new();

        foreach (SyntaxToken token in analysis.Root.DescendantTokens(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!token.IsKind(SyntaxKind.IdentifierToken))
            {
                continue;
            }

            ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, analysis.SemanticModel, cancellationToken);
            if (!IsSameSymbol(symbol, targetSymbol))
            {
                continue;
            }

            bool isDeclaration = CommandTextFormatting.IsDeclarationToken(token);
            if (!includeDeclaration && isDeclaration)
            {
                continue;
            }

            LinePositionSpan span = analysis.SourceText.Lines.GetLinePositionSpan(token.Span);
            int tokenLine = span.Start.Line + 1;
            int tokenColumn = span.Start.Character + 1;
            string snippet = CommandTextFormatting.BuildSnippet(analysis.SourceText, tokenLine, contextLines);

            matches.Add(new ReferenceMatch(
                line: tokenLine,
                column: tokenColumn,
                syntax_kind: token.Parent?.Kind().ToString() ?? "Unknown",
                is_declaration: isDeclaration,
                snippet: snippet));

            if (matches.Count >= maxResults)
            {
                break;
            }
        }

        object data = new
        {
            file_path = filePath,
            query = new
            {
                line,
                column,
                include_declaration = includeDeclaration,
                max_results = maxResults,
            },
            symbol = new
            {
                symbol_display = targetSymbol.ToDisplayString(),
                symbol_kind = targetSymbol.Kind.ToString(),
                symbol_id = targetSymbolId,
            },
            total_matches = matches.Count,
            matches,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool IsSameSymbol(ISymbol? candidate, ISymbol target)
    {
        if (candidate is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(candidate, target))
        {
            return true;
        }

        return SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition);
    }

    private sealed record ReferenceMatch(
        int line,
        int column,
        string syntax_kind,
        bool is_declaration,
        string snippet);
}
