using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class ApplyCodeFixCommand : IAgentCommand
{
    private static readonly HashSet<string> SupportedDiagnostics = new(StringComparer.OrdinalIgnoreCase)
    {
        "CS8019",
        "IDE0005",
        "CS0105",
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.apply_code_fix",
        Summary: "Apply a supported diagnostic-driven code fix in a C# file.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        if (!InputParsing.TryGetRequiredString(input, "diagnostic_id", errors, out string diagnosticId))
        {
            return errors;
        }

        if (!SupportedDiagnostics.Contains(diagnosticId))
        {
            errors.Add(new CommandError(
                "unsupported_code_fix",
                $"Diagnostic id '{diagnosticId}' is not supported by edit.apply_code_fix."));
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
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) ||
            !InputParsing.TryGetRequiredString(input, "diagnostic_id", errors, out string diagnosticId))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!SupportedDiagnostics.Contains(diagnosticId))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("unsupported_code_fix", $"Diagnostic id '{diagnosticId}' is not supported by edit.apply_code_fix.") });
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);

        int? onlyLine = null;
        if (input.TryGetProperty("line", out JsonElement lineProperty) &&
            lineProperty.ValueKind == JsonValueKind.Number &&
            lineProperty.TryGetInt32(out int parsedLine) &&
            parsedLine > 0)
        {
            onlyLine = parsedLine;
        }

        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "The input file is not a C# compilation unit.") });
        }

        IReadOnlyList<Diagnostic> beforeDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { syntaxTree }, cancellationToken);
        List<Diagnostic> matchingDiagnostics = beforeDiagnostics
            .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase))
            .Where(d =>
            {
                if (onlyLine is null)
                {
                    return true;
                }

                FileLinePositionSpan lineSpan = d.Location.GetLineSpan();
                return lineSpan.Path == filePath && lineSpan.StartLinePosition.Line + 1 == onlyLine.Value;
            })
            .ToList();

        if (matchingDiagnostics.Count == 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("diagnostic_not_found", $"No matching diagnostic '{diagnosticId}' found for the requested scope.") });
        }

        CompilationUnitSyntax updatedCompilationUnit = diagnosticId.Equals("CS0105", StringComparison.OrdinalIgnoreCase)
            ? RemoveDuplicateUsings(compilationUnit)
            : RemoveUnusedUsingsForDiagnostics(compilationUnit, matchingDiagnostics, filePath);

        string updatedSource = updatedCompilationUnit.ToFullString();
        bool changed = !string.Equals(source, updatedSource, StringComparison.Ordinal);
        SyntaxTree updatedTree = CSharpSyntaxTree.ParseText(updatedSource, path: filePath, cancellationToken: cancellationToken);
        IReadOnlyList<Diagnostic> afterDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken);
        NormalizedDiagnostic[] normalizedAfter = CompilationDiagnostics.Normalize(afterDiagnostics).Take(maxDiagnostics).ToArray();

        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedSource, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        object data = new
        {
            file_path = filePath,
            diagnostic_id = diagnosticId,
            matched_diagnostic_count = matchingDiagnostics.Count,
            apply_changes = apply,
            wrote_file = wroteFile,
            changed,
            diagnostics_after_edit = new
            {
                total = afterDiagnostics.Count,
                returned = normalizedAfter.Length,
                errors = normalizedAfter.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = normalizedAfter.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                diagnostics = normalizedAfter,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static CompilationUnitSyntax RemoveUnusedUsingsForDiagnostics(
        CompilationUnitSyntax compilationUnit,
        IReadOnlyList<Diagnostic> diagnostics,
        string filePath)
    {
        HashSet<int> lines = diagnostics
            .Select(d =>
            {
                FileLinePositionSpan span = d.Location.GetLineSpan();
                return span.Path == filePath ? span.StartLinePosition.Line + 1 : -1;
            })
            .Where(line => line > 0)
            .ToHashSet();

        if (lines.Count == 0)
        {
            return compilationUnit;
        }

        List<UsingDirectiveSyntax> filtered = compilationUnit.Usings
            .Where(u =>
            {
                FileLinePositionSpan lineSpan = u.GetLocation().GetLineSpan();
                int line = lineSpan.StartLinePosition.Line + 1;
                return !lines.Contains(line);
            })
            .ToList();

        return compilationUnit.WithUsings(SyntaxFactory.List(filtered));
    }

    private static CompilationUnitSyntax RemoveDuplicateUsings(CompilationUnitSyntax compilationUnit)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<UsingDirectiveSyntax> filtered = new();
        foreach (UsingDirectiveSyntax usingDirective in compilationUnit.Usings)
        {
            string key = $"{usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)}::{usingDirective.Name}";
            if (seen.Add(key))
            {
                filtered.Add(usingDirective);
            }
        }

        return compilationUnit.WithUsings(SyntaxFactory.List(filtered));
    }
}
