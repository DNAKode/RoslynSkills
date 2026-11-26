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
        string[] symbolBatch = SymbolQueryResolution.GetOptionalSymbolQueries(input);
        string? symbolQuery = SymbolQueryResolution.GetOptionalSymbolQuery(input);
        if (symbolBatch.Length > 0 || !string.IsNullOrWhiteSpace(symbolQuery))
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

        string[] symbolBatch = SymbolQueryResolution.GetOptionalSymbolQueries(input);
        if (symbolBatch.Length > 0)
        {
            return await ExecuteBySymbolBatchAsync(
                input,
                symbolBatch,
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

    private async Task<CommandExecutionResult> ExecuteBySymbolBatchAsync(
        JsonElement input,
        string[] symbolQueries,
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
        (StaticAnalysisWorkspace? workspace, CommandError? loadError) = await StaticAnalysisWorkspace
            .ResolveAsync(input, workspacePath, includeGenerated: false, maxFiles: 50_000, cancellationToken)
            .ConfigureAwait(false);
        if (loadError is not null || workspace?.Solution is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { loadError ?? new CommandError("workspace_required", "Multi-symbol lookup (--symbols) requires a loaded MSBuild solution or project workspace.") });
        }

        // Resolve all requested symbols upfront so we scan candidate syntax trees in a single compilation pass.
        List<(string Query, IReadOnlyList<ISymbol>? Targets, CommandError? Error)> resolvedList = new(symbolQueries.Length);
        List<ISymbol> allTargets = new();

        foreach (string query in symbolQueries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ISymbol> candidates = await SymbolQueryResolution.ResolveSymbolsByNameAsync(workspace.Solution, query, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                resolvedList.Add((query, null, new CommandError("symbol_not_found", $"No source symbol matching '{query}' was found.")));
                continue;
            }

            List<ISymbol> filtered = candidates.Where(SolutionSearchScope.IsInvocationTarget).ToList();
            if (filtered.Count == 0)
            {
                resolvedList.Add((query, null, new CommandError("invalid_target", $"Symbol '{candidates[0].ToDisplayString()}' is not a method, constructor, or constructible type.")));
                continue;
            }

            if (filtered.Count > 1 && !SymbolQueryResolution.AreSameTypeMethodOverloads(filtered))
            {
                resolvedList.Add((query, null, new CommandError(
                    "ambiguous_symbol",
                    $"Symbol '{query}' matched {filtered.Count} declarations.",
                    new { candidates = filtered.Take(10).Select(SymbolQueryResolution.FormatSymbolCandidate).ToArray() })));
                continue;
            }

            resolvedList.Add((query, filtered, null));
            allTargets.AddRange(filtered);
        }

        SearchScope scope = await SolutionSearchScope.ResolveForSolutionAsync(
            workspace.Solution,
            anchorProject: null,
            target: null,
            includeGenerated,
            cancellationToken).ConfigureAwait(false);

        int searchedTrees = 0;
        Dictionary<int, (List<InvocationMatch> Matches, bool Truncated)> perQueryMatches = new();
        for (int i = 0; i < resolvedList.Count; i++)
        {
            perQueryMatches[i] = (new List<InvocationMatch>(), false);
        }

        if (allTargets.Count > 0)
        {
            CallSiteLookup combinedLookup = CallSiteLookup.ForTargets(allTargets, CallSiteLookup.GetCompilations(scope.Units));
            List<SearchUnit> candidateUnits = new();
            foreach (SearchUnit unit in scope.Units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (combinedLookup.MightMention(unit))
                {
                    candidateUnits.Add(unit);
                }
            }

            if (candidateUnits.Count > 1)
            {
                List<SearchUnit>? coldUnits = null;
                foreach (SearchUnit unit in candidateUnits)
                {
                    if (!CallSiteAnalysis.IsLookupCached(unit.Compilation, unit.Tree, combinedLookup))
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
                        _ = CallSiteAnalysis.GetCallSitesForLookup(unit.Compilation, unit.Tree, combinedLookup, includeObjectCreations: true, cancellationToken);
                    });
                }
            }

            Dictionary<int, HashSet<string>> seenByQuery = new();
            for (int i = 0; i < resolvedList.Count; i++)
            {
                seenByQuery[i] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (SearchUnit unit in candidateUnits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                searchedTrees++;
                IReadOnlyList<CallSiteAnalysis.CallSite> sites = CallSiteAnalysis.GetCallSitesForLookup(
                    unit.Compilation,
                    unit.Tree,
                    combinedLookup,
                    includeObjectCreations,
                    cancellationToken);
                if (sites.Count == 0)
                {
                    continue;
                }

                SourceText? sourceText = null;
                foreach (CallSiteAnalysis.CallSite callSite in sites)
                {
                    for (int i = 0; i < resolvedList.Count; i++)
                    {
                        IReadOnlyList<ISymbol>? targets = resolvedList[i].Targets;
                        if (targets is null || !MatchesAnyTarget(callSite.callee, targets))
                        {
                            continue;
                        }

                        if (!seenByQuery[i].Add($"{unit.FilePath}|{callSite.node.SpanStart}|{callSite.call_kind}"))
                        {
                            continue;
                        }

                        (List<InvocationMatch> list, bool trunc) = perQueryMatches[i];
                        if (list.Count >= maxResults)
                        {
                            perQueryMatches[i] = (list, true);
                            continue;
                        }

                        sourceText ??= await unit.Tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        AddMatch(
                            list,
                            callSite.node.Span,
                            callSite.callee,
                            unit.FilePath,
                            sourceText,
                            contextLines,
                            maxResults,
                            brief,
                            callSite.call_kind);
                    }
                }
            }
        }

        List<object> results = new(resolvedList.Count);
        int aggregateMatches = 0;
        int succeededCount = 0;
        int failedCount = 0;

        for (int i = 0; i < resolvedList.Count; i++)
        {
            (string query, IReadOnlyList<ISymbol>? targets, CommandError? error) = resolvedList[i];
            if (error is not null || targets is null)
            {
                failedCount++;
                results.Add(new
                {
                    symbol_query = query,
                    ok = false,
                    error,
                });
                continue;
            }

            (List<InvocationMatch> matches, bool truncated) = perQueryMatches[i];
            ISymbol primary = targets[0];
            succeededCount++;
            aggregateMatches += matches.Count;
            results.Add(new
            {
                symbol_query = query,
                ok = true,
                symbol = new
                {
                    symbol_display = primary.ToDisplayString(),
                    symbol_kind = primary.Kind.ToString(),
                    symbol_id = CommandTextFormatting.GetStableSymbolId(primary),
                    overload_count = targets.Count,
                },
                total_matches = matches.Count,
                truncated,
                matches = FormatMatchesPayload(matches, brief),
            });
        }

        object data = new
        {
            batched = true,
            query = new
            {
                symbols = symbolQueries,
                max_results = maxResults,
                context_lines = contextLines,
                include_object_creations = includeObjectCreations,
                include_generated = includeGenerated,
                workspace_path = workspacePath ?? workspace.ResolvedWorkspacePath,
                workspace_handle = workspaceHandle,
                require_workspace = requireWorkspace,
                brief,
            },
            searched_syntax_trees = searchedTrees,
            search = scope.ToPayload(),
            symbol_count = symbolQueries.Length,
            succeeded = succeededCount,
            failed = failedCount,
            total_matches = aggregateMatches,
            results,
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
        // Narrow by what the target can be called as (spelled name, type alias, target-typed new, ctor initializer) and
        // keep every tree that might hold a call; see CallSiteLookup for the completeness rules.
        CallSiteLookup lookup = CallSiteLookup.ForTargets(targetSymbols, CallSiteLookup.GetCompilations(scope.Units));
        List<SearchUnit> candidateUnits = new();
        foreach (SearchUnit unit in scope.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lookup.MightMention(unit))
            {
                candidateUnits.Add(unit);
            }
        }

        // If several candidate trees are still cold (e.g. a query arriving before background warmup finishes),
        // bind them selectively in parallel before assembling matches in deterministic order.
        if (candidateUnits.Count > 1)
        {
            List<SearchUnit>? coldUnits = null;
            foreach (SearchUnit unit in candidateUnits)
            {
                if (!CallSiteAnalysis.IsLookupCached(unit.Compilation, unit.Tree, lookup))
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
                    _ = CallSiteAnalysis.GetCallSitesForLookup(unit.Compilation, unit.Tree, lookup, includeObjectCreations: true, cancellationToken);
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

            IReadOnlyList<CallSiteAnalysis.CallSite> sites = CallSiteAnalysis.GetCallSitesForLookup(
                unit.Compilation,
                unit.Tree,
                lookup,
                includeObjectCreations,
                cancellationToken);

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
