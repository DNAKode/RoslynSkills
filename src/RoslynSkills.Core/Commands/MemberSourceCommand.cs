using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text;
using System.Text.Json;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace RoslynSkills.Core.Commands;

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

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);

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

        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath).ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, analysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

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

        SyntaxNode memberNode;
        TextSpan targetSpan;
        ISymbol? symbol;
        string memberName;

        if (string.Equals(analysis.Language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            SyntaxNode? vbMemberNode = FindVbMemberNode(anchorNode, preferBodyContainer: mode == SourceMode.Body);
            if (vbMemberNode is null)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_target", "The provided line/column does not resolve to a member declaration.") });
            }

            memberNode = vbMemberNode;
            targetSpan = ResolveVbTargetSpan(memberNode, mode, includeTrivia);
            symbol = GetVbMemberSymbol(memberNode, analysis.SemanticModel, cancellationToken);
            memberName = GetVbMemberName(memberNode, symbol);
        }
        else
        {
            CSharpSyntax.MemberDeclarationSyntax? csharpMember = anchorNode
                .AncestorsAndSelf()
                .OfType<CSharpSyntax.MemberDeclarationSyntax>()
                .FirstOrDefault(m => m is not CSharpSyntax.BaseNamespaceDeclarationSyntax);

            if (csharpMember is null)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_target", "The provided line/column does not resolve to a member declaration.") });
            }

            memberNode = csharpMember;
            SyntaxNode targetNode = ResolveCSharpTargetNode(csharpMember, mode);
            targetSpan = includeTrivia ? targetNode.FullSpan : targetNode.Span;
            symbol = analysis.SemanticModel.GetDeclaredSymbol(csharpMember, cancellationToken);
            memberName = GetCSharpMemberName(csharpMember);
        }

        TextSpan memberSpan = includeTrivia ? memberNode.FullSpan : memberNode.Span;
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

        Dictionary<string, object?> data = new()
        {
            ["query"] = new
            {
                file_path = analysis.FilePath,
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
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            ["member"] = new
            {
                member_kind = CommandLanguageServices.GetSyntaxKindName(memberNode),
                member_name = memberName,
                symbol_display = symbol?.ToDisplayString(),
                symbol_id = symbol is null ? null : CommandTextFormatting.GetStableSymbolId(symbol),
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

    private static SyntaxNode ResolveCSharpTargetNode(CSharpSyntax.MemberDeclarationSyntax member, SourceMode mode)
    {
        if (mode != SourceMode.Body)
        {
            return member;
        }

        return member switch
        {
            CSharpSyntax.BaseMethodDeclarationSyntax method when method.Body is not null => method.Body,
            CSharpSyntax.BaseMethodDeclarationSyntax method when method.ExpressionBody is not null => method.ExpressionBody.Expression,
            CSharpSyntax.PropertyDeclarationSyntax property when property.AccessorList is not null => property.AccessorList,
            CSharpSyntax.PropertyDeclarationSyntax property when property.ExpressionBody is not null => property.ExpressionBody.Expression,
            CSharpSyntax.IndexerDeclarationSyntax indexer when indexer.AccessorList is not null => indexer.AccessorList,
            CSharpSyntax.IndexerDeclarationSyntax indexer when indexer.ExpressionBody is not null => indexer.ExpressionBody.Expression,
            CSharpSyntax.EventDeclarationSyntax eventDeclaration when eventDeclaration.AccessorList is not null => eventDeclaration.AccessorList,
            _ => member,
        };
    }

    private static SyntaxNode? FindVbMemberNode(SyntaxNode anchorNode, bool preferBodyContainer)
    {
        SyntaxNode? firstMatch = null;
        foreach (SyntaxNode candidate in anchorNode.AncestorsAndSelf())
        {
            if (candidate is VbSyntax.NamespaceBlockSyntax)
            {
                continue;
            }

            if (!IsVbMemberNode(candidate))
            {
                continue;
            }

            firstMatch ??= candidate;
            if (preferBodyContainer && IsVbBodyContainer(candidate))
            {
                return candidate;
            }

            if (!preferBodyContainer)
            {
                return candidate;
            }
        }

        return firstMatch;
    }

    private static bool IsVbBodyContainer(SyntaxNode node)
    {
        return node is VbSyntax.MethodBlockBaseSyntax
            or VbSyntax.PropertyBlockSyntax
            or VbSyntax.EventBlockSyntax;
    }

    private static bool IsVbMemberNode(SyntaxNode node)
    {
        return node is VbSyntax.MethodBlockBaseSyntax
            or VbSyntax.MethodStatementSyntax
            or VbSyntax.PropertyBlockSyntax
            or VbSyntax.PropertyStatementSyntax
            or VbSyntax.FieldDeclarationSyntax
            or VbSyntax.EventBlockSyntax
            or VbSyntax.EventStatementSyntax
            or VbSyntax.EnumMemberDeclarationSyntax
            or VbSyntax.DelegateStatementSyntax
            or VbSyntax.ClassBlockSyntax
            or VbSyntax.StructureBlockSyntax
            or VbSyntax.InterfaceBlockSyntax
            or VbSyntax.ModuleBlockSyntax
            or VbSyntax.EnumBlockSyntax;
    }

    private static TextSpan ResolveVbTargetSpan(SyntaxNode memberNode, SourceMode mode, bool includeTrivia)
    {
        if (mode != SourceMode.Body)
        {
            return includeTrivia ? memberNode.FullSpan : memberNode.Span;
        }

        return memberNode switch
        {
            VbSyntax.MethodBlockBaseSyntax methodBlock => BuildBodySpan(methodBlock.Statements, methodBlock, includeTrivia),
            VbSyntax.PropertyBlockSyntax propertyBlock => BuildBodySpan(propertyBlock.Accessors, propertyBlock, includeTrivia),
            VbSyntax.EventBlockSyntax eventBlock => BuildBodySpan(eventBlock.Accessors, eventBlock, includeTrivia),
            _ => includeTrivia ? memberNode.FullSpan : memberNode.Span,
        };
    }

    private static TextSpan BuildBodySpan<TNode>(SyntaxList<TNode> nodes, SyntaxNode fallback, bool includeTrivia)
        where TNode : SyntaxNode
    {
        if (nodes.Count == 0)
        {
            return includeTrivia ? fallback.FullSpan : fallback.Span;
        }

        TNode first = nodes[0];
        TNode last = nodes[nodes.Count - 1];
        int start = includeTrivia ? first.FullSpan.Start : first.Span.Start;
        int end = includeTrivia ? last.FullSpan.End : last.Span.End;
        return TextSpan.FromBounds(start, end);
    }

    private static string GetVbMemberName(SyntaxNode memberNode, ISymbol? symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol?.Name))
        {
            return symbol!.Name;
        }

        return memberNode switch
        {
            VbSyntax.MethodBlockBaseSyntax methodBlock => GetVbMethodBaseName(methodBlock.BlockStatement),
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier.ValueText,
            VbSyntax.SubNewStatementSyntax => "New",
            VbSyntax.OperatorStatementSyntax => "Operator",
            VbSyntax.PropertyBlockSyntax propertyBlock => propertyBlock.PropertyStatement.Identifier.ValueText,
            VbSyntax.PropertyStatementSyntax propertyStatement => propertyStatement.Identifier.ValueText,
            VbSyntax.FieldDeclarationSyntax field => string.Join(", ", field.Declarators.SelectMany(d => d.Names).Select(n => n.Identifier.ValueText)),
            VbSyntax.EventBlockSyntax eventBlock => eventBlock.EventStatement.Identifier.ValueText,
            VbSyntax.EventStatementSyntax eventStatement => eventStatement.Identifier.ValueText,
            VbSyntax.EnumMemberDeclarationSyntax enumMember => enumMember.Identifier.ValueText,
            VbSyntax.DelegateStatementSyntax delegateStatement => delegateStatement.Identifier.ValueText,
            VbSyntax.ClassBlockSyntax classBlock => classBlock.ClassStatement.Identifier.ValueText,
            VbSyntax.StructureBlockSyntax structureBlock => structureBlock.StructureStatement.Identifier.ValueText,
            VbSyntax.InterfaceBlockSyntax interfaceBlock => interfaceBlock.InterfaceStatement.Identifier.ValueText,
            VbSyntax.ModuleBlockSyntax moduleBlock => moduleBlock.ModuleStatement.Identifier.ValueText,
            VbSyntax.EnumBlockSyntax enumBlock => enumBlock.EnumStatement.Identifier.ValueText,
            _ => CommandLanguageServices.GetSyntaxKindName(memberNode),
        };
    }

    private static string GetVbMethodBaseName(VbSyntax.MethodBaseSyntax methodBase)
    {
        return methodBase switch
        {
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier.ValueText,
            VbSyntax.SubNewStatementSyntax => "New",
            VbSyntax.OperatorStatementSyntax => "Operator",
            _ => methodBase.Kind().ToString(),
        };
    }

    private static ISymbol? GetVbMemberSymbol(
        SyntaxNode memberNode,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        SyntaxToken token = memberNode switch
        {
            VbSyntax.MethodBlockBaseSyntax methodBlock => GetVbMethodBaseIdentifierToken(methodBlock.BlockStatement),
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier,
            VbSyntax.PropertyBlockSyntax propertyBlock => propertyBlock.PropertyStatement.Identifier,
            VbSyntax.PropertyStatementSyntax propertyStatement => propertyStatement.Identifier,
            VbSyntax.FieldDeclarationSyntax field => field.Declarators.SelectMany(d => d.Names).FirstOrDefault()?.Identifier ?? default,
            VbSyntax.EventBlockSyntax eventBlock => eventBlock.EventStatement.Identifier,
            VbSyntax.EventStatementSyntax eventStatement => eventStatement.Identifier,
            VbSyntax.EnumMemberDeclarationSyntax enumMember => enumMember.Identifier,
            VbSyntax.DelegateStatementSyntax delegateStatement => delegateStatement.Identifier,
            VbSyntax.ClassBlockSyntax classBlock => classBlock.ClassStatement.Identifier,
            VbSyntax.StructureBlockSyntax structureBlock => structureBlock.StructureStatement.Identifier,
            VbSyntax.InterfaceBlockSyntax interfaceBlock => interfaceBlock.InterfaceStatement.Identifier,
            VbSyntax.ModuleBlockSyntax moduleBlock => moduleBlock.ModuleStatement.Identifier,
            VbSyntax.EnumBlockSyntax enumBlock => enumBlock.EnumStatement.Identifier,
            _ => memberNode.GetFirstToken(),
        };

        if (token.RawKind == 0)
        {
            return null;
        }

        return SymbolResolution.GetSymbolForToken(token, semanticModel, cancellationToken);
    }

    private static SyntaxToken GetVbMethodBaseIdentifierToken(VbSyntax.MethodBaseSyntax methodBase)
    {
        return methodBase switch
        {
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier,
            VbSyntax.SubNewStatementSyntax ctor => ctor.NewKeyword,
            VbSyntax.OperatorStatementSyntax op => op.OperatorToken,
            _ => default,
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

    private static string GetCSharpMemberName(CSharpSyntax.MemberDeclarationSyntax member)
    {
        return member switch
        {
            CSharpSyntax.MethodDeclarationSyntax method => method.Identifier.ValueText,
            CSharpSyntax.ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            CSharpSyntax.DestructorDeclarationSyntax destructor => destructor.Identifier.ValueText,
            CSharpSyntax.PropertyDeclarationSyntax property => property.Identifier.ValueText,
            CSharpSyntax.FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            CSharpSyntax.EventDeclarationSyntax @event => @event.Identifier.ValueText,
            CSharpSyntax.EventFieldDeclarationSyntax eventField => string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            CSharpSyntax.IndexerDeclarationSyntax => "this[]",
            CSharpSyntax.OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
            CSharpSyntax.ConversionOperatorDeclarationSyntax conversion => conversion.Type.ToString(),
            CSharpSyntax.BaseTypeDeclarationSyntax typeDeclaration => typeDeclaration.Identifier.ValueText,
            CSharpSyntax.DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
            CSharpSyntax.GlobalStatementSyntax => "<global>",
            _ => member.Kind().ToString(),
        };
    }

    private enum SourceMode
    {
        Member = 0,
        Body = 1,
    }
}
