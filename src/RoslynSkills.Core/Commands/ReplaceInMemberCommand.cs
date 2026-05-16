using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace RoslynSkills.Core.Commands;

public sealed class ReplaceInMemberCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.replace_in_member",
        Summary: "Replace exact text scoped to one member anchored by member name or line/column.",
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

        string? memberName = GetOptionalString(input, "member_name");
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

        InputParsing.TryGetRequiredString(input, "old_text", errors, out string oldText);
        InputParsing.TryGetRequiredString(input, "new_text", errors, out _);
        if (oldText.Length == 0)
        {
            errors.Add(new CommandError("invalid_input", "Property 'old_text' must not be empty."));
        }

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"File '{Path.GetFullPath(filePath)}' does not exist."));
        }

        if (input.TryGetProperty("mode", out JsonElement mode) &&
            mode.ValueKind == JsonValueKind.String &&
            !IsValidMode(mode.GetString()))
        {
            errors.Add(new CommandError("invalid_input", "Property 'mode' must be 'member' or 'body'."));
        }

        InputParsing.ValidateOptionalBool(input, "replace_all", errors);
        InputParsing.ValidateOptionalBool(input, "apply", errors);
        InputParsing.ValidateOptionalBool(input, "include_diagnostics", errors);
        InputParsing.ValidateOptionalInt(input, "max_diagnostics", errors, minValue: 1, maxValue: 2_000);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
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
        string? requestedMemberName = GetOptionalString(input, "member_name");
        int line = InputParsing.GetOptionalInt(input, "line", defaultValue: 0, minValue: 0, maxValue: 1_000_000);
        int column = InputParsing.GetOptionalInt(input, "column", defaultValue: 0, minValue: 0, maxValue: 1_000_000);
        string mode = GetOptionalString(input, "mode") ?? "member";
        string oldText = input.GetProperty("old_text").GetString()!;
        string newText = input.GetProperty("new_text").GetString() ?? string.Empty;
        bool replaceAll = InputParsing.GetOptionalBool(input, "replace_all", defaultValue: false);
        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        bool includeDiagnostics = InputParsing.GetOptionalBool(input, "include_diagnostics", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 2_000);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath, workspaceHandle).ConfigureAwait(false);
        CommandExecutionResult? resolved = ResolveTarget(
            analysis,
            requestedMemberName,
            line,
            column,
            mode,
            cancellationToken,
            out SyntaxNode memberNode,
            out TextSpan targetSpan,
            out string memberName);
        if (resolved is not null)
        {
            return resolved;
        }

        string originalContent = analysis.SourceText.ToString();
        string targetText = originalContent.Substring(targetSpan.Start, targetSpan.Length);
        (int MatchCount, string EffectiveOldText, string MatchMode) match = ResolveOldText(targetText, oldText);
        MatchLocation[] matchLocations = GetMatchLocations(analysis.SourceText, targetSpan.Start, targetText, match.EffectiveOldText);
        if (match.MatchCount == 0)
        {
            CommandError error = new("old_text_not_found", "The supplied old_text was not found in the selected member.");
            return new CommandExecutionResult(BuildFailureData(analysis, memberNode, targetSpan, memberName, mode, oldText, error), new[] { error });
        }

        if (!replaceAll && match.MatchCount > 1)
        {
            CommandError error = new("old_text_ambiguous", $"The supplied old_text matched {match.MatchCount} times inside member '{memberName}'. Make old_text more specific or set replace_all=true.");
            return new CommandExecutionResult(BuildFailureData(analysis, memberNode, targetSpan, memberName, mode, oldText, error, match.MatchCount), new[] { error });
        }

        string updatedTarget = replaceAll
            ? targetText.Replace(match.EffectiveOldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(targetText, match.EffectiveOldText, newText);
        string updatedContent = string.Concat(
            originalContent.AsSpan(0, targetSpan.Start),
            updatedTarget,
            originalContent.AsSpan(targetSpan.End));
        bool changed = !string.Equals(originalContent, updatedContent, StringComparison.Ordinal);
        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedContent, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(filePath, wroteFile, cancellationToken).ConfigureAwait(false);
        object diagnosticsData = await ExactEditDiagnostics.BuildAsync(
                filePath,
                updatedContent,
                includeDiagnostics,
                maxDiagnostics,
                workspacePath,
                workspaceHandle,
                cancellationToken)
            .ConfigureAwait(false);

        LinePositionSpan targetLineSpan = analysis.SourceText.Lines.GetLinePositionSpan(targetSpan);
        object data = new
        {
            file_path = filePath,
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            apply_changes = apply,
            replace_all = replaceAll,
            member = BuildMemberPayload(analysis, memberNode, memberName),
            target = new
            {
                mode,
                span_start = targetSpan.Start,
                span_length = targetSpan.Length,
                start_line = targetLineSpan.Start.Line + 1,
                end_line = Math.Max(targetLineSpan.Start.Line + 1, targetLineSpan.End.Line + 1),
            },
            match_count = match.MatchCount,
            match_scope = "member",
            match_mode = match.MatchMode,
            matches = matchLocations,
            changed,
            wrote_file = wroteFile,
            old_text_character_count = oldText.Length,
            effective_old_text_character_count = match.EffectiveOldText.Length,
            new_text_character_count = newText.Length,
            character_delta = updatedContent.Length - originalContent.Length,
            hot_workspace_refresh = hotWorkspaceRefresh,
            diagnostics_after_replace = diagnosticsData,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static CommandExecutionResult? ResolveTarget(
        CommandFileAnalysis analysis,
        string? requestedMemberName,
        int line,
        int column,
        string mode,
        CancellationToken cancellationToken,
        out SyntaxNode memberNode,
        out TextSpan targetSpan,
        out string memberName)
    {
        memberNode = null!;
        targetSpan = default;
        memberName = string.Empty;

        if (!string.IsNullOrWhiteSpace(requestedMemberName))
        {
            List<SyntaxNode> matches = FindMemberCandidates(analysis.Root, analysis.Language)
                .Where(node => string.Equals(GetMemberName(node, analysis.SemanticModel, cancellationToken), requestedMemberName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                return new CommandExecutionResult(null, new[] { new CommandError("member_not_found", $"No member named '{requestedMemberName}' was found in the file.") });
            }

            if (matches.Count > 1)
            {
                string locations = string.Join(", ", matches.Take(10).Select(node =>
                {
                    LinePosition position = analysis.SourceText.Lines.GetLinePosition(node.SpanStart);
                    return $"{GetMemberName(node, analysis.SemanticModel, cancellationToken)}@{position.Line + 1}:{position.Character + 1}";
                }));
                return new CommandExecutionResult(null, new[] { new CommandError("member_name_ambiguous", $"Member name '{requestedMemberName}' matched {matches.Count} members. Use line/column instead. Matches: {locations}") });
            }

            memberNode = matches[0];
        }
        else
        {
            if (line > analysis.SourceText.Lines.Count)
            {
                return new CommandExecutionResult(null, new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
            }

            SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
            memberNode = anchorToken.Parent?.AncestorsAndSelf().FirstOrDefault(node => IsMemberCandidate(node, analysis.Language))!;
            if (memberNode is null)
            {
                return new CommandExecutionResult(null, new[] { new CommandError("invalid_target", "The provided line/column does not resolve to a member declaration.") });
            }
        }

        memberName = GetMemberName(memberNode, analysis.SemanticModel, cancellationToken);
        targetSpan = ResolveTargetSpan(memberNode, mode);
        return null;
    }

    private static IEnumerable<SyntaxNode> FindMemberCandidates(SyntaxNode root, string language)
        => root.DescendantNodes().Where(node => IsMemberCandidate(node, language));

    private static bool IsMemberCandidate(SyntaxNode node, string language)
    {
        if (string.Equals(language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return node is VbSyntax.MethodBlockBaseSyntax or
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
        }

        return node is CSharpSyntax.MemberDeclarationSyntax and not CSharpSyntax.BaseNamespaceDeclarationSyntax;
    }

    private static TextSpan ResolveTargetSpan(SyntaxNode memberNode, string mode)
    {
        if (!string.Equals(mode, "body", StringComparison.OrdinalIgnoreCase))
        {
            return memberNode.Span;
        }

        return memberNode switch
        {
            CSharpSyntax.BaseMethodDeclarationSyntax method when method.Body is not null => method.Body.Span,
            CSharpSyntax.BaseMethodDeclarationSyntax method when method.ExpressionBody is not null => method.ExpressionBody.Expression.Span,
            CSharpSyntax.PropertyDeclarationSyntax property when property.AccessorList is not null => property.AccessorList.Span,
            CSharpSyntax.PropertyDeclarationSyntax property when property.ExpressionBody is not null => property.ExpressionBody.Expression.Span,
            CSharpSyntax.IndexerDeclarationSyntax indexer when indexer.AccessorList is not null => indexer.AccessorList.Span,
            CSharpSyntax.EventDeclarationSyntax eventDeclaration when eventDeclaration.AccessorList is not null => eventDeclaration.AccessorList.Span,
            VbSyntax.MethodBlockBaseSyntax methodBlock when methodBlock.Statements.Count > 0 => TextSpan.FromBounds(methodBlock.Statements[0].SpanStart, methodBlock.Statements[^1].Span.End),
            VbSyntax.PropertyBlockSyntax propertyBlock when propertyBlock.Accessors.Count > 0 => TextSpan.FromBounds(propertyBlock.Accessors[0].SpanStart, propertyBlock.Accessors[^1].Span.End),
            VbSyntax.EventBlockSyntax eventBlock when eventBlock.Accessors.Count > 0 => TextSpan.FromBounds(eventBlock.Accessors[0].SpanStart, eventBlock.Accessors[^1].Span.End),
            _ => memberNode.Span,
        };
    }

    private static string GetMemberName(SyntaxNode memberNode, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        ISymbol? symbol = semanticModel.GetDeclaredSymbol(memberNode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(symbol?.Name))
        {
            return symbol!.Name;
        }

        return memberNode switch
        {
            CSharpSyntax.MethodDeclarationSyntax method => method.Identifier.ValueText,
            CSharpSyntax.ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            CSharpSyntax.PropertyDeclarationSyntax property => property.Identifier.ValueText,
            CSharpSyntax.FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            CSharpSyntax.EventDeclarationSyntax @event => @event.Identifier.ValueText,
            CSharpSyntax.BaseTypeDeclarationSyntax typeDeclaration => typeDeclaration.Identifier.ValueText,
            VbSyntax.MethodBlockBaseSyntax methodBlock => GetVbMethodName(methodBlock.BlockStatement),
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier.ValueText,
            VbSyntax.PropertyBlockSyntax propertyBlock => propertyBlock.PropertyStatement.Identifier.ValueText,
            VbSyntax.PropertyStatementSyntax propertyStatement => propertyStatement.Identifier.ValueText,
            VbSyntax.FieldDeclarationSyntax field => string.Join(", ", field.Declarators.SelectMany(d => d.Names).Select(n => n.Identifier.ValueText)),
            VbSyntax.EventBlockSyntax eventBlock => eventBlock.EventStatement.Identifier.ValueText,
            VbSyntax.EventStatementSyntax eventStatement => eventStatement.Identifier.ValueText,
            VbSyntax.ClassBlockSyntax classBlock => classBlock.ClassStatement.Identifier.ValueText,
            VbSyntax.ModuleBlockSyntax moduleBlock => moduleBlock.ModuleStatement.Identifier.ValueText,
            _ => CommandLanguageServices.GetSyntaxKindName(memberNode),
        };
    }

    private static string GetVbMethodName(VbSyntax.MethodBaseSyntax method)
        => method switch
        {
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier.ValueText,
            VbSyntax.SubNewStatementSyntax => "New",
            VbSyntax.OperatorStatementSyntax => "Operator",
            _ => method.Kind().ToString(),
        };

    private static object BuildMemberPayload(CommandFileAnalysis analysis, SyntaxNode memberNode, string memberName)
    {
        LinePositionSpan span = analysis.SourceText.Lines.GetLinePositionSpan(memberNode.Span);
        return new
        {
            member_kind = CommandLanguageServices.GetSyntaxKindName(memberNode),
            member_name = memberName,
            declaration_start_line = span.Start.Line + 1,
            declaration_end_line = Math.Max(span.Start.Line + 1, span.End.Line + 1),
        };
    }

    private static object BuildFailureData(
        CommandFileAnalysis analysis,
        SyntaxNode memberNode,
        TextSpan targetSpan,
        string memberName,
        string mode,
        string oldText,
        CommandError error,
        int matchCount = 0)
    {
        LinePositionSpan targetLineSpan = analysis.SourceText.Lines.GetLinePositionSpan(targetSpan);
        return new
        {
            file_path = analysis.FilePath,
            member = BuildMemberPayload(analysis, memberNode, memberName),
            target = new
            {
                mode,
                span_start = targetSpan.Start,
                span_length = targetSpan.Length,
                start_line = targetLineSpan.Start.Line + 1,
                end_line = Math.Max(targetLineSpan.Start.Line + 1, targetLineSpan.End.Line + 1),
            },
            match_count = matchCount,
            match_scope = "member",
            old_text_character_count = oldText.Length,
            error,
            recovery_hint = new
            {
                preferred_next_step = "Re-read this member with ctx.member_source --member-name <name> --focus-text <nearby text>; then retry edit.replace_in_member or use edit.batch_exact replace_span from an untruncated edit_target.",
                multi_agent_rule = "Keep or reacquire an edit.claim for the file/member before retrying the write.",
            },
        };
    }

    private static (int MatchCount, string EffectiveOldText, string MatchMode) ResolveOldText(string targetText, string oldText)
    {
        int exactCount = CountOccurrences(targetText, oldText);
        if (exactCount > 0)
        {
            return (exactCount, oldText, "exact");
        }

        string normalized = NormalizeLineEndingsForTarget(oldText, targetText);
        if (!string.Equals(normalized, oldText, StringComparison.Ordinal))
        {
            int normalizedCount = CountOccurrences(targetText, normalized);
            if (normalizedCount > 0)
            {
                return (normalizedCount, normalized, "line_ending_normalized");
            }
        }

        return (0, oldText, "none");
    }

    private static MatchLocation[] GetMatchLocations(SourceText sourceText, int targetStart, string targetText, string oldText)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return Array.Empty<MatchLocation>();
        }

        List<MatchLocation> matches = new();
        int index = 0;
        while ((index = targetText.IndexOf(oldText, index, StringComparison.Ordinal)) >= 0)
        {
            LinePosition position = sourceText.Lines.GetLinePosition(targetStart + index);
            matches.Add(new MatchLocation(
                line: position.Line + 1,
                column: position.Character + 1,
                target_offset: index,
                length: oldText.Length,
                text_preview: BuildTextPreview(oldText)));
            index += oldText.Length;
        }

        return matches.ToArray();
    }

    private static string BuildTextPreview(string text)
    {
        string singleLine = text
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);
        const int maxLength = 96;
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..(maxLength - 3)] + "...";
    }

    private static string NormalizeLineEndingsForTarget(string value, string targetText)
    {
        string lineEnding = targetText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", lineEnding, StringComparison.Ordinal);
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

    private static string? GetOptionalString(JsonElement input, string propertyName)
        => input.TryGetProperty(propertyName, out JsonElement property) &&
           property.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static bool IsValidMode(string? mode)
        => string.Equals(mode, "member", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(mode, "body", StringComparison.OrdinalIgnoreCase);

    private sealed record MatchLocation(int line, int column, int target_offset, int length, string text_preview);
}
