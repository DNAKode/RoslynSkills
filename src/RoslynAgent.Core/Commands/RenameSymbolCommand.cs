using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class RenameSymbolCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.rename_symbol",
        Summary: "Rename a symbol anchored by line/column in one C# file using semantic matching.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
        if (!InputParsing.TryGetRequiredString(input, "new_name", errors, out string newName))
        {
            return errors;
        }

        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property 'new_name' value '{newName}' is not a valid C# identifier."));
        }

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
            !InputParsing.TryGetRequiredInt(input, "column", errors, out int column, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredString(input, "new_name", errors, out string newName))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_input", $"Property 'new_name' value '{newName}' is not a valid C# identifier."),
                });
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("file_not_found", $"Input file '{filePath}' does not exist."),
                });
        }

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);

        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        SourceText sourceText = syntaxTree.GetText(cancellationToken);

        if (line > sourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({sourceText.Lines.Count})."),
                });
        }

        int position = GetPositionFromLineColumn(sourceText, line, column);
        SyntaxToken anchorToken = FindAnchorToken(root, position);
        if (!anchorToken.IsKind(SyntaxKind.IdentifierToken))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_target", "The provided line/column does not target an identifier token."),
                });
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "RoslynAgent.Rename",
            syntaxTrees: new[] { syntaxTree },
            references: CompilationReferenceBuilder.BuildMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);

        ISymbol? targetSymbol = SymbolResolution.GetSymbolForToken(anchorToken, semanticModel, cancellationToken);
        if (targetSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("symbol_not_found", "Unable to resolve a semantic symbol at the provided location."),
                });
        }

        SyntaxToken[] renameTokens = root
            .DescendantTokens(descendIntoTrivia: false)
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
            .Where(t => ShouldRenameToken(t, semanticModel, targetSymbol, cancellationToken))
            .ToArray();

        if (renameTokens.Length == 0)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("symbol_not_found", "No matching symbol occurrences were found to rename."),
                });
        }

        // Preserve existing trivia and only mutate identifier text.
        SyntaxNode newRoot = root.ReplaceTokens(
            renameTokens,
            (_, rewritten) => SyntaxFactory.Identifier(
                rewritten.LeadingTrivia,
                newName,
                rewritten.TrailingTrivia));

        bool changed = !string.Equals(root.ToFullString(), newRoot.ToFullString(), StringComparison.Ordinal);
        string updatedSource = newRoot.ToFullString();

        SyntaxTree updatedTree = CSharpSyntaxTree.ParseText(updatedSource, path: filePath, cancellationToken: cancellationToken);
        IReadOnlyList<Diagnostic> updatedDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken);
        NormalizedDiagnostic[] normalizedDiagnostics = CompilationDiagnostics.Normalize(updatedDiagnostics)
            .Take(maxDiagnostics)
            .ToArray();

        int diagnosticsErrors = normalizedDiagnostics.Count(d =>
            string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = normalizedDiagnostics.Count(d =>
            string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedSource, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        int[] changedLines = renameTokens
            .Select(t => sourceText.Lines.GetLineFromPosition(t.SpanStart).LineNumber + 1)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();

        object data = new
        {
            file_path = filePath,
            line,
            column,
            old_name = anchorToken.ValueText,
            new_name = newName,
            symbol_display = targetSymbol.ToDisplayString(),
            replacement_count = renameTokens.Length,
            changed_line_count = changedLines.Length,
            changed_lines = changedLines,
            apply_changes = apply,
            wrote_file = wroteFile,
            diagnostics_after_edit = new
            {
                total = updatedDiagnostics.Count,
                returned = normalizedDiagnostics.Length,
                errors = diagnosticsErrors,
                warnings = diagnosticsWarnings,
                diagnostics = normalizedDiagnostics,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool ShouldRenameToken(
        SyntaxToken token,
        SemanticModel semanticModel,
        ISymbol targetSymbol,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, semanticModel, cancellationToken);
        if (symbol is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(symbol, targetSymbol))
        {
            return true;
        }

        return SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, targetSymbol.OriginalDefinition);
    }

    private static int GetPositionFromLineColumn(SourceText sourceText, int line, int column)
    {
        TextLine textLine = sourceText.Lines[line - 1];
        int requestedOffset = Math.Max(0, column - 1);
        int maxOffset = Math.Max(0, textLine.Span.Length - 1);
        int clampedOffset = Math.Min(requestedOffset, maxOffset);
        return textLine.Start + clampedOffset;
    }

    private static SyntaxToken FindAnchorToken(SyntaxNode root, int position)
    {
        SyntaxToken token = root.FindToken(position);
        if (token.IsKind(SyntaxKind.IdentifierToken))
        {
            return token;
        }

        if (position > 0)
        {
            SyntaxToken previousToken = root.FindToken(position - 1);
            if (previousToken.IsKind(SyntaxKind.IdentifierToken))
            {
                return previousToken;
            }
        }

        return token;
    }
}
