using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class AddMemberCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.add_member",
        Summary: "Add a member declaration to a target type in a C# file.",
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

        InputParsing.TryGetRequiredString(input, "member_declaration", errors, out _);
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
            !InputParsing.TryGetRequiredString(input, "member_declaration", errors, out string memberDeclaration))
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

        string? typeName = null;
        if (input.TryGetProperty("type_name", out JsonElement typeNameProperty) && typeNameProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = typeNameProperty.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                typeName = candidate;
            }
        }

        string insertPosition = "end";
        if (input.TryGetProperty("position", out JsonElement positionProperty) && positionProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = positionProperty.GetString();
            if (string.Equals(candidate, "start", StringComparison.OrdinalIgnoreCase))
            {
                insertPosition = "start";
            }
        }

        MemberDeclarationSyntax? parsedMember = SyntaxFactory.ParseMemberDeclaration(memberDeclaration);
        if (parsedMember is null || parsedMember.ContainsDiagnostics)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'member_declaration' is not a valid C# member declaration.") });
        }

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        TypeDeclarationSyntax? targetType = ResolveTargetType(input, analysis, typeName);
        if (targetType is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "Unable to resolve a target type declaration for member insertion.") });
        }

        MemberDeclarationSyntax formattedMember = parsedMember
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

        TypeDeclarationSyntax updatedType = insertPosition == "start"
            ? targetType.WithMembers(targetType.Members.Insert(0, formattedMember))
            : targetType.WithMembers(targetType.Members.Add(formattedMember));

        SyntaxNode updatedRoot = analysis.Root.ReplaceNode(targetType, updatedType);
        string updatedSource = updatedRoot.ToFullString();
        bool changed = !string.Equals(updatedSource, analysis.Source, StringComparison.Ordinal);

        SyntaxTree updatedTree = CSharpSyntaxTree.ParseText(updatedSource, path: filePath, cancellationToken: cancellationToken);
        IReadOnlyList<Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken);
        NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics).Take(maxDiagnostics).ToArray();

        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedSource, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        object data = new
        {
            file_path = filePath,
            target_type = targetType.Identifier.ValueText,
            member_kind = parsedMember.Kind().ToString(),
            insert_position = insertPosition,
            apply_changes = apply,
            wrote_file = wroteFile,
            changed,
            diagnostics_after_edit = new
            {
                total = diagnostics.Count,
                returned = normalized.Length,
                errors = normalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = normalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                diagnostics = normalized,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static TypeDeclarationSyntax? ResolveTargetType(JsonElement input, CommandFileAnalysis analysis, string? typeName)
    {
        if (input.TryGetProperty("line", out JsonElement lineProperty) &&
            input.TryGetProperty("column", out JsonElement columnProperty) &&
            lineProperty.ValueKind == JsonValueKind.Number &&
            columnProperty.ValueKind == JsonValueKind.Number &&
            lineProperty.TryGetInt32(out int line) &&
            columnProperty.TryGetInt32(out int column) &&
            line >= 1 &&
            line <= analysis.SourceText.Lines.Count)
        {
            SyntaxToken anchor = analysis.FindAnchorToken(line, column);
            TypeDeclarationSyntax? anchoredType = anchor.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (anchoredType is not null)
            {
                return anchoredType;
            }
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            TypeDeclarationSyntax? namedType = analysis.Root
                .DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => string.Equals(t.Identifier.ValueText, typeName, StringComparison.Ordinal));
            if (namedType is not null)
            {
                return namedType;
            }
        }

        return analysis.Root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
    }
}

