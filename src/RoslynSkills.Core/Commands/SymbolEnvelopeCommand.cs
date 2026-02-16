using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class SymbolEnvelopeCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.symbol_envelope",
        Summary: "Return a structured semantic context envelope for a symbol at line/column.",
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

        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 3, minValue: 0, maxValue: 30);

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

        SyntaxToken token = analysis.FindAnchorToken(line, column);
        ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, analysis.SemanticModel, cancellationToken);
        if (symbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", "Unable to resolve a semantic symbol at the provided location.") });
        }

        int resolvedLine = analysis.SourceText.Lines.GetLinePositionSpan(token.Span).Start.Line + 1;
        int referenceCountHint = CountSymbolReferences(analysis, symbol, cancellationToken);
        int implementationCountHint = CountImplementations(analysis, symbol, cancellationToken);

        string? namespaceName = token.Parent?
            .AncestorsAndSelf()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?
            .Name
            .ToString();
        string[] containingTypes = CommandTextFormatting.GetContainingTypes(token, analysis.SemanticModel, cancellationToken);
        string? containingMember = token.Parent?
            .Ancestors()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax)?
            .Kind()
            .ToString();

        object data = new
        {
            query = new
            {
                file_path = analysis.FilePath,
                line,
                column,
                context_lines = contextLines,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            symbol_id = CommandTextFormatting.GetStableSymbolId(symbol),
            display_name = symbol.ToDisplayString(),
            kind = symbol.Kind.ToString(),
            qualified_name = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            assembly = symbol.ContainingAssembly?.Name,
            project = analysis.WorkspaceContext.project_path,
            document_path = analysis.FilePath,
            span = new
            {
                start = token.SpanStart,
                length = token.Span.Length,
            },
            hierarchy = new
            {
                @namespace = namespaceName,
                containing_types = containingTypes,
                containing_member = containingMember,
            },
            local_context = new
            {
                line_start = Math.Max(1, resolvedLine - contextLines),
                line_end = Math.Min(analysis.SourceText.Lines.Count, resolvedLine + contextLines),
                snippet = CommandTextFormatting.BuildSnippet(analysis.SourceText, resolvedLine, contextLines),
            },
            relations = new
            {
                declaration = CommandTextFormatting.IsDeclarationToken(token, analysis.SemanticModel, cancellationToken),
                reference_count_hint = referenceCountHint,
                implementation_count_hint = implementationCountHint,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static int CountSymbolReferences(CommandFileAnalysis analysis, ISymbol targetSymbol, CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (SyntaxToken token in analysis.Root.DescendantTokens(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CommandLanguageServices.IsIdentifierToken(token, analysis.Language))
            {
                continue;
            }

            ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, analysis.SemanticModel, cancellationToken);
            if (symbol is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(symbol, targetSymbol) ||
                SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, targetSymbol.OriginalDefinition))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountImplementations(CommandFileAnalysis analysis, ISymbol symbol, CancellationToken cancellationToken)
    {
        int count = 0;
        if (symbol is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Interface)
        {
            foreach (BaseTypeDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                INamedTypeSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                if (candidate is null)
                {
                    continue;
                }

                if (candidate.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, namedType)))
                {
                    count++;
                }
            }
        }
        else if (symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType.TypeKind == TypeKind.Interface)
        {
            foreach (MethodDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMethodSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                if (candidate is null)
                {
                    continue;
                }

                ISymbol? mapped = candidate.ContainingType.FindImplementationForInterfaceMember(methodSymbol);
                if (SymbolEqualityComparer.Default.Equals(mapped, candidate))
                {
                    count++;
                }
            }
        }

        return count;
    }
}

