using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using System.Text;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class EditTransactionCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.transaction",
        Summary: "Apply a multi-file edit transaction with immediate diagnostics and optional dry-run.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!input.TryGetProperty("operations", out JsonElement operationsProperty) ||
            operationsProperty.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'operations' is required and must be an array."));
            return errors;
        }

        if (operationsProperty.GetArrayLength() == 0)
        {
            errors.Add(new CommandError("invalid_input", "Property 'operations' must contain at least one operation."));
            return errors;
        }

        int index = 0;
        foreach (JsonElement operationElement in operationsProperty.EnumerateArray())
        {
            if (operationElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new CommandError("invalid_input", $"Operation at index {index} must be an object."));
                index++;
                continue;
            }

            if (!TryGetRequiredOperationString(operationElement, "file_path", index, errors, out string filePath))
            {
                index++;
                continue;
            }

            string normalizedFilePath = Path.GetFullPath(filePath);
            if (!File.Exists(normalizedFilePath))
            {
                errors.Add(new CommandError(
                    "file_not_found",
                    $"Operation at index {index} references file '{normalizedFilePath}' which does not exist."));
                index++;
                continue;
            }

            if (!TryGetOperationKind(operationElement, index, errors, out string operationKind))
            {
                index++;
                continue;
            }

            switch (operationKind)
            {
                case "set_content":
                    TryGetRequiredOperationString(operationElement, "new_content", index, errors, out _);
                    break;

                case "replace_span":
                    TryGetRequiredOperationInt(operationElement, "start_line", index, errors, out _);
                    TryGetRequiredOperationInt(operationElement, "start_column", index, errors, out _);
                    TryGetRequiredOperationInt(operationElement, "end_line", index, errors, out _);
                    TryGetRequiredOperationInt(operationElement, "end_column", index, errors, out _);
                    TryGetRequiredOperationString(operationElement, "new_text", index, errors, out _);
                    break;

                default:
                    errors.Add(new CommandError(
                        "invalid_input",
                        $"Operation at index {index} has unsupported operation '{operationKind}'. Supported values: set_content, replace_span."));
                    break;
            }

            index++;
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> validationErrors = Validate(input).ToList();
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 200, minValue: 1, maxValue: 5_000);

        if (!TryParseOperations(input.GetProperty("operations"), out List<TransactionOperation> operations, out CommandError? parseError))
        {
            return new CommandExecutionResult(null, new[] { parseError! });
        }

        Dictionary<string, TransactionFileState> fileStates = new(StringComparer.OrdinalIgnoreCase);
        foreach (TransactionOperation operation in operations)
        {
            if (!fileStates.ContainsKey(operation.file_path))
            {
                string source = await File.ReadAllTextAsync(operation.file_path, cancellationToken).ConfigureAwait(false);
                fileStates.Add(operation.file_path, new TransactionFileState(operation.file_path, source));
            }
        }

        List<object> operationResults = new(capacity: operations.Count);
        foreach (TransactionOperation operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TransactionFileState state = fileStates[operation.file_path];
            SourceText before = state.Current;
            SourceText after;

            try
            {
                after = operation.operation switch
                {
                    "set_content" => SourceText.From(operation.new_content ?? string.Empty, before.Encoding ?? Encoding.UTF8),
                    "replace_span" => before.WithChanges(BuildTextChange(before, operation)),
                    _ => before,
                };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                return new CommandExecutionResult(
                    null,
                    new[]
                    {
                        new CommandError(
                            "invalid_input",
                            $"Operation at index {operation.index} failed validation: {ex.Message}"),
                    });
            }

            bool changed = !string.Equals(before.ToString(), after.ToString(), StringComparison.Ordinal);
            int[] changedLines = changed
                ? ComputeChangedLines(before, after)
                : Array.Empty<int>();

            state.Current = after;
            operationResults.Add(new
            {
                index = operation.index,
                operation = operation.operation,
                file_path = operation.file_path,
                changed,
                changed_line_count = changedLines.Length,
                changed_lines = changedLines,
            });
        }

        List<SyntaxTree> syntaxTrees = new(capacity: fileStates.Count);
        List<object> fileSummaries = new(capacity: fileStates.Count);
        int changedFileCount = 0;
        int totalChangedLines = 0;
        int wroteFileCount = 0;

        foreach (TransactionFileState state in fileStates.Values.OrderBy(v => v.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool changed = !string.Equals(state.Original.ToString(), state.Current.ToString(), StringComparison.Ordinal);
            int[] changedLines = changed
                ? ComputeChangedLines(state.Original, state.Current)
                : Array.Empty<int>();
            totalChangedLines += changedLines.Length;
            if (changed)
            {
                changedFileCount++;
            }

            if (apply && changed)
            {
                await File.WriteAllTextAsync(state.FilePath, state.Current.ToString(), cancellationToken).ConfigureAwait(false);
                wroteFileCount++;
            }

            syntaxTrees.Add(CSharpSyntaxTree.ParseText(state.Current, path: state.FilePath, cancellationToken: cancellationToken));
            fileSummaries.Add(new
            {
                file_path = state.FilePath,
                changed,
                changed_line_count = changedLines.Length,
                changed_lines = changedLines,
                original_character_count = state.Original.Length,
                current_character_count = state.Current.Length,
            });
        }

        IReadOnlyList<Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(syntaxTrees, cancellationToken);
        NormalizedDiagnostic[] normalizedDiagnostics = CompilationDiagnostics.Normalize(diagnostics)
            .Take(maxDiagnostics)
            .ToArray();

        int diagnosticsErrors = normalizedDiagnostics.Count(d =>
            string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = normalizedDiagnostics.Count(d =>
            string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        object data = new
        {
            apply_changes = apply,
            wrote_files = apply && wroteFileCount > 0,
            wrote_file_count = wroteFileCount,
            operation_count = operations.Count,
            file_count = fileStates.Count,
            changed_file_count = changedFileCount,
            changed_line_count = totalChangedLines,
            operations = operationResults,
            files = fileSummaries,
            diagnostics_after_edit = new
            {
                total = diagnostics.Count,
                returned = normalizedDiagnostics.Length,
                errors = diagnosticsErrors,
                warnings = diagnosticsWarnings,
                diagnostics = normalizedDiagnostics,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool TryParseOperations(
        JsonElement operationsProperty,
        out List<TransactionOperation> operations,
        out CommandError? error)
    {
        operations = new List<TransactionOperation>();
        int index = 0;
        foreach (JsonElement operationElement in operationsProperty.EnumerateArray())
        {
            if (!TryGetRequiredOperationString(operationElement, "file_path", index, errors: null, out string filePath))
            {
                error = new CommandError("invalid_input", $"Operation at index {index} requires string property 'file_path'.");
                return false;
            }

            string normalizedFilePath = Path.GetFullPath(filePath);
            if (!TryGetOperationKind(operationElement, index, errors: null, out string operationKind))
            {
                error = new CommandError(
                    "invalid_input",
                    $"Operation at index {index} requires string property 'operation' (or alias 'type').");
                return false;
            }

            switch (operationKind)
            {
                case "set_content":
                    if (!TryGetRequiredOperationString(operationElement, "new_content", index, errors: null, out string newContent))
                    {
                        error = new CommandError("invalid_input", $"Operation at index {index} requires string property 'new_content'.");
                        return false;
                    }

                    operations.Add(new TransactionOperation(
                        index: index,
                        operation: operationKind,
                        file_path: normalizedFilePath,
                        new_content: newContent,
                        start_line: null,
                        start_column: null,
                        end_line: null,
                        end_column: null,
                        new_text: null));
                    break;

                case "replace_span":
                    if (!TryGetRequiredOperationInt(operationElement, "start_line", index, errors: null, out int startLine) ||
                        !TryGetRequiredOperationInt(operationElement, "start_column", index, errors: null, out int startColumn) ||
                        !TryGetRequiredOperationInt(operationElement, "end_line", index, errors: null, out int endLine) ||
                        !TryGetRequiredOperationInt(operationElement, "end_column", index, errors: null, out int endColumn) ||
                        !TryGetRequiredOperationString(operationElement, "new_text", index, errors: null, out string newText))
                    {
                        error = new CommandError(
                            "invalid_input",
                            $"Operation at index {index} requires start/end line-column integers and string property 'new_text'.");
                        return false;
                    }

                    operations.Add(new TransactionOperation(
                        index: index,
                        operation: operationKind,
                        file_path: normalizedFilePath,
                        new_content: null,
                        start_line: startLine,
                        start_column: startColumn,
                        end_line: endLine,
                        end_column: endColumn,
                        new_text: newText));
                    break;

                default:
                    error = new CommandError(
                        "invalid_input",
                        $"Operation at index {index} has unsupported operation '{operationKind}'. Supported values: set_content, replace_span.");
                    return false;
            }

            index++;
        }

        error = null;
        return true;
    }

    private static TextChange BuildTextChange(SourceText sourceText, TransactionOperation operation)
    {
        if (operation.start_line is null ||
            operation.start_column is null ||
            operation.end_line is null ||
            operation.end_column is null)
        {
            throw new ArgumentException("replace_span operation requires start/end line-column coordinates.");
        }

        int start = GetPositionFromLineColumn(sourceText, operation.start_line.Value, operation.start_column.Value, allowLineEnd: true);
        int end = GetPositionFromLineColumn(sourceText, operation.end_line.Value, operation.end_column.Value, allowLineEnd: true);
        if (end < start)
        {
            throw new ArgumentException("replace_span operation has end position before start position.");
        }

        return new TextChange(new TextSpan(start, end - start), operation.new_text ?? string.Empty);
    }

    private static int GetPositionFromLineColumn(SourceText sourceText, int line, int column, bool allowLineEnd)
    {
        if (line < 1 || line > sourceText.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(line), $"Line '{line}' is outside the valid range 1..{sourceText.Lines.Count}.");
        }

        TextLine textLine = sourceText.Lines[line - 1];
        int requestedOffset = Math.Max(0, column - 1);
        int maxOffset = allowLineEnd
            ? textLine.Span.Length
            : Math.Max(0, textLine.Span.Length - 1);
        int clampedOffset = Math.Min(requestedOffset, maxOffset);
        return textLine.Start + clampedOffset;
    }

    private static int[] ComputeChangedLines(SourceText before, SourceText after)
    {
        IReadOnlyList<TextChangeRange> changes = after.GetChangeRanges(before);
        if (changes.Count == 0)
        {
            return Array.Empty<int>();
        }

        HashSet<int> lines = new();
        foreach (TextChangeRange range in changes)
        {
            TextSpan beforeSpan = range.Span;
            if (beforeSpan.Length == 0)
            {
                int insertionPosition = Math.Max(0, Math.Min(before.Length, beforeSpan.Start));
                int insertionLine = before.Lines.GetLineFromPosition(insertionPosition).LineNumber + 1;
                lines.Add(insertionLine);
                continue;
            }

            int startLine = before.Lines.GetLineFromPosition(beforeSpan.Start).LineNumber + 1;
            int endPosition = Math.Max(beforeSpan.Start, beforeSpan.End - 1);
            int endLine = before.Lines.GetLineFromPosition(endPosition).LineNumber + 1;
            for (int line = startLine; line <= endLine; line++)
            {
                lines.Add(line);
            }
        }

        return lines
            .OrderBy(v => v)
            .ToArray();
    }

    private static bool TryGetOperationKind(
        JsonElement operationElement,
        int index,
        List<CommandError>? errors,
        out string operationKind)
    {
        operationKind = string.Empty;
        if (operationElement.TryGetProperty("operation", out JsonElement operationProperty) &&
            operationProperty.ValueKind == JsonValueKind.String)
        {
            operationKind = (operationProperty.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            return true;
        }

        if (operationElement.TryGetProperty("type", out JsonElement typeProperty) &&
            typeProperty.ValueKind == JsonValueKind.String)
        {
            operationKind = (typeProperty.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            return true;
        }

        errors?.Add(new CommandError(
            "invalid_input",
            $"Operation at index {index} requires string property 'operation' (or alias 'type')."));
        return false;
    }

    private static bool TryGetRequiredOperationString(
        JsonElement operationElement,
        string propertyName,
        int index,
        List<CommandError>? errors,
        out string value)
    {
        value = string.Empty;
        if (!operationElement.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            errors?.Add(new CommandError(
                "invalid_input",
                $"Operation at index {index} requires string property '{propertyName}'."));
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetRequiredOperationInt(
        JsonElement operationElement,
        string propertyName,
        int index,
        List<CommandError>? errors,
        out int value)
    {
        value = 0;
        if (!operationElement.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out int parsed) ||
            parsed < 1)
        {
            errors?.Add(new CommandError(
                "invalid_input",
                $"Operation at index {index} requires integer property '{propertyName}' >= 1."));
            return false;
        }

        value = parsed;
        return true;
    }

    private sealed record TransactionOperation(
        int index,
        string operation,
        string file_path,
        string? new_content,
        int? start_line,
        int? start_column,
        int? end_line,
        int? end_column,
        string? new_text);

    private sealed class TransactionFileState
    {
        public TransactionFileState(string filePath, string source)
        {
            FilePath = filePath;
            Original = SourceText.From(source, Encoding.UTF8);
            Current = Original;
        }

        public string FilePath { get; }

        public SourceText Original { get; }

        public SourceText Current { get; set; }
    }
}
