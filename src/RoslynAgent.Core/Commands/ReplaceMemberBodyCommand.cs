using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class ReplaceMemberBodyCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.replace_member_body",
        Summary: "Replace a method body anchored by line/column and return immediate diagnostics.",
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
        InputParsing.TryGetRequiredString(input, "new_body", errors, out _);

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError(
                "file_not_found",
                $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) ||
            !InputParsing.TryGetRequiredInt(input, "line", errors, out int line, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "column", errors, out int column, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredString(input, "new_body", errors, out string newBody))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("file_not_found", $"Input file '{filePath}' does not exist."),
                });
        }

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);

        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        SourceText sourceText = syntaxTree.GetText(cancellationToken);

        if (line > sourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({sourceText.Lines.Count})."),
                });
        }

        int position = GetPositionFromLineColumn(sourceText, line, column);
        SyntaxToken anchorToken = FindAnchorToken(root, position);
        MethodDeclarationSyntax? method = anchorToken.Parent?
            .AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (method is null)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_target", "The provided line/column is not inside a method declaration."),
                });
        }

        if (!TryParseBody(newBody, out BlockSyntax? parsedBody))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("invalid_input", "Property 'new_body' could not be parsed as a valid method body."),
                });
        }

        MethodDeclarationSyntax updatedMethod = method
            .WithBody(parsedBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);

        SyntaxNode newRoot = root.ReplaceNode(method, updatedMethod);
        string updatedSource = newRoot.ToFullString();
        bool changed = !string.Equals(source, updatedSource, StringComparison.Ordinal);

        SyntaxTree updatedTree = CSharpSyntaxTree.ParseText(updatedSource, path: filePath, cancellationToken: cancellationToken);
        IReadOnlyList<Diagnostic> updatedDiagnostics = CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken);
        NormalizedDiagnostic[] normalizedDiagnostics = CompilationDiagnostics.Normalize(updatedDiagnostics)
            .Take(maxDiagnostics)
            .ToArray();

        int diagnosticsErrors = normalizedDiagnostics.Count(d =>
            string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = normalizedDiagnostics.Count(d =>
            string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedSource, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        FileLinePositionSpan methodSpan = method.GetLocation().GetLineSpan();
        object data = new
        {
            file_path = filePath,
            line,
            column,
            member_kind = "MethodDeclaration",
            member_name = method.Identifier.ValueText,
            apply_changes = apply,
            wrote_file = wroteFile,
            changed = changed,
            original_method_start_line = methodSpan.StartLinePosition.Line + 1,
            original_method_end_line = methodSpan.EndLinePosition.Line + 1,
            diagnostics_after_edit = new
            {
                total = updatedDiagnostics.Count,
                returned = normalizedDiagnostics.Length,
                errors = diagnosticsErrors,
                warnings = diagnosticsWarnings,
                diagnostics = normalizedDiagnostics,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static int GetPositionFromLineColumn(SourceText sourceText, int line, int column)
    {
        TextLine textLine = sourceText.Lines[line - 1];
        int requestedOffset = Math.Max(0, column - 1);
        int maxOffset = Math.Max(0, textLine.Span.Length - 1);
        int clampedOffset = Math.Min(requestedOffset, maxOffset);
        return textLine.Start + clampedOffset;
    }

    private static SyntaxToken FindAnchorToken(SyntaxNode root, int position)
    {
        SyntaxToken token = root.FindToken(position);
        if (!token.IsKind(SyntaxKind.None))
        {
            return token;
        }

        if (position > 0)
        {
            return root.FindToken(position - 1);
        }

        return token;
    }

    private static bool TryParseBody(string newBody, out BlockSyntax? body)
    {
        body = null;
        string trimmed = newBody.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            StatementSyntax statement = SyntaxFactory.ParseStatement(trimmed);
            if (statement is BlockSyntax explicitBlock)
            {
                body = explicitBlock;
                return true;
            }

            return false;
        }

        string wrapper = $"class __Temp {{ void __Method() {{ {newBody} }} }}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(wrapper);
        SyntaxNode root = tree.GetRoot();
        body = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Body;
        return body is not null;
    }
}
