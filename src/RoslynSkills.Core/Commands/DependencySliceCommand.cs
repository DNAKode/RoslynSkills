using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class DependencySliceCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.dependency_slice",
        Summary: "Return semantic dependencies referenced from the containing member.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: [CommandTrait.Heuristic, CommandTrait.DerivedAnalysis]);

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

        int maxDependencies = InputParsing.GetOptionalInt(input, "max_dependencies", defaultValue: 200, minValue: 1, maxValue: 5_000);
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
        MemberDeclarationSyntax? member = anchorToken.Parent?
            .AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax);
        if (member is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "Could not identify a containing member at the provided location.") });
        }

        ISymbol? memberSymbol = analysis.SemanticModel.GetDeclaredSymbol(member, cancellationToken);
        Dictionary<string, DependencyInfo> dependencies = new(StringComparer.Ordinal);

        foreach (SyntaxToken token in member.DescendantTokens(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!token.IsKind(SyntaxKind.IdentifierToken))
            {
                continue;
            }

            ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, analysis.SemanticModel, cancellationToken);
            if (symbol is null)
            {
                continue;
            }

            if (memberSymbol is not null &&
                (SymbolEqualityComparer.Default.Equals(symbol, memberSymbol) ||
                 SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, memberSymbol.OriginalDefinition)))
            {
                continue;
            }

            string key = CommandTextFormatting.GetStableSymbolId(symbol) ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (dependencies.ContainsKey(key))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = token.GetLocation().GetLineSpan();
            dependencies[key] = new DependencyInfo(
                symbol_id: key,
                symbol_display: symbol.ToDisplayString(),
                symbol_kind: symbol.Kind.ToString(),
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                containing_symbol: symbol.ContainingSymbol?.ToDisplayString());

            if (dependencies.Count >= maxDependencies)
            {
                break;
            }
        }

        FileLinePositionSpan memberLineSpan = member.GetLocation().GetLineSpan();
        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                max_dependencies = maxDependencies,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            containing_member = new
            {
                kind = member.Kind().ToString(),
                name = (memberSymbol?.Name) ?? "<anonymous>",
                line_start = memberLineSpan.StartLinePosition.Line + 1,
                line_end = memberLineSpan.EndLinePosition.Line + 1,
            },
            total_dependencies = dependencies.Count,
            dependencies = dependencies.Values
                .OrderBy(d => d.symbol_display, StringComparer.Ordinal)
                .ToArray(),
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private sealed record DependencyInfo(
        string symbol_id,
        string symbol_display,
        string symbol_kind,
        int line,
        int column,
        string? containing_symbol);
}

