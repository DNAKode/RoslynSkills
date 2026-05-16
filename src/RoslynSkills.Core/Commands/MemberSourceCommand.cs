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
    private const int LargeMissingFocusMemberLineThreshold = 200;
    private const int MissingFocusFallbackLineWindow = 80;

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

        string? memberName = GetOptionalTrimmedString(input, "member_name");
        if (string.IsNullOrWhiteSpace(memberName))
        {
            InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
            InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
        }
        else
        {
            InputParsing.ValidateOptionalInt(input, "line", errors, minValue: 1, maxValue: 1_000_000);
            InputParsing.ValidateOptionalInt(input, "column", errors, minValue: 1, maxValue: 1_000_000);
        }

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "include_edit_target_text", errors);

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
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return new CommandExecutionResult(null, errors);
        }

        string? requestedMemberName = GetOptionalTrimmedString(input, "member_name");
        bool hasMemberNameAnchor = !string.IsNullOrWhiteSpace(requestedMemberName);
        int line = InputParsing.GetOptionalInt(input, "line", defaultValue: 0, minValue: 0, maxValue: 1_000_000);
        int column = InputParsing.GetOptionalInt(input, "column", defaultValue: 0, minValue: 0, maxValue: 1_000_000);
        if (!hasMemberNameAnchor &&
            (!InputParsing.TryGetRequiredInt(input, "line", errors, out line, minValue: 1, maxValue: 1_000_000) ||
             !InputParsing.TryGetRequiredInt(input, "column", errors, out column, minValue: 1, maxValue: 1_000_000)))
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
        string? focusText = GetOptionalTrimmedString(input, "focus_text");
        bool includeEditTargetTextDefault = includeSourceText && string.IsNullOrWhiteSpace(focusText);
        bool includeEditTargetText = InputParsing.GetOptionalBool(input, "include_edit_target_text", defaultValue: includeEditTargetTextDefault);
        int contextBefore = InputParsing.GetOptionalInt(input, "context_lines_before", defaultValue: 0, minValue: 0, maxValue: 500);
        int contextAfter = InputParsing.GetOptionalInt(input, "context_lines_after", defaultValue: 0, minValue: 0, maxValue: 500);
        int maxChars = InputParsing.GetOptionalInt(input, "max_chars", defaultValue: 8_000, minValue: 200, maxValue: 200_000);

        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath, workspaceHandle).ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, analysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

        if (!hasMemberNameAnchor && line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        SyntaxNode memberNode;
        TextSpan targetSpan;
        ISymbol? symbol;
        string memberName;

        if (hasMemberNameAnchor)
        {
            CommandExecutionResult? resolved = TryResolveMemberByName(
                analysis,
                requestedMemberName!,
                mode,
                includeTrivia,
                cancellationToken,
                out memberNode!,
                out targetSpan,
                out symbol,
                out memberName);

            if (resolved is not null)
            {
                return resolved;
            }

            LinePosition anchorPosition = analysis.SourceText.Lines.GetLinePosition(memberNode.SpanStart);
            line = anchorPosition.Line + 1;
            column = anchorPosition.Character + 1;
        }
        else if (string.Equals(analysis.Language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
            SyntaxNode? anchorNode = anchorToken.Parent;
            if (anchorNode is null)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_target", "No syntax node exists at the provided location.") });
            }

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
            SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
            SyntaxNode? anchorNode = anchorToken.Parent;
            if (anchorNode is null)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_target", "No syntax node exists at the provided location.") });
            }

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
        int targetStartColumn = targetLineSpan.Start.Character + 1;
        int targetEndLine = Math.Max(targetStartLine, targetLineSpan.End.Line + 1);
        int targetEndColumn = targetLineSpan.End.Character + 1;
        int snippetStartLine = Math.Max(1, targetStartLine - contextBefore);
        int snippetEndLine = Math.Min(analysis.SourceText.Lines.Count, targetEndLine + contextAfter);
        int fullSnippetStartLine = snippetStartLine;
        int fullSnippetEndLine = snippetEndLine;
        int fullSnippetLineCount = Math.Max(0, fullSnippetEndLine - fullSnippetStartLine + 1);
        bool focusRequested = !string.IsNullOrWhiteSpace(focusText);
        bool focusMatched = false;
        bool missingFocusLargeMemberGuardApplied = false;
        object? focus = null;
        if (focusRequested &&
            TryResolveFocusWindow(
                analysis.SourceText,
                targetSpan,
                focusText!,
                contextBefore,
                contextAfter,
                out int focusLine,
                out int focusColumn,
                out int focusStartLine,
                out int focusEndLine))
        {
            focusMatched = true;
            snippetStartLine = focusStartLine;
            snippetEndLine = focusEndLine;
            focus = new
            {
                text = focusText,
                matched = true,
                line = focusLine,
                column = focusColumn,
                window_start_line = focusStartLine,
                window_end_line = focusEndLine,
            };
        }
        else if (focusRequested)
        {
            if (fullSnippetLineCount > LargeMissingFocusMemberLineThreshold)
            {
                missingFocusLargeMemberGuardApplied = true;
                snippetEndLine = Math.Min(analysis.SourceText.Lines.Count, snippetStartLine + MissingFocusFallbackLineWindow - 1);
            }

            focus = new
            {
                text = focusText,
                matched = false,
                line = (int?)null,
                column = (int?)null,
                window_start_line = snippetStartLine,
                window_end_line = snippetEndLine,
                full_window_start_line = fullSnippetStartLine,
                full_window_end_line = fullSnippetEndLine,
                guard_applied = missingFocusLargeMemberGuardApplied,
            };
        }

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
                include_edit_target_text = includeEditTargetText,
                include_line_numbers = includeLineNumbers,
                include_trivia = includeTrivia,
                focus_text = focusText,
                member_name = requestedMemberName,
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
            ["edit_target"] = BuildEditTarget(
                analysis.SourceText,
                analysis.FilePath,
                modeRaw,
                targetSpan,
                targetStartLine,
                targetStartColumn,
                targetEndLine,
                targetEndColumn,
                includeTrivia,
                includeEditTargetText,
                maxChars,
                focusText),
            ["source"] = includeSourceText
                ? new
                {
                    text = source,
                    truncated,
                    character_count = sourceCharacterCount,
                    focus,
                }
                : new
                {
                    omitted = true,
                    truncated = false,
                    character_count = sourceCharacterCount,
                    focus,
                },
            ["payload_guidance"] = BuildPayloadGuidance(
                focusRequested,
                focusMatched,
                missingFocusLargeMemberGuardApplied,
                focusText,
                analysis.FilePath,
                memberName),
            ["edit_workflow"] = BuildEditWorkflow(analysis.FilePath, memberName, mode, targetStartLine, targetEndLine),
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static object? BuildPayloadGuidance(
        bool focusRequested,
        bool focusMatched,
        bool missingFocusLargeMemberGuardApplied,
        string? focusText,
        string filePath,
        string memberName)
    {
        if (!focusRequested || focusMatched)
        {
            return null;
        }

        string nextStep = $"roscli ctx.search_text {QuoteForSuggestion(focusText ?? string.Empty)} --file-path {QuoteForSuggestion(filePath)} --max-results 20 --context-lines 0";
        return new
        {
            focus_not_found = true,
            guard_applied = missingFocusLargeMemberGuardApplied,
            message = missingFocusLargeMemberGuardApplied
                ? "focus_text was not found in a large member, so source.text was capped. Use search_text or a different focus_text before requesting the full member."
                : "focus_text was not found. Verify the term with search_text or rerun with a different focus_text.",
            next_steps = new[]
            {
                nextStep,
                $"roscli ctx.file_outline {QuoteForSuggestion(filePath)} --member-name-contains {QuoteForSuggestion(memberName)} --max-members 20",
            },
        };
    }

    private static object BuildEditWorkflow(string filePath, string memberName, SourceMode mode, int targetStartLine, int targetEndLine)
    {
        string targetDescription = mode == SourceMode.Body ? "member body" : "member declaration";
        return new
        {
            multi_agent_rule = "Before mutating this file/member, reserve it with edit.claim; release the claim after validation.",
            same_member_multi_edit_rule = "If more than one change targets this same member, combine the changes into one edit.replace_in_member old/new block or one edit.batch_exact replace_span operation; do not run parallel edit commands against stale member reads.",
            claim_example = $"edit.claim claim {filePath} --reason edit-{memberName}",
            target = new
            {
                description = targetDescription,
                start_line = targetStartLine,
                end_line = targetEndLine,
            },
            preferred_mutation_commands = new[]
            {
                new
                {
                    command = "edit.batch_exact",
                    when = "Use kind=replace_span with edit_target.span_start/span_length when replacing this whole target or several claimed targets atomically. Start new_text exactly at the span; do not duplicate edit_target.trivia.preserved_line_prefix_text.",
                    next_step = "describe-command edit.batch_exact",
                },
                new
                {
                    command = "edit.replace_in_member",
                    when = "Use for small exact snippet replacement scoped to this unique member/body after edit.claim; avoids file-wide ambiguity and tolerates line-ending-only snippet drift. Add preview_chars when auditing long old/new assertion blocks.",
                    next_step = "describe-command edit.replace_in_member",
                },
                new
                {
                    command = "edit.replace_text",
                    when = "Use for small exact snippet replacement only when a file-wide exact match is intended or already known unique.",
                    next_step = "describe-command edit.replace_text",
                },
                new
                {
                    command = "edit.transaction",
                    when = "Use for coordinated multi-span or multi-file edits after all affected files are claimed.",
                    next_step = "describe-command edit.transaction",
                },
            },
            fallback_rule = "If a .cs mutation cannot use a Roslyn edit command, record the attempted command and reason in ROSLYN_FALLBACK_REFLECTION_LOG.md.",
        };
    }

    private static bool TryResolveFocusWindow(
        SourceText sourceText,
        TextSpan targetSpan,
        string focusText,
        int contextBefore,
        int contextAfter,
        out int focusLine,
        out int focusColumn,
        out int snippetStartLine,
        out int snippetEndLine)
    {
        string targetText = sourceText.ToString(targetSpan);
        int index = targetText.IndexOf(focusText, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            focusLine = 0;
            focusColumn = 0;
            snippetStartLine = 0;
            snippetEndLine = 0;
            return false;
        }

        LinePosition position = sourceText.Lines.GetLinePosition(targetSpan.Start + index);
        focusLine = position.Line + 1;
        focusColumn = position.Character + 1;
        snippetStartLine = Math.Max(1, focusLine - contextBefore);
        snippetEndLine = Math.Min(sourceText.Lines.Count, focusLine + contextAfter);
        return true;
    }

    private static string QuoteForSuggestion(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string? GetOptionalTrimmedString(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = property.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static CommandExecutionResult? TryResolveMemberByName(
        CommandFileAnalysis analysis,
        string requestedMemberName,
        SourceMode mode,
        bool includeTrivia,
        CancellationToken cancellationToken,
        out SyntaxNode memberNode,
        out TextSpan targetSpan,
        out ISymbol? symbol,
        out string memberName)
    {
        if (string.Equals(analysis.Language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            List<(SyntaxNode node, string name, ISymbol? symbol)> matches = analysis.Root
                .DescendantNodes()
                .Where(IsVbMemberCandidate)
                .Select(node =>
                {
                    ISymbol? candidateSymbol = GetVbMemberSymbol(node, analysis.SemanticModel, cancellationToken);
                    return (node, name: GetVbMemberName(node, candidateSymbol), symbol: candidateSymbol);
                })
                .Where(candidate => string.Equals(candidate.name, requestedMemberName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return CompleteMemberNameResolution(
                analysis,
                requestedMemberName,
                matches,
                node => ResolveVbTargetSpan(node, mode, includeTrivia),
                out memberNode,
                out targetSpan,
                out symbol,
                out memberName);
        }

        List<(CSharpSyntax.MemberDeclarationSyntax node, string name, ISymbol? symbol)> csharpMatches = analysis.Root
            .DescendantNodes()
            .OfType<CSharpSyntax.MemberDeclarationSyntax>()
            .Where(member => member is not CSharpSyntax.BaseNamespaceDeclarationSyntax)
            .Select(member => (node: member, name: GetCSharpMemberName(member), symbol: analysis.SemanticModel.GetDeclaredSymbol(member, cancellationToken)))
            .Where(candidate => string.Equals(candidate.name, requestedMemberName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return CompleteMemberNameResolution(
            analysis,
            requestedMemberName,
            csharpMatches,
            node =>
            {
                SyntaxNode targetNode = ResolveCSharpTargetNode(node, mode);
                return includeTrivia ? targetNode.FullSpan : targetNode.Span;
            },
            out memberNode,
            out targetSpan,
            out symbol,
            out memberName);
    }

    private static CommandExecutionResult? CompleteMemberNameResolution<TNode>(
        CommandFileAnalysis analysis,
        string requestedMemberName,
        IReadOnlyList<(TNode node, string name, ISymbol? symbol)> matches,
        Func<TNode, TextSpan> resolveTargetSpan,
        out SyntaxNode memberNode,
        out TextSpan targetSpan,
        out ISymbol? symbol,
        out string memberName)
        where TNode : SyntaxNode
    {
        if (matches.Count == 0)
        {
            memberNode = null!;
            targetSpan = default;
            symbol = null;
            memberName = string.Empty;
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("member_not_found", $"No member named '{requestedMemberName}' was found in the file.") });
        }

        if (matches.Count > 1)
        {
            memberNode = null!;
            targetSpan = default;
            symbol = null;
            memberName = string.Empty;
            string locations = string.Join(", ", matches.Take(10).Select(match =>
            {
                LinePosition position = analysis.SourceText.Lines.GetLinePosition(match.node.SpanStart);
                return $"{match.name}@{position.Line + 1}:{position.Character + 1}";
            }));
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("member_name_ambiguous", $"Member name '{requestedMemberName}' matched {matches.Count} members. Use line/column instead. Matches: {locations}") });
        }

        (TNode node, string name, ISymbol? candidateSymbol) = matches[0];
        memberNode = node;
        targetSpan = resolveTargetSpan(node);
        symbol = candidateSymbol;
        memberName = name;
        return null;
    }

    private static bool IsVbMemberCandidate(SyntaxNode node)
        => node is VbSyntax.MethodBlockBaseSyntax or
            VbSyntax.MethodStatementSyntax or
            VbSyntax.PropertyBlockSyntax or
            VbSyntax.PropertyStatementSyntax or
            VbSyntax.FieldDeclarationSyntax or
            VbSyntax.EventBlockSyntax or
            VbSyntax.EventStatementSyntax or
            VbSyntax.EnumMemberDeclarationSyntax or
            VbSyntax.DelegateStatementSyntax or
            VbSyntax.ClassBlockSyntax or
            VbSyntax.StructureBlockSyntax or
            VbSyntax.InterfaceBlockSyntax or
            VbSyntax.ModuleBlockSyntax or
            VbSyntax.EnumBlockSyntax;

    private static object BuildEditTarget(
        SourceText sourceText,
        string filePath,
        string modeRaw,
        TextSpan targetSpan,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        bool includeTrivia,
        bool includeEditTargetText,
        int maxChars,
        string? focusText)
    {
        TextLine line = sourceText.Lines[startLine - 1];
        string preservedLinePrefix = sourceText.ToString(TextSpan.FromBounds(line.Start, targetSpan.Start));
        string exactTargetText = includeEditTargetText
            ? sourceText.ToString(targetSpan)
            : string.Empty;
        int exactTargetCharacterCount = exactTargetText.Length;
        bool exactTextTruncated = exactTargetText.Length > maxChars;
        if (exactTextTruncated)
        {
            exactTargetText = exactTargetText[..maxChars];
        }
        string exactTextGuidance = BuildExactSpanTextGuidance(includeEditTargetText, exactTextTruncated, focusText);
        string? expectedTextOmittedReason = includeEditTargetText && exactTextTruncated
            ? "expected_text is omitted because exact_span_text is truncated; do not run replace_span against this whole target without an untruncated expected_text guard."
            : null;
        object replaceSpanOperation = includeEditTargetText && !exactTextTruncated
            ? new
            {
                kind = "replace_span",
                file_path = filePath,
                span_start = targetSpan.Start,
                span_length = targetSpan.Length,
                expected_text = exactTargetText,
                new_text = "<replacement text beginning exactly at span_start>",
            }
            : new
            {
                kind = "replace_span",
                file_path = filePath,
                span_start = targetSpan.Start,
                span_length = targetSpan.Length,
                expected_text_omitted_reason = expectedTextOmittedReason,
                new_text = "<replacement text beginning exactly at span_start>",
            };

        return new
        {
            file_path = filePath,
            mode = modeRaw,
            span_start = targetSpan.Start,
            span_length = targetSpan.Length,
            span_end = targetSpan.End,
            start_line = startLine,
            start_column = startColumn,
            end_line = endLine,
            end_column = endColumn,
            trivia = new
            {
                include_trivia = includeTrivia,
                span_preserves_existing_line_prefix = !includeTrivia && preservedLinePrefix.Length > 0,
                preserved_line_prefix_text = preservedLinePrefix,
                preserved_line_prefix_char_count = preservedLinePrefix.Length,
                new_text_first_line_rule = !includeTrivia && preservedLinePrefix.Length > 0
                    ? "Do not include preserved_line_prefix_text at the start of new_text; the file keeps that prefix before span_start."
                    : "Start new_text exactly at span_start.",
                prefix_edit_rule = !includeTrivia && preservedLinePrefix.Length > 0
                    ? "If the existing line prefix itself is wrong, such as duplicated indentation before a member or attribute, rerun ctx.member_source with include_trivia=true so replace_span can cover and repair the prefix."
                    : "This span includes leading trivia, so replace_span can repair indentation or attributes before the declaration.",
            },
            exact_span_text = includeEditTargetText
                ? (object)new
                {
                    text = exactTargetText,
                    truncated = exactTextTruncated,
                    character_count = exactTargetCharacterCount,
                    use_as_replacement_base = exactTextGuidance,
                }
                : new
                {
                    omitted = true,
                    truncated = false,
                    character_count = 0,
                    use_as_replacement_base = exactTextGuidance,
                },
            replace_span_operation = replaceSpanOperation,
        };
    }

    private static string BuildExactSpanTextGuidance(bool includeEditTargetText, bool exactTextTruncated, string? focusText)
    {
        bool hasFocusText = !string.IsNullOrWhiteSpace(focusText);
        if (includeEditTargetText && exactTextTruncated && hasFocusText)
        {
            return "Do not use this truncated exact_span_text as a replace_span replacement base. For a small focused change, use source.text as the exact old_text for edit.replace_text or another exact small edit. For whole-target replace_span, rerun without focus_text and with max_chars high enough that exact_span_text.truncated=false.";
        }

        if (includeEditTargetText && exactTextTruncated)
        {
            return "Do not use this truncated exact_span_text as a replace_span replacement base. Rerun with max_chars high enough that exact_span_text.truncated=false before constructing a whole-target replace_span new_text.";
        }

        if (includeEditTargetText)
        {
            return "Edit this exact_span_text when constructing replace_span new_text; it matches span_start/span_length and avoids double indentation.";
        }

        if (hasFocusText)
        {
            return "For a small focused change, use source.text as the exact old_text for edit.replace_text or another exact small edit. Request include_edit_target_text=true only when constructing a whole-target replace_span.";
        }

        return "Re-run ctx.member_source with include_edit_target_text=true when constructing a whole-target replace_span new_text.";
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
