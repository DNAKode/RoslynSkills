using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class ReplaceTextCommand : IAgentCommand
{
    private static readonly HashSet<string> CSharpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.replace_text",
        Summary: "Replace an exact text snippet in one file with immediate optional C# diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath);
        InputParsing.TryGetRequiredString(input, "old_text", errors, out string oldText);
        InputParsing.TryGetRequiredString(input, "new_text", errors, out _);

        if (!string.IsNullOrWhiteSpace(filePath) && !File.Exists(Path.GetFullPath(filePath)))
        {
            errors.Add(new CommandError("file_not_found", $"File '{Path.GetFullPath(filePath)}' does not exist."));
        }

        if (oldText.Length == 0)
        {
            errors.Add(new CommandError("invalid_input", "Property 'old_text' must not be empty."));
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
        string oldText = input.GetProperty("old_text").GetString()!;
        string newText = input.GetProperty("new_text").GetString() ?? string.Empty;
        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        bool replaceAll = InputParsing.GetOptionalBool(input, "replace_all", defaultValue: false);
        bool includeDiagnostics = InputParsing.GetOptionalBool(input, "include_diagnostics", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 2_000);

        string originalContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        int matchCount = CountOccurrences(originalContent, oldText);
        if (matchCount == 0)
        {
            return new CommandExecutionResult(null, new[]
            {
                new CommandError("old_text_not_found", "The supplied old_text was not found in the file."),
            });
        }

        if (!replaceAll && matchCount > 1)
        {
            return new CommandExecutionResult(null, new[]
            {
                new CommandError("old_text_ambiguous", $"The supplied old_text matched {matchCount} times. Make old_text more specific or set replace_all=true."),
            });
        }

        string updatedContent = replaceAll
            ? originalContent.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(originalContent, oldText, newText);

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
            replace_all = replaceAll,
            match_count = matchCount,
            changed,
            wrote_file = wroteFile,
            old_text_character_count = oldText.Length,
            new_text_character_count = newText.Length,
            character_delta = updatedContent.Length - originalContent.Length,
            diagnostics_after_replace = diagnosticsData,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
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

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        int index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
    }
}
