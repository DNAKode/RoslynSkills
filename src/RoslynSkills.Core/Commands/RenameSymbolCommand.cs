using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

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

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);

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

        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath).ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, analysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

        if (line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count})."),
                });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        if (!anchorToken.IsKind(SyntaxKind.IdentifierToken))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_target", "The provided line/column does not target an identifier token."),
                });
        }

        ISymbol? targetSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        if (targetSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("symbol_not_found", "Unable to resolve a semantic symbol at the provided location."),
                });
        }

        SyntaxToken[] renameTokens = analysis.Root
            .DescendantTokens(descendIntoTrivia: false)
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
            .Where(t => ShouldRenameToken(t, analysis.SemanticModel, targetSymbol, cancellationToken))
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
        SyntaxNode newRoot = analysis.Root.ReplaceTokens(
            renameTokens,
            (_, rewritten) => SyntaxFactory.Identifier(
                rewritten.LeadingTrivia,
                newName,
                rewritten.TrailingTrivia));

        bool changed = !string.Equals(analysis.Source, newRoot.ToFullString(), StringComparison.Ordinal);
        string updatedSource = newRoot.ToFullString();

        IReadOnlyList<Diagnostic> updatedDiagnostics = WorkspaceDiagnostics.GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
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
            await File.WriteAllTextAsync(analysis.FilePath, updatedSource, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        int[] changedLines = renameTokens
            .Select(t => analysis.SourceText.Lines.GetLineFromPosition(t.SpanStart).LineNumber + 1)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();

        object data = new
        {
            file_path = analysis.FilePath,
            workspace_path = workspacePath,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
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
}