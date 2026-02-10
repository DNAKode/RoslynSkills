using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class DiagnosticsDiffCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "diag.diff",
        Summary: "Diff diagnostics between two C# file snapshots.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "before_path", errors, out string beforePath))
        {
            return errors;
        }

        if (!InputParsing.TryGetRequiredString(input, "after_path", errors, out string afterPath))
        {
            return errors;
        }

        if (!File.Exists(beforePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{beforePath}' does not exist."));
        }

        if (!File.Exists(afterPath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{afterPath}' does not exist."));
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

        string beforePath = input.GetProperty("before_path").GetString()!;
        string afterPath = input.GetProperty("after_path").GetString()!;
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 500, minValue: 1, maxValue: 10_000);

        string beforeContent = await File.ReadAllTextAsync(beforePath, cancellationToken).ConfigureAwait(false);
        string afterContent = await File.ReadAllTextAsync(afterPath, cancellationToken).ConfigureAwait(false);

        SyntaxTree beforeTree = CSharpSyntaxTree.ParseText(beforeContent, path: beforePath, cancellationToken: cancellationToken);
        SyntaxTree afterTree = CSharpSyntaxTree.ParseText(afterContent, path: afterPath, cancellationToken: cancellationToken);

        IReadOnlyList<Diagnostic> beforeDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { beforeTree }, cancellationToken);
        IReadOnlyList<Diagnostic> afterDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { afterTree }, cancellationToken);

        NormalizedDiagnostic[] beforeNormalized = CompilationDiagnostics.Normalize(beforeDiagnostics).Take(maxDiagnostics).ToArray();
        NormalizedDiagnostic[] afterNormalized = CompilationDiagnostics.Normalize(afterDiagnostics).Take(maxDiagnostics).ToArray();

        HashSet<string> beforeKeys = beforeNormalized.Select(GetDiagnosticKey).ToHashSet(StringComparer.Ordinal);
        HashSet<string> afterKeys = afterNormalized.Select(GetDiagnosticKey).ToHashSet(StringComparer.Ordinal);

        NormalizedDiagnostic[] introduced = afterNormalized
            .Where(d => !beforeKeys.Contains(GetDiagnosticKey(d)))
            .ToArray();
        NormalizedDiagnostic[] resolved = beforeNormalized
            .Where(d => !afterKeys.Contains(GetDiagnosticKey(d)))
            .ToArray();

        object data = new
        {
            before_path = beforePath,
            after_path = afterPath,
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
                introduced_count = introduced.Length,
                resolved_count = resolved.Length,
                introduced,
                resolved,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static string GetDiagnosticKey(NormalizedDiagnostic diagnostic)
        => $"{diagnostic.id}|{diagnostic.severity}|{diagnostic.file_path}|{diagnostic.line}|{diagnostic.column}|{diagnostic.message}";
}

