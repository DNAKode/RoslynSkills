using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

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
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 2, minValue: 0, maxValue: 20);

        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SyntaxToken> matches = root
            .DescendantTokens(descendIntoTrivia: false)
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && string.Equals(t.ValueText, symbolName, StringComparison.Ordinal))
            .Take(maxResults);

        SourceText sourceText = syntaxTree.GetText(cancellationToken);
        List<SymbolMatch> resultMatches = new();
        foreach (SyntaxToken match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resultMatches.Add(CreateMatch(match, sourceText, contextLines));
        }

        object data = new
        {
            query = new
            {
                file_path = filePath,
                symbol_name = symbolName,
                max_results = maxResults,
                context_lines = contextLines,
            },
            total_matches = resultMatches.Count,
            matches = resultMatches,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static SymbolMatch CreateMatch(SyntaxToken token, SourceText sourceText, int contextLines)
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
            context: new ContextWindow(startLine, endLine, snippet));
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

    private sealed record SymbolMatch(
        string text,
        string syntax_kind,
        bool is_declaration,
        int line,
        int column,
        SymbolHierarchy hierarchy,
        ContextWindow context);

    private sealed record SymbolHierarchy(
        string? namespace_name,
        string[] containing_types,
        string? containing_member_kind);

    private sealed record ContextWindow(
        int start_line,
        int end_line,
        string snippet);
}
