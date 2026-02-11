using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class FindSymbolCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_symbol",
        Summary: "Find identifier occurrences in a C# file with structured hierarchical context.",
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

        IEnumerable<SyntaxToken> matches = analysis.Root
            .DescendantTokens(descendIntoTrivia: false)
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && string.Equals(t.ValueText, symbolName, StringComparison.Ordinal))
            .Take(maxResults);

        List<SymbolMatch> resultMatches = new();
        foreach (SyntaxToken match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resultMatches.Add(CreateMatch(match, analysis.SourceText, contextLines, analysis.SemanticModel, cancellationToken));
        }

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
                semantic_enrichment = true,
                workspace_context = BuildWorkspaceContextPayload(analysis.WorkspaceContext),
            },
            total_matches = resultMatches.Count,
            matches = matchesPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static SymbolMatch CreateMatch(
        SyntaxToken token,
        SourceText sourceText,
        int contextLines,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        LinePositionSpan linePositionSpan = sourceText.Lines.GetLinePositionSpan(token.Span);
        int line = linePositionSpan.Start.Line + 1;
        int column = linePositionSpan.Start.Character + 1;

        string? namespaceName = token.Parent?
            .AncestorsAndSelf()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?
            .Name
            .ToString();

        string[] containingTypes = token.Parent?
            .Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.ValueText)
            .Reverse()
            .ToArray() ?? Array.Empty<string>();

        string? containingMember = token.Parent?
            .Ancestors()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax)?
            .Kind()
            .ToString();

        int startLine = Math.Max(line - contextLines, 1);
        int endLine = Math.Min(line + contextLines, sourceText.Lines.Count);
        string snippet = BuildSnippet(sourceText, startLine, endLine);
        SymbolSemanticInfo semantic = CreateSemanticInfo(token, semanticModel, cancellationToken);

        return new SymbolMatch(
            text: token.ValueText,
            syntax_kind: token.Parent?.Kind().ToString() ?? "Unknown",
            is_declaration: IsDeclarationToken(token),
            line: line,
            column: column,
            hierarchy: new SymbolHierarchy(
                namespace_name: namespaceName,
                containing_types: containingTypes,
                containing_member_kind: containingMember),
            context: new ContextWindow(startLine, endLine, snippet),
            semantic: semantic);
    }

    private static bool IsDeclarationToken(SyntaxToken token)
        => token.Parent switch
        {
            ClassDeclarationSyntax classDecl => classDecl.Identifier == token,
            StructDeclarationSyntax structDecl => structDecl.Identifier == token,
            InterfaceDeclarationSyntax interfaceDecl => interfaceDecl.Identifier == token,
            EnumDeclarationSyntax enumDecl => enumDecl.Identifier == token,
            RecordDeclarationSyntax recordDecl => recordDecl.Identifier == token,
            MethodDeclarationSyntax methodDecl => methodDecl.Identifier == token,
            ConstructorDeclarationSyntax ctorDecl => ctorDecl.Identifier == token,
            PropertyDeclarationSyntax propertyDecl => propertyDecl.Identifier == token,
            VariableDeclaratorSyntax variableDecl => variableDecl.Identifier == token,
            ParameterSyntax parameter => parameter.Identifier == token,
            DelegateDeclarationSyntax delegateDecl => delegateDecl.Identifier == token,
            EventDeclarationSyntax eventDecl => eventDecl.Identifier == token,
            NamespaceDeclarationSyntax namespaceDecl => namespaceDecl.Name.ToString().EndsWith(token.ValueText, StringComparison.Ordinal),
            FileScopedNamespaceDeclarationSyntax fileNamespaceDecl => fileNamespaceDecl.Name.ToString().EndsWith(token.ValueText, StringComparison.Ordinal),
            _ => false,
        };

    private static string BuildSnippet(SourceText sourceText, int startLine, int endLine)
    {
        List<string> lines = new();
        for (int line = startLine; line <= endLine; line++)
        {
            string lineText = sourceText.Lines[line - 1].ToString();
            lines.Add($"{line,4}: {lineText}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static SymbolSemanticInfo CreateSemanticInfo(
        SyntaxToken token,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, semanticModel, cancellationToken);
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

    private static object BuildWorkspaceContextPayload(WorkspaceContextInfo context)
    {
        return new
        {
            mode = context.mode,
            resolution_source = context.resolution_source,
            requested_workspace_path = context.requested_workspace_path,
            resolved_workspace_path = context.resolved_workspace_path,
            project_path = context.project_path,
            fallback_reason = context.fallback_reason,
            attempted_workspace_paths = context.attempted_workspace_paths,
            workspace_diagnostics = context.workspace_diagnostics,
        };
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

