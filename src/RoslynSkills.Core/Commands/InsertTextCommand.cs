using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class InsertTextCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.insert_text",
        Summary: "Insert text before or after an exact anchor snippet in one file with optional C# diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath);
        InputParsing.TryGetRequiredString(input, "anchor_text", errors, out string anchorText);
        InputParsing.TryGetRequiredString(input, "insert_text", errors, out _);

        if (!string.IsNullOrWhiteSpace(filePath) && !File.Exists(Path.GetFullPath(filePath)))
        {
            errors.Add(new CommandError("file_not_found", $"File '{Path.GetFullPath(filePath)}' does not exist."));
        }

        if (anchorText.Length == 0)
        {
            errors.Add(new CommandError("invalid_input", "Property 'anchor_text' must not be empty."));
        }

        string position = GetPosition(input);
        if (!position.Equals("before", StringComparison.OrdinalIgnoreCase) &&
            !position.Equals("after", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new CommandError("invalid_input", "Property 'position' must be 'before' or 'after'."));
        }

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

        string filePath = Path.GetFullPath(input.GetProperty("file_path").GetString()!);
        string anchorText = input.GetProperty("anchor_text").GetString()!;
        string insertText = input.GetProperty("insert_text").GetString() ?? string.Empty;
        string position = GetPosition(input);
        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        bool includeDiagnostics = InputParsing.GetOptionalBool(input, "include_diagnostics", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 2_000);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);

        string originalContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        int matchCount = CountOccurrences(originalContent, anchorText);
        if (matchCount == 0)
        {
            return new CommandExecutionResult(BuildInsertFailureData(filePath, position, matchCount, anchorText, insertText, "anchor_text_not_found"), new[]
            {
                new CommandError("anchor_text_not_found", "The supplied anchor_text was not found in the file."),
            });
        }

        if (matchCount > 1)
        {
            return new CommandExecutionResult(BuildInsertFailureData(filePath, position, matchCount, anchorText, insertText, "anchor_text_ambiguous"), new[]
            {
                new CommandError("anchor_text_ambiguous", $"The supplied anchor_text matched {matchCount} times. Make anchor_text more specific."),
            });
        }

        int anchorIndex = originalContent.IndexOf(anchorText, StringComparison.Ordinal);
        int insertIndex = position.Equals("before", StringComparison.OrdinalIgnoreCase)
            ? anchorIndex
            : anchorIndex + anchorText.Length;
        string updatedContent = string.Concat(originalContent.AsSpan(0, insertIndex), insertText, originalContent.AsSpan(insertIndex));

        bool changed = !string.Equals(originalContent, updatedContent, StringComparison.Ordinal);
        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedContent, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(
                filePath,
                wroteFile,
                cancellationToken)
            .ConfigureAwait(false);

        object diagnosticsData = await ExactEditDiagnostics.BuildAsync(
                filePath,
                updatedContent,
                includeDiagnostics,
                maxDiagnostics,
                workspacePath,
                workspaceHandle,
                cancellationToken)
            .ConfigureAwait(false);

        object data = new
        {
            file_path = filePath,
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            apply_changes = apply,
            position = position.ToLowerInvariant(),
            match_count = matchCount,
            changed,
            wrote_file = wroteFile,
            anchor_text_character_count = anchorText.Length,
            insert_text_character_count = insertText.Length,
            character_delta = updatedContent.Length - originalContent.Length,
            hot_workspace_refresh = hotWorkspaceRefresh,
            diagnostics_after_insert = diagnosticsData,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static string GetPosition(JsonElement input)
    {
        if (input.TryGetProperty("position", out JsonElement property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        return "after";
    }

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

    private static object BuildInsertFailureData(
        string filePath,
        string position,
        int matchCount,
        string anchorText,
        string insertText,
        string errorCode)
        => new
        {
            file_path = filePath,
            apply_changes = false,
            position = position.ToLowerInvariant(),
            match_count = matchCount,
            changed = false,
            wrote_file = false,
            anchor_text_character_count = anchorText.Length,
            insert_text_character_count = insertText.Length,
            recovery_hint = BuildInsertRecoveryHint(errorCode, matchCount, anchorText),
        };

    private static object BuildInsertRecoveryHint(string errorCode, int matchCount, string anchorText)
    {
        bool multilineAnchor = anchorText.Contains('\n') || anchorText.Contains('\r');
        string problem = errorCode.Equals("anchor_text_ambiguous", StringComparison.Ordinal)
            ? "anchor_text matched multiple locations"
            : "anchor_text did not match current file content";

        string preferredNextStep = multilineAnchor
            ? "Retry with a short unique anchor line from ctx.member_source or ctx.search_text, not a copied multiline block."
            : "Re-read the target with ctx.member_source or ctx.search_text before retrying; the file may have drifted or the anchor may need nearby unique context.";

        return new
        {
            problem,
            match_count = matchCount,
            anchor_was_multiline = multilineAnchor,
            preferred_next_step = preferredNextStep,
            alternatives = new[]
            {
                "For adding a sibling test/member, anchor on the final unique assertion or closing line from ctx.member_source with a small focus window.",
                "For edits inside an existing member, prefer edit.replace_in_member with member_name and exact old_text/new_text.",
                "For whole-member or span-safe edits, use ctx.member_source include_edit_target_text=true plus edit.batch_exact replace_span with expected_text.",
            },
        };
    }
}
