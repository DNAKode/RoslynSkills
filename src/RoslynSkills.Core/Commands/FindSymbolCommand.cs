using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class FindSymbolCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_symbol",
        Summary: "Find identifier occurrences in a C#/VB file with structured hierarchical context.",
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

        InputParsing.TryGetRequiredString(input, "symbol_name", errors, out _);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "declarations_only", errors);
        InputParsing.ValidateOptionalBool(input, "first_declaration", errors);
        InputParsing.ValidateOptionalBool(input, "snippet_single_line", errors);
        InputParsing.ValidateOptionalInt(input, "max_snippet_chars", errors, minValue: 0, maxValue: 8000);

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
            !InputParsing.TryGetRequiredString(input, "symbol_name", errors, out string symbolName))
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

        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 50, minValue: 1, maxValue: 1_000);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: false);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);
        bool declarationsOnly = InputParsing.GetOptionalBool(input, "declarations_only", defaultValue: false);
        bool firstDeclaration = InputParsing.GetOptionalBool(input, "first_declaration", defaultValue: false);
        bool snippetSingleLine = InputParsing.GetOptionalBool(input, "snippet_single_line", defaultValue: false);
        int maxSnippetChars = InputParsing.GetOptionalInt(input, "max_snippet_chars", defaultValue: 0, minValue: 0, maxValue: 8000);
        int contextLines = InputParsing.GetOptionalInt(
            input,
            "context_lines",
            defaultValue: brief ? 0 : 2,
            minValue: 0,
            maxValue: 20);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(
            filePath,
            cancellationToken,
            workspacePath).ConfigureAwait(false);

        if (requireWorkspace &&
            !string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            string fallbackReason = string.IsNullOrWhiteSpace(analysis.WorkspaceContext.fallback_reason)
                ? "Workspace context could not be resolved."
                : analysis.WorkspaceContext.fallback_reason;
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError(
                        "workspace_required",
                        $"Command '{Descriptor.Id}' requires workspace context for '{analysis.FilePath}', but mode was '{analysis.WorkspaceContext.mode}'. {fallbackReason} Pass workspace_path (.csproj/.vbproj/.sln/.slnx or containing directory) and retry."),
                });
        }

        IEnumerable<SyntaxToken> matches = analysis.Root
            .DescendantTokens(descendIntoTrivia: false)
            .Where(t => CommandLanguageServices.IsIdentifierToken(t, analysis.Language) &&
                        string.Equals(t.ValueText, symbolName, StringComparison.Ordinal));

        List<SymbolMatch> resultMatches = ResolveMatches(
            matches,
            analysis.SourceText,
            contextLines,
            analysis.SemanticModel,
            maxResults,
            declarationsOnly,
            firstDeclaration,
            snippetSingleLine,
            maxSnippetChars,
            cancellationToken);

        object matchesPayload = brief
            ? resultMatches.Select(m => new
            {
                m.text,
                m.syntax_kind,
                m.is_declaration,
                m.line,
                m.column,
                symbol_kind = m.semantic.symbol_kind,
                symbol_display = m.semantic.symbol_display,
                symbol_id = m.semantic.symbol_id,
                is_resolved = m.semantic.is_resolved,
            }).ToArray()
            : resultMatches;

        object data = new
        {
            query = new
            {
                file_path = analysis.FilePath,
                symbol_name = symbolName,
                max_results = maxResults,
                context_lines = contextLines,
                brief,
                declarations_only = declarationsOnly,
                first_declaration = firstDeclaration,
                snippet_single_line = snippetSingleLine,
                max_snippet_chars = maxSnippetChars,
                semantic_enrichment = true,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            total_matches = resultMatches.Count,
            matches = matchesPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static List<SymbolMatch> ResolveMatches(
        IEnumerable<SyntaxToken> matches,
        SourceText sourceText,
        int contextLines,
        SemanticModel semanticModel,
        int maxResults,
        bool declarationsOnly,
        bool firstDeclaration,
        bool snippetSingleLine,
        int maxSnippetChars,
        CancellationToken cancellationToken)
    {
        if (firstDeclaration)
        {
            SymbolMatch? firstAny = null;
            SymbolMatch? firstDecl = null;
            foreach (SyntaxToken match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SymbolMatch current = CreateMatch(
                    match,
                    sourceText,
                    contextLines,
                    snippetSingleLine,
                    maxSnippetChars,
                    semanticModel,
                    cancellationToken);
                firstAny ??= current;
                if (current.is_declaration)
                {
                    firstDecl = current;
                    break;
                }
            }

            if (declarationsOnly)
            {
                return firstDecl is null ? new List<SymbolMatch>() : new List<SymbolMatch> { firstDecl };
            }

            SymbolMatch? selected = firstDecl ?? firstAny;
            return selected is null ? new List<SymbolMatch>() : new List<SymbolMatch> { selected };
        }

        List<SymbolMatch> filtered = new();
        foreach (SyntaxToken match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SymbolMatch current = CreateMatch(
                match,
                sourceText,
                contextLines,
                snippetSingleLine,
                maxSnippetChars,
                semanticModel,
                cancellationToken);
            if (declarationsOnly && !current.is_declaration)
            {
                continue;
            }

            filtered.Add(current);
            if (filtered.Count >= maxResults)
            {
                break;
            }
        }

        return filtered;
    }

    private static SymbolMatch CreateMatch(
        SyntaxToken token,
        SourceText sourceText,
        int contextLines,
        bool snippetSingleLine,
        int maxSnippetChars,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        LinePositionSpan linePositionSpan = sourceText.Lines.GetLinePositionSpan(token.Span);
        int line = linePositionSpan.Start.Line + 1;
        int column = linePositionSpan.Start.Character + 1;

        ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, semanticModel, cancellationToken);
        string? namespaceName = symbol?.ContainingNamespace is { IsGlobalNamespace: false } namespaceSymbol
            ? namespaceSymbol.ToDisplayString()
            : null;
        string[] containingTypes = CommandTextFormatting.GetContainingTypes(token, semanticModel, cancellationToken);
        string? containingMember = symbol?.ContainingSymbol?.Kind.ToString();

        int startLine = Math.Max(line - contextLines, 1);
        int endLine = Math.Min(line + contextLines, sourceText.Lines.Count);
        string snippet = BuildSnippet(sourceText, startLine, endLine, snippetSingleLine, maxSnippetChars);
        SymbolSemanticInfo semantic = CreateSemanticInfo(symbol);

        return new SymbolMatch(
            text: token.ValueText,
            syntax_kind: CommandLanguageServices.GetSyntaxKindName(token.Parent),
            is_declaration: CommandTextFormatting.IsDeclarationToken(token, semanticModel, cancellationToken),
            line: line,
            column: column,
            hierarchy: new SymbolHierarchy(
                namespace_name: namespaceName,
                containing_types: containingTypes,
                containing_member_kind: containingMember),
            context: new ContextWindow(startLine, endLine, snippet),
            semantic: semantic);
    }

    private static string BuildSnippet(SourceText sourceText, int startLine, int endLine, bool singleLine, int maxSnippetChars)
    {
        List<string> lines = new();
        for (int line = startLine; line <= endLine; line++)
        {
            string lineText = sourceText.Lines[line - 1].ToString();
            lines.Add($"{line,4}: {lineText}");
        }

        string snippet = singleLine
            ? string.Join(" | ", lines)
            : string.Join(Environment.NewLine, lines);
        if (maxSnippetChars > 0 && snippet.Length > maxSnippetChars)
        {
            if (maxSnippetChars <= 3)
            {
                return snippet[..maxSnippetChars];
            }

            int safeLength = maxSnippetChars - 3;
            return snippet[..safeLength] + "...";
        }

        return snippet;
    }

    private static SymbolSemanticInfo CreateSemanticInfo(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return new SymbolSemanticInfo(
                is_resolved: false,
                symbol_kind: null,
                symbol_display: null,
                symbol_id: null,
                containing_symbol: null);
        }

        string? symbolId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            symbolId = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return new SymbolSemanticInfo(
            is_resolved: true,
            symbol_kind: symbol.Kind.ToString(),
            symbol_display: symbol.ToDisplayString(),
            symbol_id: symbolId,
            containing_symbol: symbol.ContainingSymbol?.ToDisplayString());
    }

    private sealed record SymbolMatch(
        string text,
        string syntax_kind,
        bool is_declaration,
        int line,
        int column,
        SymbolHierarchy hierarchy,
        ContextWindow context,
        SymbolSemanticInfo semantic);

    private sealed record SymbolHierarchy(
        string? namespace_name,
        string[] containing_types,
        string? containing_member_kind);

    private sealed record ContextWindow(
        int start_line,
        int end_line,
        string snippet);

    private sealed record SymbolSemanticInfo(
        bool is_resolved,
        string? symbol_kind,
        string? symbol_display,
        string? symbol_id,
        string? containing_symbol);
}

