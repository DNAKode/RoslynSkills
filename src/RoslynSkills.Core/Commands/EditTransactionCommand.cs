using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class EditTransactionCommand : IAgentCommand
{
    private const int MaxOperations = 500;

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.transaction",
        Summary: "Apply a multi-file atomic edit transaction (replace_text, insert_text, replace_span, set_content) on the hot workspace with solution diagnostics and optional dry-run.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!input.TryGetProperty("operations", out JsonElement operationsProperty) ||
            operationsProperty.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'operations' is required and must be an array."));
            return errors;
        }

        int operationCount = operationsProperty.GetArrayLength();
        if (operationCount == 0)
        {
            errors.Add(new CommandError("invalid_input", "Property 'operations' must contain at least one operation."));
            return errors;
        }

        if (operationCount > MaxOperations)
        {
            errors.Add(new CommandError("invalid_input", $"Property 'operations' must contain at most {MaxOperations} operations."));
            return errors;
        }

        string? defaultFilePath = TryGetOptionalString(input, "file_path");
        if (!string.IsNullOrWhiteSpace(defaultFilePath) &&
            !File.Exists(Path.GetFullPath(defaultFilePath)))
        {
            errors.Add(new CommandError("file_not_found", $"File '{Path.GetFullPath(defaultFilePath)}' does not exist."));
        }

        int index = 0;
        foreach (JsonElement operationElement in operationsProperty.EnumerateArray())
        {
            ValidateOperation(operationElement, index, defaultFilePath, errors);
            index++;
        }

        InputParsing.ValidateOptionalBool(input, "apply", errors);
        InputParsing.ValidateOptionalBool(input, "atomic", errors);
        InputParsing.ValidateOptionalBool(input, "continue_on_error", errors);
        InputParsing.ValidateOptionalBool(input, "include_diagnostics", errors);
        InputParsing.ValidateOptionalInt(input, "max_diagnostics", errors, minValue: 1, maxValue: 5_000);
        ValidateOptionalHashProperty(input, "expected_sha256", "Top-level", errors);
        ValidateOptionalHashProperty(input, "expected_content_hash", "Top-level", errors);
        ValidateOptionalExpectedFileHashes(input, errors);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
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
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 200, minValue: 1, maxValue: 5_000);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);
        string? defaultFilePath = TryGetOptionalString(input, "file_path");
        string? topLevelExpectedHash = TryGetExpectedHash(input);
        Dictionary<string, string> expectedFileHashes = BuildExpectedFileHashMap(input, defaultFilePath, topLevelExpectedHash);

        JsonElement operationsProperty = input.GetProperty("operations");
        int totalOperations = operationsProperty.GetArrayLength();
        Dictionary<string, TransactionFileState> fileStates = new(StringComparer.OrdinalIgnoreCase);
        List<object> operationResults = new(capacity: totalOperations);
        List<CommandError> operationErrors = new();
        int succeeded = 0;
        int failed = 0;
        int index = 0;

        // Verify top-level expected_file_hashes for any referenced files upfront (even before mutating).
        foreach ((string expectedPath, string expectedSha) in expectedFileHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fileStates.TryGetValue(expectedPath, out TransactionFileState? existingState))
            {
                if (!File.Exists(expectedPath))
                {
                    operationErrors.Add(new CommandError(
                        "file_not_found",
                        $"Precondition file '{expectedPath}' does not exist."));
                    break;
                }

                FileTextSnapshot snapshot = await EditFileText.ReadAsync(expectedPath, cancellationToken).ConfigureAwait(false);
                existingState = new TransactionFileState(snapshot);
                fileStates.Add(expectedPath, existingState);
            }

            if (!string.Equals(existingState.Snapshot.ContentSha256, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                operationErrors.Add(new CommandError(
                    "content_hash_mismatch",
                    $"Content hash precondition failed for '{expectedPath}': expected sha256 '{expectedSha}', actual '{existingState.Snapshot.ContentSha256}'.",
                    new
                    {
                        file_path = expectedPath,
                        expected_sha256 = expectedSha,
                        actual_sha256 = existingState.Snapshot.ContentSha256,
                    }));
                if (!continueOnError)
                {
                    break;
                }
            }
        }

        if (operationErrors.Count == 0 || continueOnError)
        {
            foreach (JsonElement operationElement in operationsProperty.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                OperationOutcome outcome = await ApplyOperationAsync(
                        operationElement,
                        index,
                        defaultFilePath,
                        fileStates,
                        cancellationToken)
                    .ConfigureAwait(false);
                operationResults.Add(outcome.Data);

                if (outcome.Error is null)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                    operationErrors.Add(outcome.Error);
                    if (!continueOnError)
                    {
                        break;
                    }
                }

                index++;
            }
        }

        bool canWrite = apply && (!atomic || operationErrors.Count == 0);
        bool skippedApplyDueToErrors = apply && atomic && operationErrors.Count > 0;

        List<TransactionFileState> orderedStates = fileStates.Values
            .OrderBy(v => v.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<object> fileSummaries = new(capacity: orderedStates.Count);
        List<string> writtenPaths = new();
        int changedFileCount = 0;
        int totalChangedLines = 0;

        List<AtomicFileWrite> pendingAtomicWrites = new();
        List<(TransactionFileState State, string OriginalText, string CurrentText, bool Changed, int[] ChangedLines)> preparedStates = new(orderedStates.Count);
        foreach (TransactionFileState state in orderedStates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string originalText = state.Original.ToString();
            string currentText = state.Current.ToString();
            bool changed = !string.Equals(originalText, currentText, StringComparison.Ordinal);
            int[] changedLines = changed
                ? ComputeChangedLines(state.Original, state.Current)
                : Array.Empty<int>();
            totalChangedLines += changedLines.Length;
            if (changed)
            {
                changedFileCount++;
            }

            preparedStates.Add((state, originalText, currentText, changed, changedLines));
            if (canWrite && changed)
            {
                pendingAtomicWrites.Add(new AtomicFileWrite(
                    FilePath: state.FilePath,
                    UpdatedText: currentText,
                    Encoding: state.Snapshot.Encoding,
                    OriginalBytes: state.Snapshot.OriginalBytes,
                    ExpectedOriginalSha256: state.Snapshot.ContentSha256));
            }
        }

        if (canWrite && pendingAtomicWrites.Count > 0)
        {
            AtomicBatchWriteResult commitResult = await EditFileText
                .WriteAtomicBatchAsync(pendingAtomicWrites, cancellationToken)
                .ConfigureAwait(false);
            if (!commitResult.Succeeded)
            {
                operationErrors.Add(new CommandError(
                    commitResult.ErrorCode ?? "transaction_write_failed",
                    commitResult.ErrorMessage ?? "Atomic multi-file transaction commit failed.",
                    new
                    {
                        file_path = commitResult.FailedFilePath,
                        expected_sha256 = commitResult.ExpectedSha256,
                        actual_sha256 = commitResult.ActualSha256,
                        rolled_back_paths = commitResult.RolledBackPaths,
                        rollback_errors = commitResult.RollbackErrors,
                    }));
                skippedApplyDueToErrors = true;
            }
            else
            {
                writtenPaths.AddRange(commitResult.WrittenPaths);
            }
        }

        HashSet<string> writtenPathSet = new(writtenPaths, StringComparer.OrdinalIgnoreCase);
        foreach ((TransactionFileState state, _, string currentText, bool changed, int[] changedLines) in preparedStates)
        {
            bool wroteFile = writtenPathSet.Contains(state.FilePath);
            string currentSha256 = changed
                ? EditFileText.ComputeEncodedSha256(currentText, state.Snapshot.Encoding)
                : state.Snapshot.ContentSha256;

            fileSummaries.Add(new
            {
                file_path = state.FilePath,
                changed,
                wrote_file = wroteFile,
                original_sha256 = state.Snapshot.ContentSha256,
                current_sha256 = currentSha256,
                changed_line_count = changedLines.Length,
                changed_lines = changedLines,
                original_character_count = state.Original.Length,
                current_character_count = state.Current.Length,
                character_delta = state.Current.Length - state.Original.Length,
            });
        }

        object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(
                writtenPaths,
                wroteFile: writtenPaths.Count > 0,
                allowAddDocuments: false,
                cancellationToken)
            .ConfigureAwait(false);

        WorkspaceContextInfo? workspaceContext = null;
        object? diagnosticsAfterEdit = null;
        if (includeDiagnostics && !skippedApplyDueToErrors && orderedStates.Count > 0)
        {
            (IReadOnlyList<Diagnostic>? solutionDiagnostics, workspaceContext) =
                await TryEvaluateSolutionDiagnosticsAsync(
                        orderedStates,
                        workspacePath,
                        workspaceHandle,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (requireWorkspace && solutionDiagnostics is null && operationErrors.Count == 0)
            {
                return new CommandExecutionResult(
                    null,
                    new[]
                    {
                        new CommandError(
                            "workspace_required",
                            $"Command '{Descriptor.Id}' required a workspace, but none of the transaction files resolved to a loaded workspace."),
                    });
            }

            IReadOnlyList<Diagnostic> diagnostics;
            if (solutionDiagnostics is not null)
            {
                diagnostics = solutionDiagnostics;
            }
            else
            {
                List<SyntaxTree> syntaxTrees = orderedStates
                    .Select(state => CSharpSyntaxTree.ParseText(state.Current, path: state.FilePath, cancellationToken: cancellationToken))
                    .ToList();
                diagnostics = CompilationDiagnostics.GetDiagnostics(syntaxTrees, cancellationToken);
            }

            NormalizedDiagnostic[] allNormalized = CompilationDiagnostics.Normalize(diagnostics);
            NormalizedDiagnostic[] normalizedDiagnostics = allNormalized
                .Take(maxDiagnostics)
                .ToArray();

            int diagnosticsErrors = allNormalized.Count(d =>
                string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
            int diagnosticsWarnings = allNormalized.Count(d =>
                string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

            diagnosticsAfterEdit = new
            {
                total = diagnostics.Count,
                returned = normalizedDiagnostics.Length,
                errors = diagnosticsErrors,
                warnings = diagnosticsWarnings,
                diagnostics = normalizedDiagnostics,
            };
        }

        object data = new
        {
            file_path = defaultFilePath is null ? null : Path.GetFullPath(defaultFilePath),
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            require_workspace = requireWorkspace,
            workspace_context = workspaceContext is null ? null : WorkspaceContextPayload.Build(workspaceContext),
            apply_changes = apply,
            atomic,
            continue_on_error = continueOnError,
            wrote_files = writtenPaths.Count > 0,
            wrote_file_count = writtenPaths.Count,
            operation_count = totalOperations,
            total_operations = totalOperations,
            executed_operations = operationResults.Count,
            succeeded_operations = succeeded,
            failed_operations = failed,
            skipped_apply_due_to_errors = skippedApplyDueToErrors,
            file_count = fileStates.Count,
            changed_file_count = changedFileCount,
            changed_line_count = totalChangedLines,
            hot_workspace_refresh = hotWorkspaceRefresh,
            operations = operationResults,
            files = fileSummaries,
            diagnostics_after_edit = diagnosticsAfterEdit,
        };

        return new CommandExecutionResult(data, operationErrors);
    }

    private static void ValidateOperation(
        JsonElement operationElement,
        int index,
        string? defaultFilePath,
        List<CommandError> errors)
    {
        if (operationElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new CommandError("invalid_input", $"Operation at index {index} must be an object."));
            return;
        }

        string? filePath = TryGetOptionalString(operationElement, "file_path") ?? defaultFilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Operation at index {index} requires string property 'file_path' (or top-level 'file_path')."));
            return;
        }

        string normalizedFilePath = Path.GetFullPath(filePath);
        if (!File.Exists(normalizedFilePath))
        {
            errors.Add(new CommandError(
                "file_not_found",
                $"Operation at index {index} references file '{normalizedFilePath}' which does not exist."));
            return;
        }

        if (!TryGetOperationKind(operationElement, index, errors, out string operationKind))
        {
            return;
        }

        ValidateOptionalHashProperty(operationElement, "expected_sha256", $"Operation[{index}]", errors);
        ValidateOptionalHashProperty(operationElement, "expected_content_hash", $"Operation[{index}]", errors);

        switch (operationKind)
        {
            case "set_content":
                TryGetRequiredOperationString(operationElement, "new_content", index, errors, allowEmpty: true, out _);
                break;

            case "replace_text":
                TryGetRequiredOperationString(operationElement, "old_text", index, errors, allowEmpty: false, out _);
                TryGetRequiredOperationString(operationElement, "new_text", index, errors, allowEmpty: true, out _);
                InputParsing.ValidateOptionalBool(operationElement, "replace_all", errors);
                break;

            case "insert_text":
                TryGetRequiredOperationString(operationElement, "anchor_text", index, errors, allowEmpty: false, out _);
                TryGetRequiredOperationString(operationElement, "insert_text", index, errors, allowEmpty: true, out _);
                string position = GetPosition(operationElement);
                if (!position.Equals("before", StringComparison.OrdinalIgnoreCase) &&
                    !position.Equals("after", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new CommandError(
                        "invalid_input",
                        $"Operation at index {index} property 'position' must be 'before' or 'after'."));
                }

                break;

            case "replace_span":
                ValidateReplaceSpanOperation(operationElement, index, errors);
                break;

            default:
                errors.Add(new CommandError(
                    "invalid_input",
                    $"Operation at index {index} has unsupported operation '{operationKind}'. Supported values: replace_text, insert_text, replace_span, set_content."));
                break;
        }
    }

    private static void ValidateReplaceSpanOperation(
        JsonElement operationElement,
        int index,
        List<CommandError> errors)
    {
        bool hasSpanStart = operationElement.TryGetProperty("span_start", out _);
        bool hasLineCoords = operationElement.TryGetProperty("start_line", out _);

        if (hasSpanStart)
        {
            TryGetRequiredOperationInt(operationElement, "span_start", index, minValue: 0, errors, out int parsedStart);
            bool hasSpanLength = operationElement.TryGetProperty("span_length", out JsonElement spanLengthProp) &&
                                 spanLengthProp.ValueKind == JsonValueKind.Number &&
                                 spanLengthProp.TryGetInt32(out int parsedLength) &&
                                 parsedLength >= 0;
            if (!hasSpanLength)
            {
                if (!operationElement.TryGetProperty("span_end", out JsonElement spanEndProp) ||
                    spanEndProp.ValueKind != JsonValueKind.Number ||
                    !spanEndProp.TryGetInt32(out int parsedEnd) ||
                    parsedEnd < 0)
                {
                    errors.Add(new CommandError(
                        "invalid_input",
                        $"Operation at index {index} requires integer property 'span_length' >= 0 or 'span_end' >= 0."));
                }
                else if (parsedEnd < parsedStart)
                {
                    errors.Add(new CommandError(
                        "invalid_input",
                        $"Operation at index {index} property 'span_end' must be >= span_start."));
                }
            }
        }
        else if (hasLineCoords)
        {
            TryGetRequiredOperationInt(operationElement, "start_line", index, minValue: 1, errors, out _);
            TryGetRequiredOperationInt(operationElement, "start_column", index, minValue: 1, errors, out _);
            TryGetRequiredOperationInt(operationElement, "end_line", index, minValue: 1, errors, out _);
            TryGetRequiredOperationInt(operationElement, "end_column", index, minValue: 1, errors, out _);
        }
        else
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Operation at index {index} replace_span requires either 1-based start_line/start_column/end_line/end_column or 0-based span_start with span_length/span_end."));
        }

        TryGetRequiredOperationString(operationElement, "new_text", index, errors, allowEmpty: true, out _);
        ValidateOptionalText(operationElement, "expected_text", index, errors);
    }

    private static async Task<OperationOutcome> ApplyOperationAsync(
        JsonElement operationElement,
        int index,
        string? defaultFilePath,
        Dictionary<string, TransactionFileState> fileStates,
        CancellationToken cancellationToken)
    {
        string filePath = Path.GetFullPath(TryGetOptionalString(operationElement, "file_path") ?? defaultFilePath!);
        if (!fileStates.TryGetValue(filePath, out TransactionFileState? state))
        {
            FileTextSnapshot snapshot = await EditFileText.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
            state = new TransactionFileState(snapshot);
            fileStates.Add(filePath, state);
        }

        TryGetOperationKind(operationElement, index, errors: null, out string operationKind);
        string? opExpectedHash = TryGetExpectedHash(operationElement);
        if (opExpectedHash is not null &&
            !string.Equals(state.Snapshot.ContentSha256, opExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            CommandError hashError = new(
                "content_hash_mismatch",
                $"Operation {index} content hash precondition failed for '{state.FilePath}': expected sha256 '{opExpectedHash}', actual '{state.Snapshot.ContentSha256}'.",
                new
                {
                    file_path = state.FilePath,
                    expected_sha256 = opExpectedHash,
                    actual_sha256 = state.Snapshot.ContentSha256,
                });
            return OperationOutcome.Failed(
                BuildOperationData(index, operationKind, state.FilePath, changed: false, changedLines: Array.Empty<int>(), hashError),
                hashError);
        }

        return operationKind switch
        {
            "set_content" => ApplySetContent(operationElement, index, state),
            "replace_text" => ApplyReplaceText(operationElement, index, state),
            "insert_text" => ApplyInsertText(operationElement, index, state),
            "replace_span" => ApplyReplaceSpan(operationElement, index, state),
            _ => OperationOutcome.Failed(
                BuildOperationData(index, operationKind, state.FilePath, changed: false, changedLines: Array.Empty<int>(), error: new CommandError("invalid_input", $"Unsupported operation '{operationKind}'.")),
                new CommandError("invalid_input", $"Unsupported operation '{operationKind}'.")),
        };
    }

    private static OperationOutcome ApplySetContent(
        JsonElement operationElement,
        int index,
        TransactionFileState state)
    {
        string rawNewContent = operationElement.GetProperty("new_content").GetString() ?? string.Empty;
        string normalizedContent = NormalizeTextForFile(state.Snapshot, rawNewContent);
        SourceText before = state.Current;
        SourceText after = SourceText.From(normalizedContent, state.Snapshot.Encoding);
        bool changed = !string.Equals(before.ToString(), after.ToString(), StringComparison.Ordinal);
        int[] changedLines = changed ? ComputeChangedLines(before, after) : Array.Empty<int>();
        state.Current = after;

        return OperationOutcome.Succeeded(BuildOperationData(
            index,
            "set_content",
            state.FilePath,
            changed,
            changedLines,
            error: null));
    }

    private static OperationOutcome ApplyReplaceText(
        JsonElement operationElement,
        int index,
        TransactionFileState state)
    {
        string oldText = operationElement.GetProperty("old_text").GetString()!;
        string rawNewText = operationElement.GetProperty("new_text").GetString() ?? string.Empty;
        string newText = NormalizeTextForFile(state.Snapshot, rawNewText);
        bool replaceAll = InputParsing.GetOptionalBool(operationElement, "replace_all", defaultValue: false);

        SourceText before = state.Current;
        string beforeText = before.ToString();
        string effectiveOldText = oldText;
        int matchCount = CountOccurrences(beforeText, effectiveOldText);
        if (matchCount == 0)
        {
            string normalizedOldText = NormalizeTextForFile(state.Snapshot, oldText);
            if (!string.Equals(normalizedOldText, oldText, StringComparison.Ordinal))
            {
                int normalizedCount = CountOccurrences(beforeText, normalizedOldText);
                if (normalizedCount > 0)
                {
                    effectiveOldText = normalizedOldText;
                    matchCount = normalizedCount;
                }
            }
        }

        if (matchCount == 0)
        {
            CommandError error = new("old_text_not_found", $"Operation {index} old_text was not found.");
            return OperationOutcome.Failed(
                BuildOperationData(index, "replace_text", state.FilePath, changed: false, changedLines: Array.Empty<int>(), error, matchCount: matchCount),
                error);
        }

        if (!replaceAll && matchCount > 1)
        {
            CommandError error = new("old_text_ambiguous", $"Operation {index} old_text matched {matchCount} times. Make old_text more specific or set replace_all=true.");
            return OperationOutcome.Failed(
                BuildOperationData(index, "replace_text", state.FilePath, changed: false, changedLines: Array.Empty<int>(), error, matchCount: matchCount),
                error);
        }

        string updatedText = replaceAll
            ? beforeText.Replace(effectiveOldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(beforeText, effectiveOldText, newText);
        SourceText after = SourceText.From(updatedText, state.Snapshot.Encoding);
        bool changed = !string.Equals(beforeText, updatedText, StringComparison.Ordinal);
        int[] changedLines = changed ? ComputeChangedLines(before, after) : Array.Empty<int>();
        state.Current = after;

        return OperationOutcome.Succeeded(BuildOperationData(
            index,
            "replace_text",
            state.FilePath,
            changed,
            changedLines,
            error: null,
            matchCount: matchCount));
    }

    private static OperationOutcome ApplyInsertText(
        JsonElement operationElement,
        int index,
        TransactionFileState state)
    {
        string anchorText = operationElement.GetProperty("anchor_text").GetString()!;
        string rawInsertText = operationElement.GetProperty("insert_text").GetString() ?? string.Empty;
        string insertText = NormalizeTextForFile(state.Snapshot, rawInsertText);
        string position = GetPosition(operationElement);

        SourceText before = state.Current;
        string beforeText = before.ToString();
        string effectiveAnchorText = anchorText;
        int matchCount = CountOccurrences(beforeText, effectiveAnchorText);
        if (matchCount == 0)
        {
            string normalizedAnchorText = NormalizeTextForFile(state.Snapshot, anchorText);
            if (!string.Equals(normalizedAnchorText, anchorText, StringComparison.Ordinal))
            {
                int normalizedCount = CountOccurrences(beforeText, normalizedAnchorText);
                if (normalizedCount > 0)
                {
                    effectiveAnchorText = normalizedAnchorText;
                    matchCount = normalizedCount;
                }
            }
        }

        if (matchCount == 0)
        {
            CommandError error = new("anchor_text_not_found", $"Operation {index} anchor_text was not found.");
            return OperationOutcome.Failed(
                BuildOperationData(index, "insert_text", state.FilePath, changed: false, changedLines: Array.Empty<int>(), error, matchCount: matchCount, position: position.ToLowerInvariant()),
                error);
        }

        if (matchCount > 1)
        {
            CommandError error = new("anchor_text_ambiguous", $"Operation {index} anchor_text matched {matchCount} times. Make anchor_text more specific.");
            return OperationOutcome.Failed(
                BuildOperationData(index, "insert_text", state.FilePath, changed: false, changedLines: Array.Empty<int>(), error, matchCount: matchCount, position: position.ToLowerInvariant()),
                error);
        }

        int anchorIndex = beforeText.IndexOf(effectiveAnchorText, StringComparison.Ordinal);
        int insertIndex = position.Equals("before", StringComparison.OrdinalIgnoreCase)
            ? anchorIndex
            : anchorIndex + effectiveAnchorText.Length;
        string updatedText = string.Concat(beforeText.AsSpan(0, insertIndex), insertText, beforeText.AsSpan(insertIndex));
        SourceText after = SourceText.From(updatedText, state.Snapshot.Encoding);
        bool changed = !string.Equals(beforeText, updatedText, StringComparison.Ordinal);
        int[] changedLines = changed ? ComputeChangedLines(before, after) : Array.Empty<int>();
        state.Current = after;

        return OperationOutcome.Succeeded(BuildOperationData(
            index,
            "insert_text",
            state.FilePath,
            changed,
            changedLines,
            error: null,
            matchCount: matchCount,
            position: position.ToLowerInvariant()));
    }

    private static OperationOutcome ApplyReplaceSpan(
        JsonElement operationElement,
        int index,
        TransactionFileState state)
    {
        SourceText before = state.Current;
        string beforeText = before.ToString();
        string rawNewText = operationElement.GetProperty("new_text").GetString() ?? string.Empty;
        string normalizedNewText = NormalizeTextForFile(state.Snapshot, rawNewText);

        int spanStart;
        int spanLength;

        if (operationElement.TryGetProperty("span_start", out JsonElement spanStartProp))
        {
            spanStart = spanStartProp.GetInt32();
            spanLength = GetSpanLength(operationElement, spanStart);
            if (spanLength < 0 ||
                spanStart > beforeText.Length ||
                spanLength > beforeText.Length - spanStart)
            {
                CommandError error = new("span_out_of_range", $"Operation {index} span [{spanStart}, {spanStart + spanLength}) is outside the current file content.");
                return OperationOutcome.Failed(
                    BuildSpanOperationData(index, state.FilePath, spanStart, spanLength, changed: false, changedLines: Array.Empty<int>(), error),
                    error);
            }
        }
        else
        {
            int startLine = operationElement.GetProperty("start_line").GetInt32();
            int startColumn = operationElement.GetProperty("start_column").GetInt32();
            int endLine = operationElement.GetProperty("end_line").GetInt32();
            int endColumn = operationElement.GetProperty("end_column").GetInt32();

            try
            {
                spanStart = GetPositionFromLineColumn(before, startLine, startColumn, allowLineEnd: true);
                int spanEnd = GetPositionFromLineColumn(before, endLine, endColumn, allowLineEnd: true);
                if (spanEnd < spanStart)
                {
                    throw new ArgumentException("replace_span operation has end position before start position.");
                }

                spanLength = spanEnd - spanStart;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                CommandError error = new("invalid_input", $"Operation at index {index} failed validation: {ex.Message}");
                return OperationOutcome.Failed(
                    BuildOperationData(index, "replace_span", state.FilePath, changed: false, changedLines: Array.Empty<int>(), error),
                    error);
            }
        }

        string currentSpanText = beforeText.Substring(spanStart, spanLength);
        string? expectedText = TryGetOptionalString(operationElement, "expected_text");
        if (expectedText is not null &&
            !string.Equals(currentSpanText, expectedText, StringComparison.Ordinal) &&
            !string.Equals(currentSpanText, NormalizeTextForFile(state.Snapshot, expectedText), StringComparison.Ordinal))
        {
            CommandError error = new("expected_text_mismatch", $"Operation {index} expected_text did not match the span content.");
            return OperationOutcome.Failed(
                BuildSpanOperationData(index, state.FilePath, spanStart, spanLength, changed: false, changedLines: Array.Empty<int>(), error),
                error);
        }

        SourceText after = before.WithChanges(new TextChange(new TextSpan(spanStart, spanLength), normalizedNewText));
        bool changed = !string.Equals(beforeText, after.ToString(), StringComparison.Ordinal);
        int[] changedLines = changed ? ComputeChangedLines(before, after) : Array.Empty<int>();
        state.Current = after;

        return OperationOutcome.Succeeded(BuildSpanOperationData(
            index,
            state.FilePath,
            spanStart,
            spanLength,
            changed,
            changedLines,
            error: null));
    }

    private static async Task<(IReadOnlyList<Diagnostic>? Diagnostics, WorkspaceContextInfo? WorkspaceContext)> TryEvaluateSolutionDiagnosticsAsync(
        IReadOnlyList<TransactionFileState> states,
        string? workspacePath,
        string? workspaceHandle,
        CancellationToken cancellationToken)
    {
        Solution? baseSolution = null;
        WorkspaceContextInfo? workspaceContext = null;

        IWorkspaceHostStore store = WorkspaceHostStoreProvider.Current;
        if (!string.IsNullOrWhiteSpace(workspaceHandle) &&
            store.TryGet(workspaceHandle.Trim(), out HostedWorkspace? explicitWorkspace) &&
            explicitWorkspace?.Workspace.Solution is { } explicitSolution)
        {
            baseSolution = explicitSolution;
            workspaceContext = BuildHostedWorkspaceContext(explicitWorkspace, explicitWorkspace.Handle);
        }

        if (baseSolution is null)
        {
            StringComparer pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            foreach (HostedWorkspace hosted in store.List())
            {
                if (hosted.Workspace.Solution is { } hostedSolution &&
                    states.Any(state => hosted.TrackedSourcePaths.Contains(state.FilePath, pathComparer)))
                {
                    baseSolution = hostedSolution;
                    workspaceContext = BuildHostedWorkspaceContext(hosted, hosted.Handle);
                    break;
                }
            }
        }

        if (baseSolution is null)
        {
            TransactionFileState? firstSourceFile = states.FirstOrDefault(s => CommandLanguageServices.IsSupportedSourceFile(s.FilePath));
            if (firstSourceFile is not null)
            {
                CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(
                        firstSourceFile.FilePath,
                        cancellationToken,
                        workspacePath,
                        workspaceHandle)
                    .ConfigureAwait(false);
                workspaceContext = analysis.WorkspaceContext;
                if (analysis.Document is not null)
                {
                    baseSolution = analysis.Document.Project.Solution;
                }
            }
        }

        if (baseSolution is null)
        {
            return (null, workspaceContext);
        }

        Solution updatedSolution = baseSolution;
        List<DocumentId> affectedDocumentIds = new();
        foreach (TransactionFileState state in states)
        {
            Document[] docs = SolutionEditSupport.FindDocumentsByPath(updatedSolution, state.FilePath).ToArray();
            if (docs.Length == 0)
            {
                continue;
            }

            foreach (Document doc in docs)
            {
                updatedSolution = updatedSolution.WithDocumentText(doc.Id, state.Current);
                affectedDocumentIds.Add(doc.Id);
            }
        }

        if (affectedDocumentIds.Count == 0)
        {
            return (null, workspaceContext);
        }

        IReadOnlyList<Diagnostic> diagnostics = await WorkspaceDiagnostics.GetDiagnosticsForUpdatedSolutionAsync(
                updatedSolution,
                affectedDocumentIds,
                cancellationToken)
            .ConfigureAwait(false);
        return (diagnostics, workspaceContext);
    }

    private static WorkspaceContextInfo BuildHostedWorkspaceContext(HostedWorkspace hosted, string handle)
        => new(
            mode: "workspace",
            resolution_source: "workspace_handle",
            requested_workspace_path: hosted.Workspace.WorkspacePath,
            resolved_workspace_path: hosted.Workspace.ResolvedWorkspacePath,
            project_path: null,
            fallback_reason: null,
            attempted_workspace_paths: new[] { hosted.Workspace.ResolvedWorkspacePath },
            workspace_diagnostics: hosted.Workspace.WorkspaceDiagnostics,
            workspace_cache_mode: "process_hot",
            workspace_cache_hit: true,
            workspace_kind: hosted.Workspace.WorkspaceKind,
            project_count: hosted.Workspace.ProjectCount,
            document_count: hosted.Workspace.DocumentCount,
            workspace_handle: handle,
            project_resolved: hosted.Workspace.UnresolvedProjects.Count == 0,
            project_resolution_issues: Array.Empty<string>(),
            unresolved_project_count: hosted.Workspace.UnresolvedProjects.Count);

    private static string NormalizeTextForFile(FileTextSnapshot snapshot, string text)
    {
        if (!snapshot.HasCrLf && !snapshot.HasBareLf)
        {
            return text;
        }

        return EditFileText.NormalizeLineEndings(text, snapshot.LineEnding);
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
        => SolutionEditSupport.ComputeChangedLines(before, after);

    private static object BuildOperationData(
        int index,
        string kind,
        string filePath,
        bool changed,
        int[] changedLines,
        CommandError? error,
        int? matchCount = null,
        string? position = null)
        => new
        {
            index,
            operation = kind,
            kind,
            file_path = filePath,
            position,
            ok = error is null,
            match_count = matchCount,
            changed,
            changed_line_count = changedLines.Length,
            changed_lines = changedLines,
            error,
            recovery_hint = BuildExactTextRecoveryHint(error, kind, matchCount ?? 0),
        };

    private static object BuildSpanOperationData(
        int index,
        string filePath,
        int spanStart,
        int spanLength,
        bool changed,
        int[] changedLines,
        CommandError? error)
        => new
        {
            index,
            operation = "replace_span",
            kind = "replace_span",
            file_path = filePath,
            span_start = spanStart,
            span_length = spanLength,
            span_end = spanStart + spanLength,
            ok = error is null,
            match_count = (int?)null,
            changed,
            changed_line_count = changedLines.Length,
            changed_lines = changedLines,
            error,
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
                preferred_next_step = "Use ctx.member_source on a precise line/column anchor and prefer edit.transaction operation=replace_span with expected_text when replacing a known member/span.",
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
            bool isInsertAnchor = string.Equals(error.Code, "anchor_text_not_found", StringComparison.Ordinal) &&
                                  string.Equals(kind, "insert_text", StringComparison.Ordinal);
            return new
            {
                problem = "text did not match current file content",
                match_count = matchCount,
                preferred_next_step = isInsertAnchor
                    ? "Retry with a short unique anchor line from ctx.member_source or ctx.search_text; avoid copied multiline block anchors for insertions."
                    : "Re-read the target with ctx.member_source or ctx.search_text before retrying; the file may have drifted.",
                alternatives = new[]
                {
                    isInsertAnchor
                        ? "For adding a sibling test/member, anchor on a final unique assertion or closing line from a small ctx.member_source focus window."
                        : "Use replace_span with expected_text from an untruncated ctx.member_source edit_target.",
                    "Refresh the hot workspace if a prior edit succeeded in this file.",
                },
            };
        }

        return null;
    }

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

    private static bool TryGetOperationKind(
        JsonElement operationElement,
        int index,
        List<CommandError>? errors,
        out string operationKind)
    {
        operationKind = string.Empty;
        foreach (string propertyName in new[] { "operation", "kind", "type" })
        {
            if (operationElement.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String)
            {
                string? raw = property.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    operationKind = raw.Trim().ToLowerInvariant();
                    return true;
                }
            }
        }

        if (operationElement.TryGetProperty("old_text", out _))
        {
            operationKind = "replace_text";
            return true;
        }

        errors?.Add(new CommandError(
            "invalid_input",
            $"Operation at index {index} requires string property 'operation' (or alias 'kind'/'type')."));
        return false;
    }

    private static bool TryGetRequiredOperationString(
        JsonElement operationElement,
        string propertyName,
        int index,
        List<CommandError>? errors,
        bool allowEmpty,
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
        if (!allowEmpty && value.Length == 0)
        {
            errors?.Add(new CommandError(
                "invalid_input",
                $"Operation at index {index} property '{propertyName}' must not be empty."));
            return false;
        }

        return true;
    }

    private static void ValidateOptionalText(
        JsonElement operationElement,
        string propertyName,
        int index,
        List<CommandError> errors)
    {
        if (operationElement.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Operation at index {index} property '{propertyName}' must be a string when provided."));
        }
    }

    private static bool TryGetRequiredOperationInt(
        JsonElement operationElement,
        string propertyName,
        int index,
        int minValue,
        List<CommandError>? errors,
        out int value)
    {
        value = 0;
        if (!operationElement.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out int parsed) ||
            parsed < minValue)
        {
            errors?.Add(new CommandError(
                "invalid_input",
                $"Operation at index {index} requires integer property '{propertyName}' >= {minValue}."));
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? TryGetExpectedHash(JsonElement element)
    {
        string? raw = TryGetOptionalString(element, "expected_sha256") ?? TryGetOptionalString(element, "expected_content_hash");
        return raw is null ? null : EditFileText.NormalizeSha256(raw);
    }

    private static void ValidateOptionalHashProperty(JsonElement element, string propertyName, string context, List<CommandError> errors)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement prop))
        {
            return;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError("invalid_input", $"{context} property '{propertyName}' must be a SHA-256 hex string."));
            return;
        }

        string normalized = EditFileText.NormalizeSha256(prop.GetString() ?? string.Empty);
        if (!EditFileText.IsValidSha256Hex(normalized))
        {
            errors.Add(new CommandError("invalid_input", $"{context} property '{propertyName}' must be a valid 64-character SHA-256 hex digest."));
        }
    }

    private static void ValidateOptionalExpectedFileHashes(JsonElement input, List<CommandError> errors)
    {
        if (!input.TryGetProperty("expected_file_hashes", out JsonElement mapProp))
        {
            return;
        }

        if (mapProp.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new CommandError("invalid_input", "Property 'expected_file_hashes' must be an object mapping file paths to SHA-256 hex digests."));
            return;
        }

        foreach (JsonProperty entry in mapProp.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", $"Property 'expected_file_hashes[\"{entry.Name}\"]' must be a SHA-256 hex string."));
                continue;
            }

            string normalized = EditFileText.NormalizeSha256(entry.Value.GetString() ?? string.Empty);
            if (!EditFileText.IsValidSha256Hex(normalized))
            {
                errors.Add(new CommandError("invalid_input", $"Property 'expected_file_hashes[\"{entry.Name}\"]' must be a valid 64-character SHA-256 hex digest."));
            }
        }
    }

    private static Dictionary<string, string> BuildExpectedFileHashMap(JsonElement input, string? defaultFilePath, string? topLevelExpectedHash)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(defaultFilePath) && topLevelExpectedHash is not null)
        {
            map[Path.GetFullPath(defaultFilePath)] = topLevelExpectedHash;
        }

        if (input.TryGetProperty("expected_file_hashes", out JsonElement mapProp) && mapProp.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in mapProp.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Value.ValueKind == JsonValueKind.String)
                {
                    map[Path.GetFullPath(entry.Name)] = EditFileText.NormalizeSha256(entry.Value.GetString() ?? string.Empty);
                }
            }
        }

        return map;
    }

    private static string? TryGetOptionalString(JsonElement input, string propertyName)
        => input.TryGetProperty(propertyName, out JsonElement property) &&
           property.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

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

    private sealed class TransactionFileState
    {
        public TransactionFileState(FileTextSnapshot snapshot)
        {
            Snapshot = snapshot;
            FilePath = snapshot.FilePath;
            Original = SourceText.From(snapshot.Text, snapshot.Encoding);
            Current = Original;
        }

        public FileTextSnapshot Snapshot { get; }

        public string FilePath { get; }

        public SourceText Original { get; }

        public SourceText Current { get; set; }
    }

    private sealed record OperationOutcome(object Data, CommandError? Error)
    {
        public static OperationOutcome Succeeded(object data) => new(data, null);

        public static OperationOutcome Failed(object data, CommandError error) => new(data, error);
    }
}
