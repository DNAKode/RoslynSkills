using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class ChangeSignatureCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.change_signature",
        Summary: "Change method signature anchored by line/column (parameters, return type, optional name).",
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

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredString(input, "parameters", errors, out _);
        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        if (input.TryGetProperty("new_name", out JsonElement newNameProperty) && newNameProperty.ValueKind == JsonValueKind.String)
        {
            string? newName = newNameProperty.GetString();
            if (!string.IsNullOrWhiteSpace(newName) && !SyntaxFacts.IsValidIdentifier(newName))
            {
                errors.Add(new CommandError(
                    "invalid_input",
                    $"Property 'new_name' value '{newName}' is not a valid C# identifier."));
            }
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) ||
            !InputParsing.TryGetRequiredInt(input, "line", errors, out int line, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "column", errors, out int column, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredString(input, "parameters", errors, out string parametersRaw))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);

        string? newName = null;
        if (input.TryGetProperty("new_name", out JsonElement newNameProperty) && newNameProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = newNameProperty.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                if (!SyntaxFacts.IsValidIdentifier(candidate))
                {
                    return new CommandExecutionResult(
                        null,
                        new[] { new CommandError("invalid_input", $"Property 'new_name' value '{candidate}' is not a valid C# identifier.") });
                }

                newName = candidate;
            }
        }

        string? returnTypeRaw = null;
        if (input.TryGetProperty("return_type", out JsonElement returnTypeProperty) && returnTypeProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = returnTypeProperty.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                returnTypeRaw = candidate;
            }
        }

        if (!TryParseParameterList(parametersRaw, out ParameterListSyntax? parameterList))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'parameters' could not be parsed as a valid C# parameter list.") });
        }

        TypeSyntax? parsedReturnType = null;
        if (returnTypeRaw is not null)
        {
            parsedReturnType = SyntaxFactory.ParseTypeName(returnTypeRaw);
            if (parsedReturnType.ContainsDiagnostics || parsedReturnType.IsMissing)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_input", $"Property 'return_type' value '{returnTypeRaw}' is not a valid C# type.") });
            }
        }

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        MethodDeclarationSyntax? method = anchorToken.Parent?
            .AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (method is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "The provided line/column is not inside a method declaration.") });
        }

        MethodDeclarationSyntax updatedMethod = method.WithParameterList(parameterList!);
        if (parsedReturnType is not null)
        {
            TypeSyntax normalizedReturnType = parsedReturnType
                .WithLeadingTrivia(method.ReturnType.GetLeadingTrivia())
                .WithTrailingTrivia(method.ReturnType.GetTrailingTrivia());
            updatedMethod = updatedMethod.WithReturnType(normalizedReturnType);
        }

        if (!string.IsNullOrWhiteSpace(newName))
        {
            SyntaxToken oldIdentifier = updatedMethod.Identifier;
            updatedMethod = updatedMethod.WithIdentifier(SyntaxFactory.Identifier(
                oldIdentifier.LeadingTrivia,
                newName,
                oldIdentifier.TrailingTrivia));
        }

        SyntaxNode updatedRoot = analysis.Root.ReplaceNode(method, updatedMethod);
        string updatedSource = updatedRoot.ToFullString();
        bool changed = !string.Equals(analysis.Source, updatedSource, StringComparison.Ordinal);

        SyntaxTree updatedTree = CSharpSyntaxTree.ParseText(updatedSource, path: filePath, cancellationToken: cancellationToken);
        IReadOnlyList<Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken);
        NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics).Take(maxDiagnostics).ToArray();

        int diagnosticsErrors = normalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = normalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedSource, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        object data = new
        {
            file_path = filePath,
            line,
            column,
            member_name = method.Identifier.ValueText,
            new_member_name = updatedMethod.Identifier.ValueText,
            old_signature = method.ParameterList.ToString(),
            new_signature = updatedMethod.ParameterList.ToString(),
            old_return_type = method.ReturnType.ToString(),
            new_return_type = updatedMethod.ReturnType.ToString(),
            apply_changes = apply,
            wrote_file = wroteFile,
            changed,
            diagnostics_after_edit = new
            {
                total = diagnostics.Count,
                returned = normalized.Length,
                errors = diagnosticsErrors,
                warnings = diagnosticsWarnings,
                diagnostics = normalized,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool TryParseParameterList(string raw, out ParameterListSyntax? parameterList)
    {
        parameterList = null;
        string trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (!trimmed.StartsWith('('))
        {
            trimmed = $"({trimmed})";
        }

        parameterList = SyntaxFactory.ParseParameterList(trimmed);
        return !parameterList.ContainsDiagnostics && !parameterList.IsMissing;
    }
}
