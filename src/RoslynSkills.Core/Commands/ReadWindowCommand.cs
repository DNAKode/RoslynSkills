using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Zero-disk line-window reader backed by the hot workspace's in-memory <see cref="WorkspaceOverlayIndex"/> /
/// <see cref="SourceText"/> line-offset table, with single-window and batched multi-window support.
/// </summary>
public sealed class ReadWindowCommand : IAgentCommand
{
    private const int MaxBatchWindows = 64;

    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.read_window",
        Summary: "Read bounded line windows from one or more files directly from the hot workspace's in-memory SourceText (zero disk reads when warm), with content SHA-256 and enclosing symbol context.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        RunsOnHotWorkspace: true,
        HotWorkspaceOptional: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "include_line_numbers", errors);
        InputParsing.ValidateOptionalBool(input, "include_content_hash", errors);
        InputParsing.ValidateOptionalBool(input, "include_enclosing_outline", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalInt(input, "max_chars", errors, minValue: 200, maxValue: 200_000);

        bool hasWindows = input.TryGetProperty("windows", out JsonElement windowsProp) &&
                          windowsProp.ValueKind != JsonValueKind.Null &&
                          windowsProp.ValueKind != JsonValueKind.Undefined;

        if (hasWindows)
        {
            if (windowsProp.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new CommandError("invalid_input", "Property 'windows' must be an array of window objects."));
                return errors;
            }

            int count = windowsProp.GetArrayLength();
            if (count == 0)
            {
                errors.Add(new CommandError("invalid_input", "Property 'windows' must contain at least one window."));
                return errors;
            }

            if (count > MaxBatchWindows)
            {
                errors.Add(new CommandError("invalid_input", $"Property 'windows' supports at most {MaxBatchWindows} items per call."));
                return errors;
            }

            string? defaultFile = GetOptionalTrimmedString(input, "file_path");
            int idx = 0;
            foreach (JsonElement item in windowsProp.EnumerateArray())
            {
                ValidateWindowSpec(item, idx, defaultFile, errors);
                idx++;
            }

            return errors;
        }

        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        ValidateLineBounds(input, prefix: string.Empty, errors);
        if (!File.Exists(filePath) && !ExistsInHotWorkspace(input, filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        IReadOnlyList<CommandError> validationErrors = Validate(input);
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: false);
        bool includeLineNumbers = InputParsing.GetOptionalBool(input, "include_line_numbers", defaultValue: true);
        bool includeContentHash = InputParsing.GetOptionalBool(input, "include_content_hash", defaultValue: true);
        bool includeEnclosingOutline = InputParsing.GetOptionalBool(input, "include_enclosing_outline", defaultValue: !brief);
        int defaultLineCount = InputParsing.GetOptionalInt(input, "line_count", defaultValue: 100, minValue: 1, maxValue: 1_000);
        int maxChars = InputParsing.GetOptionalInt(input, "max_chars", defaultValue: 32_000, minValue: 200, maxValue: 200_000);

        HostedWorkspace? hosted = TryGetHostedWorkspace(input);

        if (input.TryGetProperty("windows", out JsonElement windowsProp) && windowsProp.ValueKind == JsonValueKind.Array)
        {
            string? defaultFilePath = GetOptionalTrimmedString(input, "file_path");
            List<object> results = new();
            List<CommandError> batchErrors = new();
            int overlayHits = 0;
            int diskReads = 0;
            int idx = 0;

            foreach (JsonElement item in windowsProp.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? label = GetOptionalTrimmedString(item, "label");
                string? itemFilePath = GetOptionalTrimmedString(item, "file_path") ?? defaultFilePath;
                if (string.IsNullOrWhiteSpace(itemFilePath))
                {
                    batchErrors.Add(new CommandError("invalid_input", $"Window at index {idx} is missing 'file_path'."));
                    idx++;
                    continue;
                }

                (WindowReadResult? read, CommandError? error) = await ReadSingleWindowAsync(
                        hosted,
                        itemFilePath,
                        item,
                        defaultLineCount,
                        includeLineNumbers,
                        includeContentHash,
                        includeEnclosingOutline,
                        maxChars,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (error is not null || read is null)
                {
                    batchErrors.Add(error ?? new CommandError("read_failed", $"Failed to read window {idx}."));
                    results.Add(new
                    {
                        index = idx,
                        label,
                        ok = false,
                        file_path = Path.GetFullPath(itemFilePath),
                        error = error?.Message,
                    });
                }
                else
                {
                    if (string.Equals(read.TextSource, "workspace_overlay_index", StringComparison.Ordinal))
                    {
                        overlayHits++;
                    }
                    else
                    {
                        diskReads++;
                    }

                    results.Add(new
                    {
                        index = idx,
                        label,
                        ok = true,
                        file_path = read.FilePath,
                        text_source = read.TextSource,
                        file_sha256 = read.FileSha256,
                        total_lines = read.TotalLines,
                        start_line = read.StartLine,
                        end_line = read.EndLine,
                        returned_lines = read.ReturnedLines,
                        truncated = read.Truncated,
                        character_count = read.Content.Length,
                        enclosing_symbol = read.EnclosingSymbol,
                        content = read.Content,
                    });
                }

                idx++;
            }

            string batchSource = overlayHits > 0 && diskReads == 0
                ? "workspace_overlay_index"
                : overlayHits == 0
                    ? "disk"
                    : "mixed";

            object batchData = new
            {
                text_source = batchSource,
                window_count = results.Count,
                overlay_hits = overlayHits,
                disk_reads = diskReads,
                windows = results,
            };

            return new CommandExecutionResult(batchData, batchErrors);
        }

        string singleFilePath = input.GetProperty("file_path").GetString()!.Trim();
        (WindowReadResult? single, CommandError? singleError) = await ReadSingleWindowAsync(
                hosted,
                singleFilePath,
                input,
                defaultLineCount,
                includeLineNumbers,
                includeContentHash,
                includeEnclosingOutline,
                maxChars,
                cancellationToken)
            .ConfigureAwait(false);
        if (singleError is not null || single is null)
        {
            return new CommandExecutionResult(null, new[] { singleError ?? new CommandError("read_failed", "Failed to read file window.") });
        }

        object data = new
        {
            file_path = single.FilePath,
            text_source = single.TextSource,
            file_sha256 = single.FileSha256,
            total_lines = single.TotalLines,
            start_line = single.StartLine,
            end_line = single.EndLine,
            returned_lines = single.ReturnedLines,
            truncated = single.Truncated,
            character_count = single.Content.Length,
            enclosing_symbol = single.EnclosingSymbol,
            content = single.Content,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static async Task<(WindowReadResult? Result, CommandError? Error)> ReadSingleWindowAsync(
        HostedWorkspace? hosted,
        string rawFilePath,
        JsonElement spec,
        int defaultLineCount,
        bool includeLineNumbers,
        bool includeContentHash,
        bool includeEnclosingOutline,
        int maxChars,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(rawFilePath);
        SourceText sourceText;
        SyntaxTree? syntaxTree = null;
        string textSource;
        string? fileSha256 = null;

        if (hosted is not null &&
            hosted.OverlayIndex.TryGetDocument(hosted.Workspace, fullPath, out OverlayDocumentEntry? entry, cancellationToken) &&
            entry is not null)
        {
            sourceText = entry.SourceText;
            syntaxTree = entry.SyntaxTree;
            textSource = "workspace_overlay_index";
            if (includeContentHash)
            {
                fileSha256 = entry.GetTextSha256();
            }
        }
        else
        {
            if (!File.Exists(fullPath))
            {
                return (null, new CommandError("file_not_found", $"Input file '{fullPath}' does not exist."));
            }

            FileTextSnapshot snapshot = await EditFileText.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            sourceText = SourceText.From(snapshot.Text, snapshot.Encoding);
            textSource = "disk";
            if (includeContentHash)
            {
                fileSha256 = snapshot.ContentSha256;
            }

            if (includeEnclosingOutline && CommandLanguageServices.IsSupportedSourceFile(fullPath))
            {
                syntaxTree = CommandLanguageServices.ParseSyntaxTree(snapshot.Text, fullPath, cancellationToken);
            }
        }

        int totalLines = Math.Max(1, sourceText.Lines.Count);
        int startLine;
        int endLine;

        int? anchorLine = TryGetOptionalInt(spec, "anchor_line");
        if (anchorLine.HasValue)
        {
            int clampedAnchor = Math.Clamp(anchorLine.Value, 1, totalLines);
            int before = InputParsing.GetOptionalInt(spec, "context_before", defaultValue: 15, minValue: 0, maxValue: 500);
            int after = InputParsing.GetOptionalInt(spec, "context_after", defaultValue: 30, minValue: 0, maxValue: 500);
            startLine = Math.Max(1, clampedAnchor - before);
            endLine = Math.Min(totalLines, clampedAnchor + after);
        }
        else
        {
            startLine = InputParsing.GetOptionalInt(spec, "start_line", defaultValue: 1, minValue: 1, maxValue: 1_000_000);
            if (startLine > totalLines)
            {
                startLine = totalLines;
            }

            int? requestedEnd = TryGetOptionalInt(spec, "end_line");
            int lineCount = InputParsing.GetOptionalInt(spec, "line_count", defaultValue: defaultLineCount, minValue: 1, maxValue: 1_000);
            if (requestedEnd.HasValue)
            {
                endLine = Math.Clamp(requestedEnd.Value, startLine, totalLines);
            }
            else
            {
                endLine = Math.Min(totalLines, startLine + lineCount - 1);
            }
        }

        StringBuilder sb = new();
        bool truncated = false;
        int actualEndLine = startLine - 1;

        for (int lineNum = startLine; lineNum <= endLine; lineNum++)
        {
            string lineText = sourceText.Lines[lineNum - 1].ToString();
            string formatted = includeLineNumbers ? $"{lineNum,5}: {lineText}" : lineText;
            int needed = formatted.Length + (sb.Length > 0 ? 1 : 0);
            if (sb.Length + needed > maxChars)
            {
                if (sb.Length == 0)
                {
                    sb.Append(formatted[..Math.Min(formatted.Length, maxChars)]);
                    actualEndLine = lineNum;
                }

                truncated = true;
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(formatted);
            actualEndLine = lineNum;
        }

        string? enclosingSymbol = null;
        if (includeEnclosingOutline && syntaxTree is not null)
        {
            int midLine = Math.Clamp(anchorLine ?? ((startLine + Math.Max(startLine, actualEndLine)) / 2), 1, totalLines);
            int midPos = sourceText.Lines[midLine - 1].Start;
            SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SyntaxToken token = root.FindToken(Math.Clamp(midPos, 0, Math.Max(0, root.FullSpan.End - 1)));
            enclosingSymbol = WorkspaceOverlayIndex.FindEnclosingSyntaxSymbolName(token.Parent);
        }

        return (new WindowReadResult(
            FilePath: fullPath,
            TextSource: textSource,
            FileSha256: fileSha256,
            TotalLines: totalLines,
            StartLine: startLine,
            EndLine: Math.Max(startLine, actualEndLine),
            ReturnedLines: Math.Max(0, actualEndLine - startLine + 1),
            Truncated: truncated,
            EnclosingSymbol: enclosingSymbol,
            Content: sb.ToString()), null);
    }

    private static void ValidateWindowSpec(JsonElement item, int index, string? defaultFile, List<CommandError> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new CommandError("invalid_input", $"Window at index {index} must be an object."));
            return;
        }

        string? filePath = GetOptionalTrimmedString(item, "file_path") ?? defaultFile;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add(new CommandError("invalid_input", $"Window at index {index} requires 'file_path' (or top-level 'file_path')."));
            return;
        }

        ValidateLineBounds(item, $"Window[{index}]: ", errors);
    }

    private static void ValidateLineBounds(JsonElement element, string prefix, List<CommandError> errors)
    {
        InputParsing.ValidateOptionalInt(element, "start_line", errors, minValue: 1, maxValue: 1_000_000);
        InputParsing.ValidateOptionalInt(element, "end_line", errors, minValue: 1, maxValue: 1_000_000);
        InputParsing.ValidateOptionalInt(element, "line_count", errors, minValue: 1, maxValue: 1_000);
        InputParsing.ValidateOptionalInt(element, "anchor_line", errors, minValue: 1, maxValue: 1_000_000);
        InputParsing.ValidateOptionalInt(element, "context_before", errors, minValue: 0, maxValue: 500);
        InputParsing.ValidateOptionalInt(element, "context_after", errors, minValue: 0, maxValue: 500);

        int? start = TryGetOptionalInt(element, "start_line");
        int? end = TryGetOptionalInt(element, "end_line");
        if (start.HasValue && end.HasValue && end.Value < start.Value)
        {
            errors.Add(new CommandError("invalid_input", $"{prefix}'end_line' ({end.Value}) must be >= 'start_line' ({start.Value})."));
        }
    }

    private static int? TryGetOptionalInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement prop) &&
           prop.ValueKind == JsonValueKind.Number &&
           prop.TryGetInt32(out int value)
            ? value
            : null;

    private static string? GetOptionalTrimmedString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string trimmed = (prop.GetString() ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static HostedWorkspace? TryGetHostedWorkspace(JsonElement input)
    {
        string? handle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        if (!string.IsNullOrWhiteSpace(handle) &&
            WorkspaceHostStoreProvider.Current.TryGet(handle, out HostedWorkspace? hosted) &&
            hosted is not null)
        {
            return hosted;
        }

        return null;
    }

    private static bool ExistsInHotWorkspace(JsonElement input, string filePath)
    {
        HostedWorkspace? hosted = TryGetHostedWorkspace(input);
        return hosted is not null && hosted.Workspace.TryGetTreeByPath(filePath, out _);
    }

    private sealed record WindowReadResult(
        string FilePath,
        string TextSource,
        string? FileSha256,
        int TotalLines,
        int StartLine,
        int EndLine,
        int ReturnedLines,
        bool Truncated,
        string? EnclosingSymbol,
        string Content);
}
