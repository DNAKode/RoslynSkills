using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class FindReferencesCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_references",
        Summary: "Find same-symbol references anchored by line/column or --symbol with semantic matching; spans the whole loaded solution when available.",
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
            InputParsing.ValidateOptionalBool(input, "include_declaration", errors);
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
        InputParsing.ValidateOptionalBool(input, "include_declaration", errors);
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
        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 200, minValue: 1, maxValue: 2_000);
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 2, minValue: 0, maxValue: 20);
        bool includeDeclaration = InputParsing.GetOptionalBool(input, "include_declaration", defaultValue: true);
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
                includeDeclaration,
                includeGenerated,
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
                includeDeclaration,
                includeGenerated,
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
        ISymbol? targetSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        if (targetSymbol is null)
        {
            string hint = SymbolResolution.FormatLineSymbolHint(analysis, line, predicate: null, cancellationToken);
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", $"Unable to resolve a semantic symbol at {line}:{column}.{hint}") });
        }

        targetSymbol = SolutionSearchScope.NormalizePositionalRecordProperty(targetSymbol);

        string? targetSymbolId = CommandTextFormatting.GetStableSymbolId(targetSymbol);
        List<ReferenceMatch> matches;
        bool truncated;
        SearchScope? scope = null;
        Solution? solution = analysis.Document?.Project.Solution;
        if (solution is not null)
        {
            (matches, truncated) = await FindSolutionReferencesAsync(
                solution,
                targetSymbol,
                includeDeclaration,
                includeGenerated,
                contextLines,
                maxResults,
                cancellationToken).ConfigureAwait(false);
            scope = new SearchScope(Array.Empty<SearchUnit>(), SolutionSearchScope.SolutionKind, solution.Projects.Count());
        }
        else
        {
            (matches, truncated) = FindSameFileReferences(analysis, targetSymbol, includeDeclaration, contextLines, maxResults, cancellationToken);
        }

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                include_declaration = includeDeclaration,
                include_generated = includeGenerated,
                max_results = maxResults,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            symbol = new
            {
                symbol_display = targetSymbol.ToDisplayString(),
                symbol_kind = targetSymbol.Kind.ToString(),
                symbol_id = targetSymbolId,
            },
            search_scope = scope?.Kind ?? SolutionSearchScope.CompilationKind,
            searched_projects = scope?.ProjectCount ?? 1,
            total_matches = matches.Count,
            truncated,
            matches,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private async Task<CommandExecutionResult> ExecuteBySymbolNameAsync(
        JsonElement input,
        string symbolQuery,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        bool includeDeclaration,
        bool includeGenerated,
        int contextLines,
        int maxResults,
        CancellationToken cancellationToken)
    {
        (SymbolQueryResult? resolved, CommandExecutionResult? errorResult) = await SymbolQueryResolution.ResolveAsync(
            input,
            symbolQuery,
            workspacePath,
            targetPredicate: null,
            invalidTargetMessageFormat: string.Empty,
            allowSameTypeOverloads: false,
            cancellationToken).ConfigureAwait(false);
        if (errorResult is not null || resolved is null)
        {
            return errorResult!;
        }

        StaticAnalysisWorkspace workspace = resolved.Workspace;
        ISymbol targetSymbol = SolutionSearchScope.NormalizePositionalRecordProperty(resolved.Symbols[0]);
        string? targetSymbolId = CommandTextFormatting.GetStableSymbolId(targetSymbol);
        (List<ReferenceMatch> matches, bool truncated) = await FindSolutionReferencesAsync(
            workspace.Solution!,
            targetSymbol,
            includeDeclaration,
            includeGenerated,
            contextLines,
            maxResults,
            cancellationToken).ConfigureAwait(false);

        (string? declarationFilePath, int? declarationLine, int? declarationColumn) = SymbolQueryResolution.GetPrimaryDeclarationLocation(targetSymbol);

        object data = new
        {
            file_path = declarationFilePath ?? workspace.ResolvedWorkspacePath,
            query = new
            {
                symbol = symbolQuery,
                line = declarationLine,
                column = declarationColumn,
                include_declaration = includeDeclaration,
                include_generated = includeGenerated,
                max_results = maxResults,
                workspace_path = workspacePath ?? workspace.ResolvedWorkspacePath,
                workspace_handle = workspaceHandle,
                require_workspace = requireWorkspace,
            },
            symbol = new
            {
                symbol_display = targetSymbol.ToDisplayString(),
                symbol_kind = targetSymbol.Kind.ToString(),
                symbol_id = targetSymbolId,
            },
            search_scope = SolutionSearchScope.SolutionKind,
            searched_projects = workspace.Solution!.Projects.Count(),
            total_matches = matches.Count,
            truncated,
            matches,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private async Task<CommandExecutionResult> ExecuteBySymbolBatchAsync(
        JsonElement input,
        string[] symbolQueries,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        bool includeDeclaration,
        bool includeGenerated,
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

        List<object> batchEntries = new(symbolQueries.Length);
        int aggregateMatches = 0;
        int succeededCount = 0;
        int failedCount = 0;

        foreach (string query in symbolQueries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ISymbol> candidates = await SymbolQueryResolution.ResolveSymbolsByNameAsync(workspace.Solution, query, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                failedCount++;
                batchEntries.Add(new
                {
                    symbol_query = query,
                    ok = false,
                    error = new CommandError("symbol_not_found", $"No source symbol matching '{query}' was found."),
                });
                continue;
            }

            if (candidates.Count > 1)
            {
                failedCount++;
                batchEntries.Add(new
                {
                    symbol_query = query,
                    ok = false,
                    error = new CommandError(
                        "ambiguous_symbol",
                        $"Symbol '{query}' matched {candidates.Count} declarations.",
                        new { candidates = candidates.Take(10).Select(SymbolQueryResolution.FormatSymbolCandidate).ToArray() }),
                });
                continue;
            }

            ISymbol targetSymbol = SolutionSearchScope.NormalizePositionalRecordProperty(candidates[0]);
            (List<ReferenceMatch> matches, bool truncated) = await FindSolutionReferencesAsync(
                workspace.Solution,
                targetSymbol,
                includeDeclaration,
                includeGenerated,
                contextLines,
                maxResults,
                cancellationToken).ConfigureAwait(false);

            succeededCount++;
            aggregateMatches += matches.Count;
            batchEntries.Add(new
            {
                symbol_query = query,
                ok = true,
                symbol = new
                {
                    symbol_display = targetSymbol.ToDisplayString(),
                    symbol_kind = targetSymbol.Kind.ToString(),
                    symbol_id = CommandTextFormatting.GetStableSymbolId(targetSymbol),
                },
                total_matches = matches.Count,
                truncated,
                matches,
            });
        }

        object data = new
        {
            batched = true,
            query = new
            {
                symbols = symbolQueries,
                include_declaration = includeDeclaration,
                include_generated = includeGenerated,
                max_results = maxResults,
                workspace_path = workspacePath ?? workspace.ResolvedWorkspacePath,
                workspace_handle = workspaceHandle,
                require_workspace = requireWorkspace,
            },
            search_scope = SolutionSearchScope.SolutionKind,
            searched_projects = workspace.Solution.Projects.Count(),
            symbol_count = symbolQueries.Length,
            succeeded = succeededCount,
            failed = failedCount,
            total_matches = aggregateMatches,
            results = batchEntries,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static (List<ReferenceMatch> Matches, bool Truncated) FindSameFileReferences(
        CommandFileAnalysis analysis,
        ISymbol targetSymbol,
        bool includeDeclaration,
        int contextLines,
        int maxResults,
        CancellationToken cancellationToken)
    {
        List<ReferenceMatch> matches = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool truncated = false;

        foreach (SyntaxToken token in analysis.Root.DescendantTokens(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CommandLanguageServices.IsIdentifierToken(token, analysis.Language))
            {
                continue;
            }

            ISymbol? symbol = SymbolResolution.GetSymbolForToken(token, analysis.SemanticModel, cancellationToken);
            if (!SolutionSearchScope.MatchesReferenceDefinition(symbol, targetSymbol, out _))
            {
                continue;
            }

            bool isDeclaration = CommandTextFormatting.IsDeclarationToken(token, analysis.SemanticModel, cancellationToken);
            if (!includeDeclaration && isDeclaration)
            {
                continue;
            }

            LinePositionSpan span = analysis.SourceText.Lines.GetLinePositionSpan(token.Span);
            int tokenLine = span.Start.Line + 1;
            int tokenColumn = span.Start.Character + 1;
            if (!seen.Add($"{tokenLine}|{tokenColumn}"))
            {
                continue;
            }

            if (matches.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            string snippet = CommandTextFormatting.BuildSnippet(analysis.SourceText, tokenLine, contextLines);
            matches.Add(new ReferenceMatch(
                file_path: analysis.FilePath,
                line: tokenLine,
                column: tokenColumn,
                syntax_kind: CommandLanguageServices.GetSyntaxKindName(token.Parent),
                is_declaration: isDeclaration,
                snippet: snippet));
        }

        // Also capture target-typed `new(...)` object creations which have no type identifier token.
        if (!truncated &&
            (targetSymbol is INamedTypeSymbol || targetSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor }))
        {
            foreach (CallSiteAnalysis.CallSite site in CallSiteAnalysis.EnumerateCallSites(
                         analysis.SemanticModel,
                         analysis.Root,
                         includeObjectCreations: true,
                         cancellationToken))
            {
                if (!string.Equals(site.call_kind, "object_creation", StringComparison.Ordinal) ||
                    !SolutionSearchScope.MatchesInvocationCallee(site.callee, targetSymbol))
                {
                    continue;
                }

                // Explicit `new Foo(...)` is already matched via the identifier token `Foo`; only add implicit `new(...)` or unmatched spans.
                if (site.node is Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax explicitCreation)
                {
                    LinePositionSpan typeSpan = analysis.SourceText.Lines.GetLinePositionSpan(explicitCreation.Type.Span);
                    if (seen.Contains($"{typeSpan.Start.Line + 1}|{typeSpan.Start.Character + 1}"))
                    {
                        continue;
                    }
                }

                LinePositionSpan span = analysis.SourceText.Lines.GetLinePositionSpan(site.node.Span);
                int siteLine = span.Start.Line + 1;
                int siteColumn = span.Start.Character + 1;
                if (!seen.Add($"{siteLine}|{siteColumn}"))
                {
                    continue;
                }

                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                matches.Add(new ReferenceMatch(
                    file_path: analysis.FilePath,
                    line: siteLine,
                    column: siteColumn,
                    syntax_kind: CommandLanguageServices.GetSyntaxKindName(site.node),
                    is_declaration: false,
                    snippet: CommandTextFormatting.BuildSnippet(analysis.SourceText, siteLine, contextLines)));
            }
        }

        matches = matches
            .OrderBy(match => match.line)
            .ThenBy(match => match.column)
            .ToList();
        return (matches, truncated);
    }

    /// <summary>
    /// Whole-solution search: Roslyn's symbol finder visits every document of every project that can see the symbol,
    /// so callers in sibling projects are found, not just the anchor file.
    /// For a named type, includes its constructor reference groups so explicit <c>new Type(...)</c>,
    /// target-typed <c>new(...)</c>, and primary-constructor creations are all reported.
    /// </summary>
    internal static async Task<(List<ReferenceMatch> Matches, bool Truncated)> FindSolutionReferencesAsync(
        Solution solution,
        ISymbol targetSymbol,
        bool includeDeclaration,
        bool includeGenerated,
        int contextLines,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IEnumerable<ReferencedSymbol> referenced = await SymbolFinder
            .FindReferencesAsync(targetSymbol, solution, cancellationToken)
            .ConfigureAwait(false);

        // Cascading also reports overrides, implementations and base members; keep the anchored symbol,
        // its positional-record counterpart (synthesized property <-> primary constructor parameter), and
        // (for a named type) its constructors so object creations are included.
        List<(ReferencedSymbol Group, bool IsConstructorOfTargetType)> groups = new();
        ISymbol? positionalCounterpart = SolutionSearchScope.TryGetPositionalRecordCounterpart(targetSymbol);
        bool sawCounterpart = false;
        foreach (ReferencedSymbol group in referenced)
        {
            if (SolutionSearchScope.MatchesReferenceDefinition(group.Definition, targetSymbol, out bool isCtorOfTargetType))
            {
                groups.Add((group, isCtorOfTargetType));
                if (positionalCounterpart is not null &&
                    SolutionSearchScope.IsSameSymbol(group.Definition, positionalCounterpart))
                {
                    sawCounterpart = true;
                }
            }
        }

        // Roslyn's FindReferencesAsync does not always cascade between a positional record's synthesized property
        // and its primary constructor parameter (e.g. `{ Kind: ... }` vs `new Record(Kind: ...)`). Search both.
        if (positionalCounterpart is not null && !sawCounterpart)
        {
            IEnumerable<ReferencedSymbol> counterpartRefs = await SymbolFinder
                .FindReferencesAsync(positionalCounterpart, solution, cancellationToken)
                .ConfigureAwait(false);
            foreach (ReferencedSymbol group in counterpartRefs)
            {
                if (SolutionSearchScope.MatchesReferenceDefinition(group.Definition, targetSymbol, out bool isCtorOfTargetType))
                {
                    groups.Add((group, isCtorOfTargetType));
                }
            }
        }

        List<(string FilePath, int Line, int Column, string Kind, bool IsDeclaration, string Snippet)> found = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        Dictionary<SyntaxTree, SourceText> texts = new();
        Dictionary<SyntaxTree, SyntaxNode> roots = new();

        async Task<SyntaxNode> GetRootAsync(SyntaxTree tree)
        {
            if (!roots.TryGetValue(tree, out SyntaxNode? root))
            {
                root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                roots[tree] = root;
            }

            return root;
        }

        async Task AddAsync(Location location, bool isDeclaration)
        {
            if (!location.IsInSource || location.SourceTree is null)
            {
                return;
            }

            SyntaxTree tree = location.SourceTree;
            string filePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);
            if (!includeGenerated && CommandFileFilters.IsGeneratedPath(filePath))
            {
                return;
            }

            if (!texts.TryGetValue(tree, out SourceText? text))
            {
                text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                texts[tree] = text;
            }

            LinePositionSpan span = text.Lines.GetLinePositionSpan(location.SourceSpan);
            int line = span.Start.Line + 1;
            int column = span.Start.Character + 1;
            if (!seen.Add($"{filePath}|{line}|{column}"))
            {
                return;
            }

            SyntaxNode root = await GetRootAsync(tree).ConfigureAwait(false);
            SyntaxToken token = root.FindToken(location.SourceSpan.Start);
            found.Add((
                filePath,
                line,
                column,
                CommandLanguageServices.GetSyntaxKindName(token.Parent),
                isDeclaration,
                CommandTextFormatting.BuildSnippet(text, line, contextLines)));
        }

        foreach ((ReferencedSymbol group, bool isConstructorOfTargetType) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includeDeclaration && !isConstructorOfTargetType)
            {
                foreach (Location definitionLocation in group.Definition.Locations)
                {
                    await AddAsync(definitionLocation, isDeclaration: true).ConfigureAwait(false);
                }
            }

            foreach (ReferenceLocation reference in group.Locations)
            {
                if (reference.IsCandidateLocation)
                {
                    continue;
                }

                if (reference.IsImplicit)
                {
                    // Target-typed `new(...)` may be flagged implicit by SymbolFinder even though `new(...)` is written in source.
                    if (!reference.Location.IsInSource || reference.Location.SourceTree is null)
                    {
                        continue;
                    }

                    SyntaxNode root = await GetRootAsync(reference.Location.SourceTree).ConfigureAwait(false);
                    SyntaxToken token = root.FindToken(reference.Location.SourceSpan.Start);
                    bool isObjectCreation = token.Parent?.AncestorsAndSelf().Any(
                        node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseObjectCreationExpressionSyntax) == true;
                    if (!isObjectCreation)
                    {
                        continue;
                    }
                }

                await AddAsync(reference.Location, isDeclaration: false).ConfigureAwait(false);
            }
        }

        List<ReferenceMatch> ordered = found
            .OrderBy(match => match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Line)
            .ThenBy(match => match.Column)
            .Take(maxResults)
            .Select(match => new ReferenceMatch(match.FilePath, match.Line, match.Column, match.Kind, match.IsDeclaration, match.Snippet))
            .ToList();
        return (ordered, found.Count > maxResults);
    }

    internal sealed record ReferenceMatch(
        string file_path,
        int line,
        int column,
        string syntax_kind,
        bool is_declaration,
        string snippet);
}

