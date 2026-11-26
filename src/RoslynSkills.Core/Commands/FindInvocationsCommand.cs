using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class FindInvocationsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_invocations",
        Summary: "Find method/constructor/type call sites anchored by line/column or --symbol with semantic matching across the active solution.",
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
            InputParsing.ValidateOptionalBool(input, "brief", errors);
            InputParsing.ValidateOptionalBool(input, "include_object_creations", errors);
            InputParsing.ValidateOptionalBool(input, "include_generated", errors);
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
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalBool(input, "include_object_creations", errors);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);

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
        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 200, minValue: 1, maxValue: 10_000);
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 1, minValue: 0, maxValue: 20);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        bool includeObjectCreations = InputParsing.GetOptionalBool(input, "include_object_creations", defaultValue: true);
        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
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
                includeObjectCreations,
                includeGenerated,
                brief,
                contextLines,
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

        if (!SolutionSearchScope.IsInvocationTarget(anchorSymbol))
        {
            string hint = SymbolResolution.FormatLineSymbolHint(analysis, line, SolutionSearchScope.IsInvocationTarget, cancellationToken);
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError(
                        "invalid_target",
                        $"Symbol '{anchorSymbol?.ToDisplayString() ?? "<unknown>"}' is not a method, constructor, or constructible type. Anchor to a method, constructor, or class/struct/record and retry.{hint}"),
                });
        }

        ISymbol targetSymbol = anchorSymbol!;
        SearchScope scope = await SolutionSearchScope.ResolveAsync(analysis, targetSymbol, includeGenerated, cancellationToken).ConfigureAwait(false);
        (List<InvocationMatch> matches, bool truncated, int searchedSyntaxTreeCount) = await SearchInvocationsAsync(
            scope,
            new[] { targetSymbol },
            includeObjectCreations,
            brief,
            contextLines,
            maxResults,
            cancellationToken).ConfigureAwait(false);

        object matchesPayload = FormatMatchesPayload(matches, brief);

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                max_results = maxResults,
                context_lines = contextLines,
                include_object_creations = includeObjectCreations,
                include_generated = includeGenerated,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                brief,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            symbol = new
            {
                symbol_display = targetSymbol.ToDisplayString(),
                symbol_kind = targetSymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(targetSymbol),
                method_kind = (targetSymbol as IMethodSymbol)?.MethodKind.ToString(),
            },
            searched_syntax_trees = searchedSyntaxTreeCount,
            search = scope.ToPayload(),
            total_matches = matches.Count,
            truncated,
            matches = matchesPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private async Task<CommandExecutionResult> ExecuteBySymbolNameAsync(
        JsonElement input,
        string symbolQuery,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        bool includeObjectCreations,
        bool includeGenerated,
        bool brief,
        int contextLines,
        int maxResults,
        CancellationToken cancellationToken)
    {
        (SymbolQueryResult? resolved, CommandExecutionResult? errorResult) = await SymbolQueryResolution.ResolveAsync(
            input,
            symbolQuery,
            workspacePath,
            targetPredicate: SolutionSearchScope.IsInvocationTarget,
            invalidTargetMessageFormat: "Symbol '{0}' ({1}) is not a method, constructor, or constructible type. Specify a method, constructor, or class/struct/record and retry.",
            allowSameTypeOverloads: true,
            cancellationToken).ConfigureAwait(false);
        if (errorResult is not null || resolved is null)
        {
            return errorResult!;
        }

        StaticAnalysisWorkspace workspace = resolved.Workspace;
        IReadOnlyList<ISymbol> targetSymbols = resolved.Symbols;
        ISymbol primarySymbol = targetSymbols[0];
        SearchScope scope = await SolutionSearchScope.ResolveForSolutionAsync(
            workspace.Solution!,
            anchorProject: null,
            target: primarySymbol,
            includeGenerated,
            cancellationToken).ConfigureAwait(false);

        (List<InvocationMatch> matches, bool truncated, int searchedSyntaxTreeCount) = await SearchInvocationsAsync(
            scope,
            targetSymbols,
            includeObjectCreations,
            brief,
            contextLines,
            maxResults,
            cancellationToken).ConfigureAwait(false);

        (string? declarationFilePath, int? declarationLine, int? declarationColumn) = SymbolQueryResolution.GetPrimaryDeclarationLocation(primarySymbol);
        object matchesPayload = FormatMatchesPayload(matches, brief);
        object[]? matchedOverloads = targetSymbols.Count > 1
            ? targetSymbols.Select(SymbolQueryResolution.FormatSymbolCandidate).ToArray()
            : null;

        object data = new
        {
            file_path = declarationFilePath ?? workspace.ResolvedWorkspacePath,
            query = new
            {
                symbol = symbolQuery,
                line = declarationLine,
                column = declarationColumn,
                max_results = maxResults,
                context_lines = contextLines,
                include_object_creations = includeObjectCreations,
                include_generated = includeGenerated,
                workspace_path = workspacePath ?? workspace.ResolvedWorkspacePath,
                workspace_handle = workspaceHandle,
                require_workspace = requireWorkspace,
                brief,
            },
            symbol = new
            {
                symbol_display = primarySymbol.ToDisplayString(),
                symbol_kind = primarySymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(primarySymbol),
                method_kind = (primarySymbol as IMethodSymbol)?.MethodKind.ToString(),
                overload_count = targetSymbols.Count,
            },
            matched_overloads = matchedOverloads,
            searched_syntax_trees = searchedSyntaxTreeCount,
            search = scope.ToPayload(),
            total_matches = matches.Count,
            truncated,
            matches = matchesPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static async Task<(List<InvocationMatch> Matches, bool Truncated, int SearchedSyntaxTreeCount)> SearchInvocationsAsync(
        SearchScope scope,
        IReadOnlyList<ISymbol> targetSymbols,
        bool includeObjectCreations,
        bool brief,
        int contextLines,
        int maxResults,
        CancellationToken cancellationToken)
    {
        string? searchName = SolutionSearchScope.GetSearchName(targetSymbols[0]);
        List<SearchUnit> candidateUnits = new();
        foreach (SearchUnit unit in scope.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SolutionSearchScope.MightMention(unit, searchName))
            {
                candidateUnits.Add(unit);
            }
        }

        // If several candidate trees are still cold (e.g. a query arriving before background warmup finishes),
        // populate their call-site caches in parallel before assembling matches in deterministic order.
        if (candidateUnits.Count > 1)
        {
            List<SearchUnit>? coldUnits = null;
            foreach (SearchUnit unit in candidateUnits)
            {
                if (!CallSiteAnalysis.IsCached(unit.Compilation, unit.Tree))
                {
                    coldUnits ??= new List<SearchUnit>();
                    coldUnits.Add(unit);
                }
            }

            if (coldUnits is { Count: > 1 })
            {
                ParallelOptions options = new()
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 6),
                    CancellationToken = cancellationToken,
                };
                Parallel.ForEach(coldUnits, options, unit =>
                {
                    _ = CallSiteAnalysis.GetCallSites(unit.Compilation, unit.Tree, includeObjectCreations: true, cancellationToken);
                });
            }
        }

        List<InvocationMatch> matches = new();
        HashSet<string> seenMatches = new(StringComparer.Ordinal);
        bool truncated = false;
        int searchedSyntaxTreeCount = 0;

        foreach (SearchUnit unit in candidateUnits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            searchedSyntaxTreeCount++;

            IReadOnlyList<CallSiteAnalysis.CallSite> sites = searchName is not null
                ? CallSiteAnalysis.GetCallSitesByName(unit.Compilation, unit.Tree, searchName, includeObjectCreations, cancellationToken)
                : CallSiteAnalysis.GetCallSites(unit.Compilation, unit.Tree, includeObjectCreations, cancellationToken);

            if (sites.Count == 0)
            {
                continue;
            }

            SourceText? sourceText = null;
            foreach (CallSiteAnalysis.CallSite callSite in sites)
            {
                if (!MatchesAnyTarget(callSite.callee, targetSymbols))
                {
                    continue;
                }

                if (!seenMatches.Add($"{unit.FilePath}|{callSite.node.SpanStart}|{callSite.call_kind}"))
                {
                    continue;
                }

                sourceText ??= await unit.Tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                AddMatch(
                    matches,
                    callSite.node.Span,
                    callSite.callee,
                    unit.FilePath,
                    sourceText,
                    contextLines,
                    maxResults,
                    brief,
                    callSite.call_kind);

                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        return (matches, truncated, searchedSyntaxTreeCount);
    }

    private static bool MatchesAnyTarget(IMethodSymbol callee, IReadOnlyList<ISymbol> targetSymbols)
    {
        for (int i = 0; i < targetSymbols.Count; i++)
        {
            if (SolutionSearchScope.MatchesInvocationCallee(callee, targetSymbols[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static object FormatMatchesPayload(List<InvocationMatch> matches, bool brief)
        => brief
            ? matches.Select(match => new
            {
                match.file_path,
                match.line,
                match.column,
                match.call_kind,
                match.symbol_display,
            }).ToArray()
            : matches;

    private static void AddMatch(
        List<InvocationMatch> matches,
        TextSpan span,
        IMethodSymbol calledMethod,
        string filePath,
        SourceText sourceText,
        int contextLines,
        int maxResults,
        bool brief,
        string callKind)
    {
        if (matches.Count >= maxResults)
        {
            return;
        }

        LinePositionSpan lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        int line = lineSpan.Start.Line + 1;
        int column = lineSpan.Start.Character + 1;
        string snippet = brief
            ? string.Empty
            : CommandTextFormatting.BuildSnippet(sourceText, line, contextLines);

        matches.Add(new InvocationMatch(
            file_path: filePath,
            line: line,
            column: column,
            call_kind: callKind,
            symbol_display: calledMethod.ToDisplayString(),
            snippet: snippet));
    }

    private sealed record InvocationMatch(
        string file_path,
        int line,
        int column,
        string call_kind,
        string symbol_display,
        string snippet);
}
