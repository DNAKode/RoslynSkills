using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using System.Text;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class MemberSourceCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.member_source",
        Summary: "Return source for the anchored member (or body-only view) with bounded context.",
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

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        if (input.TryGetProperty("mode", out JsonElement modeProperty) &&
            modeProperty.ValueKind == JsonValueKind.String)
        {
            string modeRaw = modeProperty.GetString() ?? string.Empty;
            if (!TryParseMode(modeRaw, out _))
            {
                errors.Add(new CommandError("invalid_input", "Property 'mode' must be 'member' or 'body'."));
            }
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) ||
            !InputParsing.TryGetRequiredInt(input, "line", errors, out int line, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "column", errors, out int column, minValue: 1, maxValue: 1_000_000))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        string modeRaw = "member";
        if (input.TryGetProperty("mode", out JsonElement modeProperty) &&
            modeProperty.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(modeProperty.GetString()))
        {
            modeRaw = modeProperty.GetString()!;
        }

        if (!TryParseMode(modeRaw, out SourceMode mode))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'mode' must be 'member' or 'body'.") });
        }

        bool includeLineNumbers = InputParsing.GetOptionalBool(input, "include_line_numbers", defaultValue: false);
        bool includeTrivia = InputParsing.GetOptionalBool(input, "include_trivia", defaultValue: false);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: false);
        bool includeSourceText = InputParsing.GetOptionalBool(input, "include_source_text", defaultValue: !brief);
        int contextBefore = InputParsing.GetOptionalInt(input, "context_lines_before", defaultValue: 0, minValue: 0, maxValue: 500);
        int contextAfter = InputParsing.GetOptionalInt(input, "context_lines_after", defaultValue: 0, minValue: 0, maxValue: 500);
        int maxChars = InputParsing.GetOptionalInt(input, "max_chars", defaultValue: 8_000, minValue: 200, maxValue: 200_000);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        SyntaxNode? anchorNode = anchorToken.Parent;
        if (anchorNode is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "No syntax node exists at the provided location.") });
        }

        MemberDeclarationSyntax? member = anchorNode
            .AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseNamespaceDeclarationSyntax);

        if (member is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "The provided line/column does not resolve to a member declaration.") });
        }

        SyntaxNode targetNode = ResolveTargetNode(member, mode);
        TextSpan memberSpan = includeTrivia ? member.FullSpan : member.Span;
        TextSpan targetSpan = includeTrivia ? targetNode.FullSpan : targetNode.Span;
        LinePositionSpan memberLineSpan = analysis.SourceText.Lines.GetLinePositionSpan(memberSpan);
        LinePositionSpan targetLineSpan = analysis.SourceText.Lines.GetLinePositionSpan(targetSpan);

        int targetStartLine = targetLineSpan.Start.Line + 1;
        int targetEndLine = Math.Max(targetStartLine, targetLineSpan.End.Line + 1);
        int snippetStartLine = Math.Max(1, targetStartLine - contextBefore);
        int snippetEndLine = Math.Min(analysis.SourceText.Lines.Count, targetEndLine + contextAfter);

        string source = string.Empty;
        bool truncated = false;
        int sourceCharacterCount = 0;
        if (includeSourceText)
        {
            source = BuildSnippet(analysis.SourceText, snippetStartLine, snippetEndLine, includeLineNumbers);
            truncated = source.Length > maxChars;
            if (truncated)
            {
                source = source[..maxChars];
            }

            sourceCharacterCount = source.Length;
        }

        ISymbol? declaredSymbol = analysis.SemanticModel.GetDeclaredSymbol(member, cancellationToken);
        Dictionary<string, object?> data = new()
        {
            ["query"] = new
            {
                file_path = filePath,
                line,
                column,
                mode = modeRaw,
                brief,
                include_source_text = includeSourceText,
                include_line_numbers = includeLineNumbers,
                include_trivia = includeTrivia,
                context_lines_before = contextBefore,
                context_lines_after = contextAfter,
                max_chars = maxChars,
            },
            ["member"] = new
            {
                member_kind = member.Kind().ToString(),
                member_name = GetMemberName(member),
                symbol_display = declaredSymbol?.ToDisplayString(),
                symbol_id = declaredSymbol is null ? null : CommandTextFormatting.GetStableSymbolId(declaredSymbol),
                declaration_start_line = memberLineSpan.Start.Line + 1,
                declaration_end_line = Math.Max(memberLineSpan.Start.Line + 1, memberLineSpan.End.Line + 1),
                source_start_line = snippetStartLine,
                source_end_line = snippetEndLine,
                source_line_count = Math.Max(0, snippetEndLine - snippetStartLine + 1),
            },
            ["source"] = includeSourceText
                ? new
                {
                    text = source,
                    truncated,
                    character_count = sourceCharacterCount,
                }
                : new
            {
                omitted = true,
                truncated = false,
                character_count = sourceCharacterCount,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool TryParseMode(string modeRaw, out SourceMode mode)
    {
        if (string.Equals(modeRaw, "member", StringComparison.OrdinalIgnoreCase))
        {
            mode = SourceMode.Member;
            return true;
        }

        if (string.Equals(modeRaw, "body", StringComparison.OrdinalIgnoreCase))
        {
            mode = SourceMode.Body;
            return true;
        }

        mode = SourceMode.Member;
        return false;
    }

    private static SyntaxNode ResolveTargetNode(MemberDeclarationSyntax member, SourceMode mode)
    {
        if (mode != SourceMode.Body)
        {
            return member;
        }

        return member switch
        {
            BaseMethodDeclarationSyntax method when method.Body is not null => method.Body,
            BaseMethodDeclarationSyntax method when method.ExpressionBody is not null => method.ExpressionBody.Expression,
            PropertyDeclarationSyntax property when property.AccessorList is not null => property.AccessorList,
            PropertyDeclarationSyntax property when property.ExpressionBody is not null => property.ExpressionBody.Expression,
            IndexerDeclarationSyntax indexer when indexer.AccessorList is not null => indexer.AccessorList,
            IndexerDeclarationSyntax indexer when indexer.ExpressionBody is not null => indexer.ExpressionBody.Expression,
            EventDeclarationSyntax eventDeclaration when eventDeclaration.AccessorList is not null => eventDeclaration.AccessorList,
            _ => member,
        };
    }

    private static string BuildSnippet(SourceText sourceText, int startLine, int endLine, bool includeLineNumbers)
    {
        StringBuilder builder = new();
        for (int lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            TextLine line = sourceText.Lines[lineNumber - 1];
            if (includeLineNumbers)
            {
                builder.Append(lineNumber.ToString("D4"));
                builder.Append(": ");
            }

            builder.Append(line.ToString());
            if (lineNumber < endLine)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            DestructorDeclarationSyntax destructor => destructor.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            EventDeclarationSyntax @event => @event.Identifier.ValueText,
            EventFieldDeclarationSyntax eventField => string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            IndexerDeclarationSyntax => "this[]",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion => conversion.Type.ToString(),
            BaseTypeDeclarationSyntax typeDeclaration => typeDeclaration.Identifier.ValueText,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
            GlobalStatementSyntax => "<global>",
            _ => member.Kind().ToString(),
        };
    }

    private enum SourceMode
    {
        Member = 0,
        Body = 1,
    }
}
