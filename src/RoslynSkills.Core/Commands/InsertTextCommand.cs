using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class InsertTextCommand : IAgentCommand
{
    private static readonly HashSet<string> CSharpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
    };

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

        string originalContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        int matchCount = CountOccurrences(originalContent, anchorText);
        if (matchCount == 0)
        {
            return new CommandExecutionResult(null, new[]
            {
                new CommandError("anchor_text_not_found", "The supplied anchor_text was not found in the file."),
            });
        }

        if (matchCount > 1)
        {
            return new CommandExecutionResult(null, new[]
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

        object diagnosticsData = BuildDiagnosticsData(filePath, updatedContent, includeDiagnostics, maxDiagnostics, cancellationToken);

        object data = new
        {
            file_path = filePath,
            apply_changes = apply,
            position = position.ToLowerInvariant(),
            match_count = matchCount,
            changed,
            wrote_file = wroteFile,
            anchor_text_character_count = anchorText.Length,
            insert_text_character_count = insertText.Length,
            character_delta = updatedContent.Length - originalContent.Length,
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

    private static object BuildDiagnosticsData(
        string filePath,
        string content,
        bool includeDiagnostics,
        int maxDiagnostics,
        CancellationToken cancellationToken)
    {
        bool isCSharpFile = CSharpExtensions.Contains(Path.GetExtension(filePath));
        if (!includeDiagnostics || !isCSharpFile)
        {
            return new
            {
                evaluated = false,
                reason = isCSharpFile ? "diagnostics_disabled" : "not_csharp_source_file",
                total = 0,
                returned = 0,
                errors = 0,
                warnings = 0,
                diagnostics = Array.Empty<NormalizedDiagnostic>(),
            };
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(content, path: filePath, cancellationToken: cancellationToken);
        IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { tree }, cancellationToken);
        NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics)
            .Take(maxDiagnostics)
            .ToArray();

        return new
        {
            evaluated = true,
            reason = string.Empty,
            total = diagnostics.Count,
            returned = normalized.Length,
            errors = normalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
            warnings = normalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
            diagnostics = normalized,
        };
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
}
