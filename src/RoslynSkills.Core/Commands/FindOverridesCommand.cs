using Microsoft.CodeAnalysis;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class FindOverridesCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_overrides",
        Summary: "Find overrides for a virtual/abstract member anchored by line/column or --symbol across the loaded solution.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        string? symbolQuery = SymbolQueryResolution.GetOptionalSymbolQuery(input);
        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            WorkspaceInput.TryGetRequiredWorkspaceTarget(input, errors, out _, out _);
            InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
            return errors;
        }

        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 200, minValue: 1, maxValue: 2_000);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        string? symbolQuery = SymbolQueryResolution.GetOptionalSymbolQuery(input);
        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            return await ExecuteBySymbolNameAsync(
                input,
                symbolQuery,
                workspacePath,
                workspaceHandle,
                requireWorkspace,
                maxResults,
                cancellationToken).ConfigureAwait(false);
        }

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

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath, workspaceHandle).ConfigureAwait(false);
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
        ISymbol? anchorSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        if (anchorSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", "Unable to resolve a semantic symbol at the provided location.") });
        }

        anchorSymbol = SolutionSearchScope.NormalizePositionalRecordProperty(anchorSymbol);

        string searchScope = SolutionSearchScope.CompilationKind;
        List<OverrideMatch> matches = new();
        Solution? solution = analysis.Document?.Project.Solution;

        if (solution is not null && anchorSymbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            // Overrides live in other files and projects; ask the whole loaded solution.
            IReadOnlyList<RelatedDeclaration> related = await SolutionSymbolRelations
                .FindOverridesAsync(solution, anchorSymbol, cancellationToken)
                .ConfigureAwait(false);
            matches.AddRange(related.Take(maxResults).Select(declaration => new OverrideMatch(
                declaration.Kind,
                declaration.Line,
                declaration.Column,
                declaration.SymbolDisplay,
                declaration.FilePath)));
            searchScope = SolutionSearchScope.SolutionKind;
        }
        else switch (anchorSymbol)
        {
            case IMethodSymbol methodSymbol:
                AddMethodOverrides(methodSymbol, analysis, matches, maxResults, cancellationToken);
                break;
            case IPropertySymbol propertySymbol:
                AddPropertyOverrides(propertySymbol, analysis, matches, maxResults, cancellationToken);
                break;
            case IEventSymbol eventSymbol:
                AddEventOverrides(eventSymbol, analysis, matches, maxResults, cancellationToken);
                break;
            default:
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_target", $"Symbol kind '{anchorSymbol.Kind}' does not support override lookup.") });
        }

        matches = matches.Select(match => match.file_path is null ? match with { file_path = analysis.FilePath } : match).ToList();

        object data = new
        {
            file_path = analysis.FilePath,
            query = new { line, column, max_results = maxResults, workspace_path = workspacePath, require_workspace = requireWorkspace, workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext) },
            symbol = new
            {
                symbol_display = anchorSymbol.ToDisplayString(),
                symbol_kind = anchorSymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(anchorSymbol),
            },
            search_scope = searchScope,
            total_matches = matches.Count,
            matches,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static async Task<CommandExecutionResult> ExecuteBySymbolNameAsync(
        JsonElement input,
        string symbolQuery,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        int maxResults,
        CancellationToken cancellationToken)
    {
        (SymbolQueryResult? resolved, CommandExecutionResult? errorResult) = await SymbolQueryResolution.ResolveAsync(
            input,
            symbolQuery,
            workspacePath,
            targetPredicate: s => s is IMethodSymbol or IPropertySymbol or IEventSymbol,
            invalidTargetMessageFormat: "Symbol '{0}' ({1}) does not support override lookup.",
            allowSameTypeOverloads: false,
            cancellationToken).ConfigureAwait(false);
        if (errorResult is not null || resolved is null)
        {
            return errorResult!;
        }

        StaticAnalysisWorkspace workspace = resolved.Workspace;
        ISymbol targetSymbol = SolutionSearchScope.NormalizePositionalRecordProperty(resolved.Symbols[0]);
        IReadOnlyList<RelatedDeclaration> related = await SolutionSymbolRelations
            .FindOverridesAsync(workspace.Solution!, targetSymbol, cancellationToken)
            .ConfigureAwait(false);
        List<OverrideMatch> matches = related
            .Take(maxResults)
            .Select(declaration => new OverrideMatch(
                declaration.Kind,
                declaration.Line,
                declaration.Column,
                declaration.SymbolDisplay,
                declaration.FilePath))
            .ToList();

        (string? declarationFilePath, int? declarationLine, int? declarationColumn) = SymbolQueryResolution.GetPrimaryDeclarationLocation(targetSymbol);
        object data = new
        {
            file_path = declarationFilePath ?? workspace.ResolvedWorkspacePath,
            query = new
            {
                symbol = symbolQuery,
                line = declarationLine,
                column = declarationColumn,
                max_results = maxResults,
                workspace_path = workspacePath ?? workspace.ResolvedWorkspacePath,
                workspace_handle = workspaceHandle,
                require_workspace = requireWorkspace,
            },
            symbol = new
            {
                symbol_display = targetSymbol.ToDisplayString(),
                symbol_kind = targetSymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(targetSymbol),
            },
            search_scope = SolutionSearchScope.SolutionKind,
            total_matches = matches.Count,
            matches,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static void AddMethodOverrides(
        IMethodSymbol anchorMethod,
        CommandFileAnalysis analysis,
        List<OverrideMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IMethodSymbol baseMethod = anchorMethod.OverriddenMethod ?? anchorMethod;
        foreach (SyntaxNode declaration in analysis.Root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IMethodSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
            if (candidate?.OverriddenMethod is null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(candidate.OverriddenMethod, baseMethod) &&
                !SymbolEqualityComparer.Default.Equals(candidate.OverriddenMethod.OriginalDefinition, baseMethod.OriginalDefinition))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = (SymbolResolution.GetDeclarationIdentifierLocation(candidate, declaration) ?? declaration.GetLocation()).GetLineSpan();
            matches.Add(new OverrideMatch(
                kind: declaration.GetType().Name,
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString()));
        }
    }

    private static void AddPropertyOverrides(
        IPropertySymbol anchorProperty,
        CommandFileAnalysis analysis,
        List<OverrideMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IPropertySymbol baseProperty = anchorProperty.OverriddenProperty ?? anchorProperty;
        foreach (SyntaxNode declaration in analysis.Root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IPropertySymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IPropertySymbol;
            if (candidate?.OverriddenProperty is null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(candidate.OverriddenProperty, baseProperty) &&
                !SymbolEqualityComparer.Default.Equals(candidate.OverriddenProperty.OriginalDefinition, baseProperty.OriginalDefinition))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = (SymbolResolution.GetDeclarationIdentifierLocation(candidate, declaration) ?? declaration.GetLocation()).GetLineSpan();
            matches.Add(new OverrideMatch(
                kind: declaration.GetType().Name,
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString()));
        }
    }

    private static void AddEventOverrides(
        IEventSymbol anchorEvent,
        CommandFileAnalysis analysis,
        List<OverrideMatch> matches,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IEventSymbol baseEvent = anchorEvent.OverriddenEvent ?? anchorEvent;
        foreach (SyntaxNode declaration in analysis.Root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                break;
            }

            IEventSymbol? candidate = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IEventSymbol;
            if (candidate?.OverriddenEvent is null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(candidate.OverriddenEvent, baseEvent) &&
                !SymbolEqualityComparer.Default.Equals(candidate.OverriddenEvent.OriginalDefinition, baseEvent.OriginalDefinition))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = (SymbolResolution.GetDeclarationIdentifierLocation(candidate, declaration) ?? declaration.GetLocation()).GetLineSpan();
            matches.Add(new OverrideMatch(
                kind: declaration.GetType().Name,
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1,
                symbol_display: candidate.ToDisplayString()));
        }
    }

    private sealed record OverrideMatch(
        string kind,
        int line,
        int column,
        string symbol_display,
        string? file_path = null);
}

