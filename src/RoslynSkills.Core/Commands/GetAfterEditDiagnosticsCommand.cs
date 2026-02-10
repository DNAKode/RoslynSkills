using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class GetAfterEditDiagnosticsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "diag.get_after_edit",
        Summary: "Return diagnostics for proposed content versus current file diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 100, minValue: 1, maxValue: 1_000);

        string beforeContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        string afterContent = beforeContent;
        bool usedProposedContent = false;
        if (input.TryGetProperty("proposed_content", out JsonElement proposedContentProperty) &&
            proposedContentProperty.ValueKind == JsonValueKind.String)
        {
            string? proposed = proposedContentProperty.GetString();
            if (proposed is not null)
            {
                afterContent = proposed;
                usedProposedContent = true;
            }
        }

        SyntaxTree beforeTree = CSharpSyntaxTree.ParseText(beforeContent, path: filePath, cancellationToken: cancellationToken);
        SyntaxTree afterTree = CSharpSyntaxTree.ParseText(afterContent, path: filePath, cancellationToken: cancellationToken);

        IReadOnlyList<Diagnostic> beforeDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { beforeTree }, cancellationToken);
        IReadOnlyList<Diagnostic> afterDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { afterTree }, cancellationToken);

        NormalizedDiagnostic[] beforeNormalized = CompilationDiagnostics.Normalize(beforeDiagnostics).Take(maxDiagnostics).ToArray();
        NormalizedDiagnostic[] afterNormalized = CompilationDiagnostics.Normalize(afterDiagnostics).Take(maxDiagnostics).ToArray();

        DiagnosticDelta delta = ComputeDelta(beforeNormalized, afterNormalized);

        object data = new
        {
            file_path = filePath,
            used_proposed_content = usedProposedContent,
            before = new
            {
                total = beforeDiagnostics.Count,
                returned = beforeNormalized.Length,
                errors = beforeNormalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = beforeNormalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                diagnostics = beforeNormalized,
            },
            after = new
            {
                total = afterDiagnostics.Count,
                returned = afterNormalized.Length,
                errors = afterNormalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = afterNormalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                diagnostics = afterNormalized,
            },
            delta = new
            {
                introduced = delta.Introduced,
                resolved = delta.Resolved,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static DiagnosticDelta ComputeDelta(
        IReadOnlyList<NormalizedDiagnostic> before,
        IReadOnlyList<NormalizedDiagnostic> after)
    {
        HashSet<string> beforeSet = before.Select(GetDiagnosticKey).ToHashSet(StringComparer.Ordinal);
        HashSet<string> afterSet = after.Select(GetDiagnosticKey).ToHashSet(StringComparer.Ordinal);

        int introduced = afterSet.Count(key => !beforeSet.Contains(key));
        int resolved = beforeSet.Count(key => !afterSet.Contains(key));
        return new DiagnosticDelta(introduced, resolved);
    }

    private static string GetDiagnosticKey(NormalizedDiagnostic diagnostic)
        => $"{diagnostic.id}|{diagnostic.severity}|{diagnostic.file_path}|{diagnostic.line}|{diagnostic.column}|{diagnostic.message}";

    private sealed record DiagnosticDelta(int Introduced, int Resolved);
}

