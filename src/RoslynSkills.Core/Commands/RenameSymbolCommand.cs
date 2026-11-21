using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class RenameSymbolCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.rename_symbol",
        Summary: "Rename a symbol anchored by line/column across the loaded solution using Roslyn Renamer.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true,
        RunsOnHotWorkspace: true);

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
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
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
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(
                filePath,
                cancellationToken,
                workspacePath,
                workspaceHandle)
            .ConfigureAwait(false);
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

        if (analysis.Document is not null)
        {
            return await ExecuteSolutionRenameAsync(
                    analysis,
                    anchorToken,
                    targetSymbol,
                    line,
                    column,
                    newName,
                    workspacePath,
                    workspaceHandle,
                    requireWorkspace,
                    apply,
                    maxDiagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await ExecuteAdHocFileRenameAsync(
                analysis,
                anchorToken,
                targetSymbol,
                line,
                column,
                newName,
                workspacePath,
                workspaceHandle,
                requireWorkspace,
                apply,
                maxDiagnostics,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CommandExecutionResult> ExecuteSolutionRenameAsync(
        CommandFileAnalysis analysis,
        SyntaxToken anchorToken,
        ISymbol targetSymbol,
        int line,
        int column,
        string newName,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        bool apply,
        int maxDiagnostics,
        CancellationToken cancellationToken)
    {
        Solution initialSolution = analysis.Document!.Project.Solution;
        Solution solution = SolutionEditSupport.EnsureDocumentationCommentParsing(initialSolution);
        ISymbol solutionSymbol = targetSymbol;

        if (!ReferenceEquals(solution, initialSolution))
        {
            Document? anchorDoc = solution.GetDocument(analysis.Document.Id);
            if (anchorDoc is not null)
            {
                SyntaxNode? root = await anchorDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                SemanticModel? model = await anchorDoc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is not null && model is not null)
                {
                    SyntaxToken solutionToken = root.FindToken(anchorToken.SpanStart, findInsideTrivia: true);
                    ISymbol? rebound = SymbolResolution.GetSymbolForToken(solutionToken, model, cancellationToken);
                    if (rebound is not null)
                    {
                        solutionSymbol = rebound;
                    }
                }
            }
        }

        ISymbol renamableSymbol = SolutionEditSupport.ResolveRenamableSymbol(solutionSymbol);
        SymbolRenameOptions renameOptions = new(
            RenameOverloads: false,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false);

        Solution renamedSolution = await Renamer.RenameSymbolAsync(
                solution,
                renamableSymbol,
                renameOptions,
                newName,
                cancellationToken)
            .ConfigureAwait(false);

        SolutionEditPlan plan = await SolutionEditSupport.FinalizeSolutionChangesAsync(
                solution,
                renamedSolution,
                explicitFormatSpans: null,
                formatChangedSpans: true,
                cancellationToken)
            .ConfigureAwait(false);

        List<string> writtenPaths = new();
        if (apply)
        {
            foreach (ChangedSolutionFile changedFile in plan.ChangedFiles)
            {
                await EditFileText.WriteAsync(
                        changedFile.FilePath,
                        changedFile.UpdatedText,
                        changedFile.OriginalSnapshot.Encoding,
                        cancellationToken)
                    .ConfigureAwait(false);
                writtenPaths.Add(changedFile.FilePath);
            }
        }

        object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(
                writtenPaths,
                wroteFile: writtenPaths.Count > 0,
                allowAddDocuments: false,
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<DocumentId> diagnosticDocumentIds = plan.AffectedDocumentIds.Count > 0
            ? plan.AffectedDocumentIds
            : new[] { analysis.Document.Id };
        IReadOnlyList<Diagnostic> updatedDiagnostics = await WorkspaceDiagnostics.GetDiagnosticsForUpdatedSolutionAsync(
                plan.FinalSolution,
                diagnosticDocumentIds,
                cancellationToken)
            .ConfigureAwait(false);
        NormalizedDiagnostic[] allNormalized = CompilationDiagnostics.Normalize(updatedDiagnostics);
        NormalizedDiagnostic[] normalizedDiagnostics = allNormalized
            .Take(maxDiagnostics)
            .ToArray();

        int diagnosticsErrors = allNormalized.Count(d =>
            string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = allNormalized.Count(d =>
            string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        ChangedSolutionFile? anchorChangedFile = plan.ChangedFiles
            .FirstOrDefault(f => pathComparer.Equals(f.FilePath, analysis.FilePath));
        int[] anchorChangedLines = anchorChangedFile?.ChangedLines ?? Array.Empty<int>();
        int totalReplacements = plan.ChangedFiles.Sum(f => f.ReplacementCount);

        object data = new
        {
            file_path = analysis.FilePath,
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            rename_scope = "solution",
            line,
            column,
            old_name = anchorToken.ValueText,
            new_name = newName,
            symbol_display = renamableSymbol.ToDisplayString(),
            replacement_count = totalReplacements,
            changed_file_count = plan.ChangedFiles.Count,
            wrote_file_count = writtenPaths.Count,
            changed_line_count = anchorChangedLines.Length,
            changed_lines = anchorChangedLines,
            changed_files = plan.ChangedFiles
                .Select(f => new
                {
                    file_path = f.FilePath,
                    replacement_count = f.ReplacementCount,
                    changed_line_count = f.ChangedLines.Length,
                    changed_lines = f.ChangedLines,
                })
                .ToArray(),
            apply_changes = apply,
            wrote_file = writtenPaths.Count > 0,
            hot_workspace_refresh = hotWorkspaceRefresh,
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

    private static async Task<CommandExecutionResult> ExecuteAdHocFileRenameAsync(
        CommandFileAnalysis analysis,
        SyntaxToken anchorToken,
        ISymbol targetSymbol,
        int line,
        int column,
        string newName,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        bool apply,
        int maxDiagnostics,
        CancellationToken cancellationToken)
    {
        FileTextSnapshot originalSnapshot = await EditFileText.ReadAsync(analysis.FilePath, cancellationToken).ConfigureAwait(false);
        SyntaxToken[] renameTokens = analysis.Root
            .DescendantTokens(descendIntoTrivia: true)
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

        string updatedSource = EditFileText.PreserveLineEndings(originalSnapshot, newRoot.ToFullString());
        bool changed = !string.Equals(originalSnapshot.Text, updatedSource, StringComparison.Ordinal);

        IReadOnlyList<Diagnostic> updatedDiagnostics = WorkspaceDiagnostics.GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
        NormalizedDiagnostic[] allNormalized = CompilationDiagnostics.Normalize(updatedDiagnostics);
        NormalizedDiagnostic[] normalizedDiagnostics = allNormalized
            .Take(maxDiagnostics)
            .ToArray();

        int diagnosticsErrors = allNormalized.Count(d =>
            string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = allNormalized.Count(d =>
            string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        bool wroteFile = false;
        if (apply && changed)
        {
            await EditFileText.WriteAsync(
                    analysis.FilePath,
                    updatedSource,
                    originalSnapshot.Encoding,
                    cancellationToken)
                .ConfigureAwait(false);
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
            workspace_handle = workspaceHandle,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            rename_scope = "file_ad_hoc",
            line,
            column,
            old_name = anchorToken.ValueText,
            new_name = newName,
            symbol_display = targetSymbol.ToDisplayString(),
            replacement_count = renameTokens.Length,
            changed_file_count = changed ? 1 : 0,
            wrote_file_count = wroteFile ? 1 : 0,
            changed_line_count = changedLines.Length,
            changed_lines = changedLines,
            changed_files = changed
                ? new object[]
                {
                    new
                    {
                        file_path = analysis.FilePath,
                        replacement_count = renameTokens.Length,
                        changed_line_count = changedLines.Length,
                        changed_lines = changedLines,
                    },
                }
                : Array.Empty<object>(),
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
