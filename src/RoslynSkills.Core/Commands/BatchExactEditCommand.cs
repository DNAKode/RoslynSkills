using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class BatchExactEditCommand : IAgentCommand
{
    private const int MaxOperations = 100;

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.batch_exact",
        Summary: "Apply multiple exact text/span edits with per-operation reporting and atomic apply by default.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!input.TryGetProperty("operations", out JsonElement operations) ||
            operations.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'operations' is required and must be an array."));
            return errors;
        }

        int operationCount = operations.GetArrayLength();
        if (operationCount == 0)
        {
            errors.Add(new CommandError("invalid_input", "Property 'operations' must contain at least one operation."));
        }

        if (operationCount > MaxOperations)
        {
            errors.Add(new CommandError("invalid_input", $"Property 'operations' must contain at most {MaxOperations} operations."));
        }

        string? defaultFilePath = TryGetOptionalString(input, "file_path");
        if (!string.IsNullOrWhiteSpace(defaultFilePath) &&
            !File.Exists(Path.GetFullPath(defaultFilePath)))
        {
            errors.Add(new CommandError("file_not_found", $"File '{Path.GetFullPath(defaultFilePath)}' does not exist."));
        }

        int index = 0;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            ValidateOperation(operation, index, defaultFilePath, errors);
            index++;
        }

        InputParsing.ValidateOptionalBool(input, "apply", errors);
        InputParsing.ValidateOptionalBool(input, "atomic", errors);
        InputParsing.ValidateOptionalBool(input, "continue_on_error", errors);
        InputParsing.ValidateOptionalBool(input, "include_diagnostics", errors);
        InputParsing.ValidateOptionalInt(input, "max_diagnostics", errors, minValue: 1, maxValue: 2_000);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
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
        bool atomic = InputParsing.GetOptionalBool(input, "atomic", defaultValue: true);
        bool continueOnError = InputParsing.GetOptionalBool(input, "continue_on_error", defaultValue: false);
        bool includeDiagnostics = InputParsing.GetOptionalBool(input, "include_diagnostics", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 2_000);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        string? defaultFilePath = TryGetOptionalString(input, "file_path");

        Dictionary<string, FileState> files = new(StringComparer.OrdinalIgnoreCase);
        List<object> operationResults = new();
        List<CommandError> operationErrors = new();
        int succeeded = 0;
        int failed = 0;
        int index = 0;

        foreach (JsonElement operation in input.GetProperty("operations").EnumerateArray())
        {
            OperationResult result = await ApplyOperationAsync(
                    operation,
                    index,
                    defaultFilePath,
                    files,
                    cancellationToken)
                .ConfigureAwait(false);
            operationResults.Add(result.Data);

            if (result.Error is null)
            {
                succeeded++;
            }
            else
            {
                failed++;
                operationErrors.Add(result.Error);
                if (!continueOnError)
                {
                    break;
                }
            }

            index++;
        }

        bool canWrite = apply && (!atomic || operationErrors.Count == 0);
        List<object> fileResults = new();
        int wroteFileCount = 0;
        foreach (FileState file in files.Values)
        {
            bool changed = !string.Equals(file.OriginalContent, file.UpdatedContent, StringComparison.Ordinal);
            bool wroteFile = false;
            if (canWrite && changed)
            {
                await File.WriteAllTextAsync(file.FilePath, file.UpdatedContent, cancellationToken).ConfigureAwait(false);
                wroteFile = true;
                wroteFileCount++;
            }

            object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(
                    file.FilePath,
                    wroteFile,
                    cancellationToken)
                .ConfigureAwait(false);

            object diagnosticsData = await ExactEditDiagnostics.BuildAsync(
                    file.FilePath,
                    file.UpdatedContent,
                    includeDiagnostics,
                    maxDiagnostics,
                    workspacePath,
                    workspaceHandle,
                    cancellationToken)
                .ConfigureAwait(false);

            fileResults.Add(new
            {
                file_path = file.FilePath,
                changed,
                wrote_file = wroteFile,
                character_delta = file.UpdatedContent.Length - file.OriginalContent.Length,
                hot_workspace_refresh = hotWorkspaceRefresh,
                diagnostics_after_batch = diagnosticsData,
            });
        }

        object data = new
        {
            file_path = defaultFilePath is null ? null : Path.GetFullPath(defaultFilePath),
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            apply_changes = apply,
            atomic,
            continue_on_error = continueOnError,
            total_operations = input.GetProperty("operations").GetArrayLength(),
            executed_operations = operationResults.Count,
            succeeded_operations = succeeded,
            failed_operations = failed,
            wrote_file_count = wroteFileCount,
            skipped_apply_due_to_errors = apply && atomic && operationErrors.Count > 0,
            operations = operationResults.ToArray(),
            file_results = fileResults.ToArray(),
        };

        return new CommandExecutionResult(data, operationErrors);
    }

    private static void ValidateOperation(
        JsonElement operation,
        int index,
        string? defaultFilePath,
        List<CommandError> errors)
    {
        if (operation.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} must be an object."));
            return;
        }

        string? filePath = TryGetOptionalString(operation, "file_path") ?? defaultFilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} must specify file_path or use top-level file_path."));
        }
        else if (!File.Exists(Path.GetFullPath(filePath)))
        {
            errors.Add(new CommandError("file_not_found", $"Operation {index} file '{Path.GetFullPath(filePath)}' does not exist."));
        }

        string kind = GetOperationKind(operation);
        if (kind is not ("replace_text" or "insert_text" or "replace_span"))
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} kind must be 'replace_text', 'insert_text', or 'replace_span'."));
            return;
        }

        if (kind == "replace_text")
        {
            ValidateRequiredText(operation, "old_text", index, errors, allowEmpty: false);
            ValidateRequiredText(operation, "new_text", index, errors, allowEmpty: true);
            InputParsing.ValidateOptionalBool(operation, "replace_all", errors);
            return;
        }

        if (kind == "replace_span")
        {
            ValidateRequiredInt(operation, "span_start", index, errors, minValue: 0);
            if (!operation.TryGetProperty("span_length", out JsonElement spanLength) ||
                spanLength.ValueKind != JsonValueKind.Number ||
                !spanLength.TryGetInt32(out int parsedLength) ||
                parsedLength < 0)
            {
                if (!operation.TryGetProperty("span_end", out JsonElement spanEnd) ||
                    spanEnd.ValueKind != JsonValueKind.Number ||
                    !spanEnd.TryGetInt32(out int parsedEnd) ||
                    parsedEnd < 0)
                {
                    errors.Add(new CommandError("invalid_operation", $"Operation {index} requires integer property 'span_length' >= 0 or 'span_end' >= 0."));
                }
                else if (operation.TryGetProperty("span_start", out JsonElement spanStartForEnd) &&
                    spanStartForEnd.ValueKind == JsonValueKind.Number &&
                    spanStartForEnd.TryGetInt32(out int parsedStart) &&
                    parsedEnd < parsedStart)
                {
                    errors.Add(new CommandError("invalid_operation", $"Operation {index} property 'span_end' must be >= span_start."));
                }
            }

            ValidateRequiredText(operation, "new_text", index, errors, allowEmpty: true);
            ValidateOptionalText(operation, "expected_text", index, errors);
            return;
        }

        ValidateRequiredText(operation, "anchor_text", index, errors, allowEmpty: false);
        ValidateRequiredText(operation, "insert_text", index, errors, allowEmpty: true);
        string position = GetPosition(operation);
        if (!position.Equals("before", StringComparison.OrdinalIgnoreCase) &&
            !position.Equals("after", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} property 'position' must be 'before' or 'after'."));
        }
    }

    private static async Task<OperationResult> ApplyOperationAsync(
        JsonElement operation,
        int index,
        string? defaultFilePath,
        Dictionary<string, FileState> files,
        CancellationToken cancellationToken)
    {
        string filePath = Path.GetFullPath(TryGetOptionalString(operation, "file_path") ?? defaultFilePath!);
        if (!files.TryGetValue(filePath, out FileState? file))
        {
            string content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            file = new FileState(filePath, content);
            files[filePath] = file;
        }

        string kind = GetOperationKind(operation);
        return kind switch
        {
            "insert_text" => ApplyInsertOperation(operation, index, file),
            "replace_span" => ApplyReplaceSpanOperation(operation, index, file),
            _ => ApplyReplaceOperation(operation, index, file),
        };
    }

    private static OperationResult ApplyReplaceOperation(JsonElement operation, int index, FileState file)
    {
        string oldText = operation.GetProperty("old_text").GetString()!;
        string newText = operation.GetProperty("new_text").GetString() ?? string.Empty;
        bool replaceAll = InputParsing.GetOptionalBool(operation, "replace_all", defaultValue: false);
        int matchCount = CountOccurrences(file.UpdatedContent, oldText);
        if (matchCount == 0)
        {
            CommandError error = new("old_text_not_found", $"Operation {index} old_text was not found.");
            return OperationResult.Failed(BuildOperationData(index, "replace_text", file.FilePath, matchCount, false, error), error);
        }

        if (!replaceAll && matchCount > 1)
        {
            CommandError error = new("old_text_ambiguous", $"Operation {index} old_text matched {matchCount} times. Make old_text more specific or set replace_all=true.");
            return OperationResult.Failed(BuildOperationData(index, "replace_text", file.FilePath, matchCount, false, error), error);
        }

        string before = file.UpdatedContent;
        file.UpdatedContent = replaceAll
            ? file.UpdatedContent.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(file.UpdatedContent, oldText, newText);
        object data = BuildOperationData(index, "replace_text", file.FilePath, matchCount, !string.Equals(before, file.UpdatedContent, StringComparison.Ordinal), null);
        return OperationResult.Succeeded(data);
    }

    private static OperationResult ApplyInsertOperation(JsonElement operation, int index, FileState file)
    {
        string anchorText = operation.GetProperty("anchor_text").GetString()!;
        string insertText = operation.GetProperty("insert_text").GetString() ?? string.Empty;
        string position = GetPosition(operation);
        int matchCount = CountOccurrences(file.UpdatedContent, anchorText);
        if (matchCount == 0)
        {
            CommandError error = new("anchor_text_not_found", $"Operation {index} anchor_text was not found.");
            return OperationResult.Failed(BuildOperationData(index, "insert_text", file.FilePath, matchCount, false, error), error);
        }

        if (matchCount > 1)
        {
            CommandError error = new("anchor_text_ambiguous", $"Operation {index} anchor_text matched {matchCount} times. Make anchor_text more specific.");
            return OperationResult.Failed(BuildOperationData(index, "insert_text", file.FilePath, matchCount, false, error), error);
        }

        int anchorIndex = file.UpdatedContent.IndexOf(anchorText, StringComparison.Ordinal);
        int insertIndex = position.Equals("before", StringComparison.OrdinalIgnoreCase)
            ? anchorIndex
            : anchorIndex + anchorText.Length;
        file.UpdatedContent = string.Concat(file.UpdatedContent.AsSpan(0, insertIndex), insertText, file.UpdatedContent.AsSpan(insertIndex));
        object data = BuildOperationData(index, "insert_text", file.FilePath, matchCount, true, null, position.ToLowerInvariant());
        return OperationResult.Succeeded(data);
    }

    private static OperationResult ApplyReplaceSpanOperation(JsonElement operation, int index, FileState file)
    {
        int spanStart = operation.GetProperty("span_start").GetInt32();
        int spanLength = GetSpanLength(operation, spanStart);
        string newText = operation.GetProperty("new_text").GetString() ?? string.Empty;
        if (spanLength < 0 ||
            spanStart > file.UpdatedContent.Length ||
            spanLength > file.UpdatedContent.Length - spanStart)
        {
            CommandError error = new("span_out_of_range", $"Operation {index} span [{spanStart}, {spanStart + spanLength}) is outside the current file content.");
            return OperationResult.Failed(BuildSpanOperationData(index, file.FilePath, spanStart, spanLength, false, error), error);
        }

        string currentText = file.UpdatedContent.Substring(spanStart, spanLength);
        string? expectedText = TryGetOptionalString(operation, "expected_text");
        if (expectedText is not null &&
            !string.Equals(currentText, expectedText, StringComparison.Ordinal))
        {
            CommandError error = new("expected_text_mismatch", $"Operation {index} expected_text did not match the span content.");
            return OperationResult.Failed(BuildSpanOperationData(index, file.FilePath, spanStart, spanLength, false, error), error);
        }

        string before = file.UpdatedContent;
        file.UpdatedContent = string.Concat(
            file.UpdatedContent.AsSpan(0, spanStart),
            newText,
            file.UpdatedContent.AsSpan(spanStart + spanLength));
        object data = BuildSpanOperationData(
            index,
            file.FilePath,
            spanStart,
            spanLength,
            !string.Equals(before, file.UpdatedContent, StringComparison.Ordinal),
            null);
        return OperationResult.Succeeded(data);
    }

    private static object BuildOperationData(
        int index,
        string kind,
        string filePath,
        int matchCount,
        bool changed,
        CommandError? error,
        string? position = null)
        => new
        {
            index,
            kind,
            file_path = filePath,
            position,
            ok = error is null,
            match_count = matchCount,
            changed,
            error,
            recovery_hint = BuildExactTextRecoveryHint(error, kind, matchCount),
        };

    private static object? BuildExactTextRecoveryHint(CommandError? error, string kind, int matchCount)
    {
        if (error is null)
        {
            return null;
        }

        if (string.Equals(error.Code, "old_text_ambiguous", StringComparison.Ordinal) ||
            string.Equals(error.Code, "anchor_text_ambiguous", StringComparison.Ordinal))
        {
            return new
            {
                problem = "text matched multiple locations",
                match_count = matchCount,
                preferred_next_step = "Use ctx.member_source on a precise line/column anchor and prefer edit.batch_exact kind=replace_span with expected_text when replacing a known member/span.",
                alternatives = new[]
                {
                    "Make old_text/anchor_text include more unique surrounding context.",
                    "Use replace_all=true only when every match should change.",
                },
            };
        }

        if (string.Equals(error.Code, "old_text_not_found", StringComparison.Ordinal) ||
            string.Equals(error.Code, "anchor_text_not_found", StringComparison.Ordinal))
        {
            return new
            {
                problem = "text did not match current file content",
                match_count = matchCount,
                preferred_next_step = "Re-read the target with ctx.member_source or ctx.search_text before retrying; the file may have drifted.",
                alternatives = new[]
                {
                    "Use replace_span with expected_text from an untruncated ctx.member_source edit_target.",
                    "Refresh the hot workspace if a prior edit succeeded in this file.",
                },
            };
        }

        return null;
    }

    private static object BuildSpanOperationData(
        int index,
        string filePath,
        int spanStart,
        int spanLength,
        bool changed,
        CommandError? error)
        => new
        {
            index,
            kind = "replace_span",
            file_path = filePath,
            span_start = spanStart,
            span_length = spanLength,
            span_end = spanStart + spanLength,
            ok = error is null,
            match_count = (int?)null,
            changed,
            error,
        };

    private static int GetSpanLength(JsonElement operation, int spanStart)
    {
        if (operation.TryGetProperty("span_length", out JsonElement spanLength) &&
            spanLength.ValueKind == JsonValueKind.Number &&
            spanLength.TryGetInt32(out int parsedLength))
        {
            return parsedLength;
        }

        int spanEnd = operation.GetProperty("span_end").GetInt32();
        return spanEnd - spanStart;
    }

    private static void ValidateRequiredText(
        JsonElement input,
        string propertyName,
        int index,
        List<CommandError> errors,
        bool allowEmpty)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} property '{propertyName}' is required and must be a string."));
            return;
        }

        if (!allowEmpty && string.IsNullOrEmpty(property.GetString()))
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} property '{propertyName}' must not be empty."));
        }
    }

    private static void ValidateOptionalText(
        JsonElement input,
        string propertyName,
        int index,
        List<CommandError> errors)
    {
        if (input.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} property '{propertyName}' must be a string when provided."));
        }
    }

    private static void ValidateRequiredInt(
        JsonElement input,
        string propertyName,
        int index,
        List<CommandError> errors,
        int minValue)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out int value) ||
            value < minValue)
        {
            errors.Add(new CommandError("invalid_operation", $"Operation {index} property '{propertyName}' is required and must be an integer >= {minValue}."));
        }
    }

    private static string? TryGetOptionalString(JsonElement input, string propertyName)
        => input.TryGetProperty(propertyName, out JsonElement property) &&
           property.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static string GetOperationKind(JsonElement operation)
        => TryGetOptionalString(operation, "kind") ??
           TryGetOptionalString(operation, "operation") ??
           TryGetOptionalString(operation, "type") ??
           "replace_text";

    private static string GetPosition(JsonElement input)
        => TryGetOptionalString(input, "position") ?? "after";

    private static int CountOccurrences(string text, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        int index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
    }

    private sealed class FileState
    {
        public FileState(string filePath, string originalContent)
        {
            FilePath = filePath;
            OriginalContent = originalContent;
            UpdatedContent = originalContent;
        }

        public string FilePath { get; }

        public string OriginalContent { get; }

        public string UpdatedContent { get; set; }
    }

    private sealed record OperationResult(object Data, CommandError? Error)
    {
        public static OperationResult Succeeded(object data) => new(data, null);

        public static OperationResult Failed(object data, CommandError error) => new(data, error);
    }
}
