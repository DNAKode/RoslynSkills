using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class CallHierarchyCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; }

    public CallHierarchyCommand()
        : this(
            commandId: "nav.call_hierarchy",
            summary: "Build incoming/outgoing call hierarchy for an anchored method or --symbol with bounded depth.")
    {
    }

    internal CallHierarchyCommand(string commandId, string summary)
    {
        Descriptor = new CommandDescriptor(
            Id: commandId,
            Summary: summary,
            InputSchemaVersion: "1.0",
            OutputSchemaVersion: "1.0",
            MutatesState: false,
            Maturity: CommandMaturity.Advanced,
            Traits: [CommandTrait.Heuristic, CommandTrait.DerivedAnalysis, CommandTrait.PotentiallySlow],
            RunsOnHotWorkspace: true);
    }

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
            InputParsing.ValidateOptionalBool(input, "include_external", errors);
            InputParsing.ValidateOptionalBool(input, "include_generated", errors);
            if (!TryParseDirection(input, out _))
            {
                errors.Add(new CommandError("invalid_input", "Property 'direction' must be 'incoming', 'outgoing', or 'both'."));
            }

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
        InputParsing.ValidateOptionalBool(input, "include_external", errors);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);

        if (!TryParseDirection(input, out _))
        {
            errors.Add(new CommandError("invalid_input", "Property 'direction' must be 'incoming', 'outgoing', or 'both'."));
        }

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
        if (!TryParseDirection(input, out CallHierarchyDirection direction))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'direction' must be 'incoming', 'outgoing', or 'both'.") });
        }

        int maxDepth = InputParsing.GetOptionalInt(input, "max_depth", defaultValue: 2, minValue: 1, maxValue: 8);
        int maxNodes = InputParsing.GetOptionalInt(input, "max_nodes", defaultValue: 150, minValue: 1, maxValue: 2_000);
        int maxEdges = InputParsing.GetOptionalInt(input, "max_edges", defaultValue: 400, minValue: 1, maxValue: 10_000);
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 1, minValue: 0, maxValue: 10);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        bool includeObjectCreations = InputParsing.GetOptionalBool(input, "include_object_creations", defaultValue: true);
        bool includeExternal = InputParsing.GetOptionalBool(input, "include_external", defaultValue: false);
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
                direction,
                maxDepth,
                maxNodes,
                maxEdges,
                contextLines,
                brief,
                includeObjectCreations,
                includeExternal,
                includeGenerated,
                workspacePath,
                workspaceHandle,
                requireWorkspace,
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
        if (anchorSymbol is not (IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol))
        {
            string hint = SymbolResolution.FormatLineSymbolHint(
                analysis,
                line,
                s => s is IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol,
                cancellationToken);
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError(
                        "invalid_target",
                        $"Symbol '{anchorSymbol?.ToDisplayString() ?? "<unknown>"}' is not a method or member. Anchor to a method or member declaration/reference and retry.{hint}"),
                });
        }

        ISymbol normalizedAnchor = NormalizeGraphSymbol(anchorSymbol);
        SearchScope scope = await SolutionSearchScope.ResolveAsync(analysis, target: null, includeGenerated, cancellationToken).ConfigureAwait(false);
        CallGraph graph = CallGraph.Create(scope, includeObjectCreations, includeExternal, contextLines, brief);

        return BuildHierarchyResult(
            normalizedAnchor,
            scope,
            analysis.FilePath,
            line,
            column,
            symbolQuery: null,
            direction,
            maxDepth,
            maxNodes,
            maxEdges,
            contextLines,
            brief,
            includeObjectCreations,
            includeExternal,
            includeGenerated,
            workspacePath,
            workspaceHandle: null,
            requireWorkspace,
            WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            graph,
            cancellationToken);
    }

    private static async Task<CommandExecutionResult> ExecuteBySymbolNameAsync(
        JsonElement input,
        string symbolQuery,
        CallHierarchyDirection direction,
        int maxDepth,
        int maxNodes,
        int maxEdges,
        int contextLines,
        bool brief,
        bool includeObjectCreations,
        bool includeExternal,
        bool includeGenerated,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        CancellationToken cancellationToken)
    {
        (SymbolQueryResult? resolved, CommandExecutionResult? errorResult) = await SymbolQueryResolution.ResolveAsync(
            input,
            symbolQuery,
            workspacePath,
            targetPredicate: s => s is IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol,
            invalidTargetMessageFormat: "Symbol '{0}' ({1}) is not a method or member. Specify a method or member declaration and retry.",
            allowSameTypeOverloads: false,
            cancellationToken).ConfigureAwait(false);
        if (errorResult is not null || resolved is null)
        {
            return errorResult!;
        }

        StaticAnalysisWorkspace workspace = resolved.Workspace;
        ISymbol normalizedAnchor = NormalizeGraphSymbol(SolutionSearchScope.NormalizePositionalRecordProperty(resolved.Symbols[0]));
        SearchScope scope = await SolutionSearchScope.ResolveForSolutionAsync(
            workspace.Solution!,
            anchorProject: null,
            target: null,
            includeGenerated,
            cancellationToken).ConfigureAwait(false);
        CallGraph graph = CallGraph.Create(scope, includeObjectCreations, includeExternal, contextLines, brief);

        (string? declarationFilePath, int? declarationLine, int? declarationColumn) = SymbolQueryResolution.GetPrimaryDeclarationLocation(normalizedAnchor);
        return BuildHierarchyResult(
            normalizedAnchor,
            scope,
            declarationFilePath ?? workspace.ResolvedWorkspacePath,
            declarationLine,
            declarationColumn,
            symbolQuery,
            direction,
            maxDepth,
            maxNodes,
            maxEdges,
            contextLines,
            brief,
            includeObjectCreations,
            includeExternal,
            includeGenerated,
            workspacePath ?? workspace.ResolvedWorkspacePath,
            workspaceHandle,
            requireWorkspace,
            workspaceContext: null,
            graph,
            cancellationToken);
    }

    private static CommandExecutionResult BuildHierarchyResult(
        ISymbol normalizedAnchor,
        SearchScope scope,
        string anchorFilePath,
        int? anchorLine,
        int? anchorColumn,
        string? symbolQuery,
        CallHierarchyDirection direction,
        int maxDepth,
        int maxNodes,
        int maxEdges,
        int contextLines,
        bool brief,
        bool includeObjectCreations,
        bool includeExternal,
        bool includeGenerated,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        object? workspaceContext,
        CallGraph graph,
        CancellationToken cancellationToken)
    {
        string anchorId = graph.GetOrAddNode(normalizedAnchor).symbol_id;
        if (anchorLine.HasValue && anchorColumn.HasValue)
        {
            graph.TryUpdateNodeSourceLocation(anchorId, anchorFilePath, anchorLine.Value, anchorColumn.Value);
        }

        HashSet<string> selectedNodes = new(StringComparer.Ordinal) { anchorId };
        Dictionary<string, int> nodeDepths = new(StringComparer.Ordinal) { [anchorId] = 0 };
        Dictionary<int, List<string>> levels = new() { [0] = new() { anchorId } };
        Dictionary<string, CallHierarchyEdge> selectedEdgesByKey = new(StringComparer.Ordinal);
        Queue<(string nodeId, int depth)> frontier = new();
        frontier.Enqueue((anchorId, 0));
        bool truncated = false;

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string currentNodeId, int currentDepth) = frontier.Dequeue();
            if (currentDepth >= maxDepth)
            {
                continue;
            }

            foreach (CallHierarchyEdge edge in EnumerateTraversalEdges(graph, currentNodeId, direction, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (selectedEdgesByKey.Count >= maxEdges)
                {
                    truncated = true;
                    break;
                }

                selectedEdgesByKey.TryAdd(edge.edge_key, edge);
                string neighborId = GetNeighborNodeId(edge, currentNodeId, direction);

                if (!selectedNodes.Contains(neighborId))
                {
                    if (selectedNodes.Count >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }

                    selectedNodes.Add(neighborId);
                    int neighborDepth = currentDepth + 1;
                    nodeDepths[neighborId] = neighborDepth;
                    if (!levels.TryGetValue(neighborDepth, out List<string>? layer))
                    {
                        layer = new List<string>();
                        levels[neighborDepth] = layer;
                    }

                    layer.Add(neighborId);
                    frontier.Enqueue((neighborId, neighborDepth));
                }
            }

            if (truncated)
            {
                break;
            }
        }

        IEnumerable<CallHierarchyNode> orderedNodes = selectedNodes
            .Select(nodeId => graph.nodesById[nodeId])
            .OrderBy(node => nodeDepths.TryGetValue(node.symbol_id, out int depth) ? depth : int.MaxValue)
            .ThenBy(node => node.symbol_display, StringComparer.Ordinal);

        IEnumerable<CallHierarchyEdge> orderedEdges = selectedEdgesByKey.Values
            .Where(edge => selectedNodes.Contains(edge.from_symbol_id) && selectedNodes.Contains(edge.to_symbol_id))
            .OrderBy(edge => edge.file_path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.line)
            .ThenBy(edge => edge.column);

        object nodePayload = brief
            ? orderedNodes.Select(node => new
            {
                node.symbol_id,
                node.symbol_display,
                node.symbol_kind,
                node.method_kind,
                node.is_external,
                node.file_path,
                node.line,
                node.column,
                depth = nodeDepths.TryGetValue(node.symbol_id, out int depth) ? depth : 0,
            }).ToArray()
            : orderedNodes;

        object edgePayload = brief
            ? orderedEdges.Select(edge => new
            {
                edge.from_symbol_id,
                edge.to_symbol_id,
                edge.call_kind,
                edge.via_lambda,
                edge.implemented_member,
                edge.file_path,
                edge.line,
                edge.column,
            }).ToArray()
            : orderedEdges;

        object levelPayload = levels
            .OrderBy(pair => pair.Key)
            .Select(pair => new
            {
                depth = pair.Key,
                node_count = pair.Value.Count,
                symbol_ids = pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            })
            .ToArray();

        object queryPayload = symbolQuery is not null
            ? new
            {
                symbol = symbolQuery,
                line = anchorLine,
                column = anchorColumn,
                direction = DirectionToString(direction),
                max_depth = maxDepth,
                max_nodes = maxNodes,
                max_edges = maxEdges,
                context_lines = contextLines,
                brief,
                include_object_creations = includeObjectCreations,
                include_external = includeExternal,
                include_generated = includeGenerated,
                workspace_path = workspacePath,
                workspace_handle = workspaceHandle,
                require_workspace = requireWorkspace,
            }
            : new
            {
                line = anchorLine,
                column = anchorColumn,
                direction = DirectionToString(direction),
                max_depth = maxDepth,
                max_nodes = maxNodes,
                max_edges = maxEdges,
                context_lines = contextLines,
                brief,
                include_object_creations = includeObjectCreations,
                include_external = includeExternal,
                include_generated = includeGenerated,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = workspaceContext,
            };

        object data = new
        {
            file_path = anchorFilePath,
            query = queryPayload,
            symbol = new
            {
                symbol_display = normalizedAnchor.ToDisplayString(),
                symbol_kind = normalizedAnchor.Kind.ToString(),
                symbol_id = anchorId,
                method_kind = (normalizedAnchor as IMethodSymbol)?.MethodKind.ToString(),
            },
            scanned_syntax_trees = graph.scannedSyntaxTreeCount,
            search = scope.ToPayload(),
            total_nodes = selectedNodes.Count,
            total_edges = selectedEdgesByKey.Count,
            truncated,
            levels = levelPayload,
            nodes = nodePayload,
            edges = edgePayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static IEnumerable<CallHierarchyEdge> EnumerateTraversalEdges(CallGraph graph, string nodeId, CallHierarchyDirection direction, CancellationToken cancellationToken)
    {
        if (direction is CallHierarchyDirection.Outgoing or CallHierarchyDirection.Both)
        {
            foreach (CallHierarchyEdge edge in graph.GetOutgoingEdges(nodeId, cancellationToken))
            {
                yield return edge;
            }
        }

        if (direction is CallHierarchyDirection.Incoming or CallHierarchyDirection.Both)
        {
            foreach (CallHierarchyEdge edge in graph.GetIncomingEdges(nodeId, cancellationToken))
            {
                yield return edge;
            }
        }
    }

    private static string GetNeighborNodeId(CallHierarchyEdge edge, string currentNodeId, CallHierarchyDirection direction)
    {
        if (direction == CallHierarchyDirection.Incoming)
        {
            return edge.from_symbol_id;
        }

        if (direction == CallHierarchyDirection.Outgoing)
        {
            return edge.to_symbol_id;
        }

        return string.Equals(edge.from_symbol_id, currentNodeId, StringComparison.Ordinal)
            ? edge.to_symbol_id
            : edge.from_symbol_id;
    }

    private static ISymbol NormalizeGraphSymbol(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            return (method.ReducedFrom ?? method).OriginalDefinition;
        }

        return symbol.OriginalDefinition;
    }

    private static bool IsInSource(ISymbol symbol)
        => symbol.Locations.Any(location => location.IsInSource);

    private static CallHierarchyEdge? CreateEdge(
        HashSet<string> edgeKeys,
        CallHierarchyNode callerNode,
        CallHierarchyNode calleeNode,
        string callKind,
        bool viaLambda,
        TextSpan callSpan,
        SourceText sourceText,
        string filePath,
        int contextLines,
        bool brief,
        string? implementedMember)
    {
        LinePositionSpan lineSpan = sourceText.Lines.GetLinePositionSpan(callSpan);
        int line = lineSpan.Start.Line + 1;
        int column = lineSpan.Start.Character + 1;
        string snippet = brief ? string.Empty : CommandTextFormatting.BuildSnippet(sourceText, line, contextLines);

        string edgeKey = $"{callerNode.symbol_id}|{calleeNode.symbol_id}|{filePath}|{line}|{column}|{callKind}";
        if (!edgeKeys.Add(edgeKey))
        {
            return null;
        }

        return new CallHierarchyEdge(
            edge_key: edgeKey,
            from_symbol_id: callerNode.symbol_id,
            to_symbol_id: calleeNode.symbol_id,
            call_kind: callKind,
            via_lambda: viaLambda,
            file_path: filePath,
            line: line,
            column: column,
            snippet: snippet,
            implemented_member: implementedMember);
    }

    /// <summary>
    /// The interface methods <paramref name="method"/> implements (explicitly or implicitly) in its containing type.
    /// A call through such an interface member reaches the implementation, so it is an incoming call of the
    /// implementation (<c>854z</c>).
    /// </summary>
    private static IReadOnlyList<IMethodSymbol> GetImplementedInterfaceMethods(IMethodSymbol method)
    {
        INamedTypeSymbol? containingType = method.ContainingType;
        if (containingType is null ||
            method.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation) ||
            method.IsStatic)
        {
            return Array.Empty<IMethodSymbol>();
        }

        List<IMethodSymbol> implemented = new();
        foreach (IMethodSymbol explicitImplementation in method.ExplicitInterfaceImplementations)
        {
            implemented.Add(explicitImplementation);
        }

        if (method.MethodKind == MethodKind.ExplicitInterfaceImplementation)
        {
            return implemented;
        }

        foreach (INamedTypeSymbol iface in containingType.AllInterfaces)
        {
            foreach (ISymbol member in iface.GetMembers(method.Name))
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } interfaceMethod &&
                    SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(interfaceMethod), method))
                {
                    implemented.Add(interfaceMethod);
                }
            }
        }

        return implemented;
    }

    private static CallHierarchyNode GetOrAddNode(Dictionary<string, CallHierarchyNode> nodesById, ISymbol symbol)
    {
        string symbolId = SymbolResolution.GetUniqueGraphSymbolId(symbol);
        if (nodesById.TryGetValue(symbolId, out CallHierarchyNode? existing))
        {
            return existing;
        }

        Location? sourceLocation = SymbolResolution.GetDeclarationIdentifierLocation(symbol);
        if (sourceLocation is not { IsInSource: true })
        {
            sourceLocation = null;
        }

        string? filePath = sourceLocation?.SourceTree?.FilePath;
        int? line = null;
        int? column = null;
        if (sourceLocation is not null)
        {
            FileLinePositionSpan span = sourceLocation.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }

        CallHierarchyNode created = new(
            symbol_id: symbolId,
            symbol_display: symbol.ToDisplayString(),
            symbol_kind: symbol.Kind.ToString(),
            method_kind: (symbol as IMethodSymbol)?.MethodKind.ToString(),
            containing_type: symbol.ContainingType?.ToDisplayString(),
            is_external: !IsInSource(symbol),
            file_path: string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath),
            line: line,
            column: column);
        nodesById[symbolId] = created;
        return created;
    }

    private static bool TryParseDirection(JsonElement input, out CallHierarchyDirection direction)
    {
        if (!input.TryGetProperty("direction", out JsonElement directionProperty) ||
            directionProperty.ValueKind != JsonValueKind.String)
        {
            direction = CallHierarchyDirection.Both;
            return true;
        }

        string value = (directionProperty.GetString() ?? string.Empty).Trim();
        if (string.Equals(value, "incoming", StringComparison.OrdinalIgnoreCase))
        {
            direction = CallHierarchyDirection.Incoming;
            return true;
        }

        if (string.Equals(value, "outgoing", StringComparison.OrdinalIgnoreCase))
        {
            direction = CallHierarchyDirection.Outgoing;
            return true;
        }

        if (string.Equals(value, "both", StringComparison.OrdinalIgnoreCase))
        {
            direction = CallHierarchyDirection.Both;
            return true;
        }

        direction = CallHierarchyDirection.Both;
        return false;
    }

    private static string DirectionToString(CallHierarchyDirection direction)
        => direction switch
        {
            CallHierarchyDirection.Incoming => "incoming",
            CallHierarchyDirection.Outgoing => "outgoing",
            _ => "both",
        };

    private enum CallHierarchyDirection
    {
        Incoming = 0,
        Outgoing = 1,
        Both = 2,
    }

    /// <summary>
    /// A call graph discovered lazily, one frontier node at a time, instead of materialising every call site of the
    /// solution. Incoming edges of a node look only at trees whose text can call it (<see cref="CallSiteLookup"/>);
    /// outgoing edges of a node look only at the trees that declare it. Discovery of a node's edges is deterministic and
    /// yields the same edges, in the same per-node order, as filtering a whole-solution graph.
    /// </summary>
    private sealed class CallGraph
    {
        private readonly SearchScope scope;
        private readonly bool includeObjectCreations;
        private readonly bool includeExternal;
        private readonly int contextLines;
        private readonly bool brief;
        private readonly Dictionary<string, ISymbol> symbolsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CallHierarchyEdge>> incomingCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CallHierarchyEdge>> outgoingCache = new(StringComparer.Ordinal);
        private readonly Dictionary<SyntaxTree, SourceText> sourceTexts = new();
        private readonly HashSet<SyntaxTree> scannedTrees = new();
        private IReadOnlyList<Compilation>? aliasScope;

        private CallGraph(SearchScope scope, bool includeObjectCreations, bool includeExternal, int contextLines, bool brief)
        {
            this.scope = scope;
            this.includeObjectCreations = includeObjectCreations;
            this.includeExternal = includeExternal;
            this.contextLines = contextLines;
            this.brief = brief;
        }

        public Dictionary<string, CallHierarchyNode> nodesById { get; } = new(StringComparer.Ordinal);

        /// <summary>Number of distinct syntax trees looked at so far.</summary>
        public int scannedSyntaxTreeCount => scannedTrees.Count;

        public static CallGraph Create(SearchScope scope, bool includeObjectCreations, bool includeExternal, int contextLines, bool brief)
            => new(scope, includeObjectCreations, includeExternal, contextLines, brief);

        public CallHierarchyNode GetOrAddNode(ISymbol symbol)
        {
            CallHierarchyNode node = CallHierarchyCommand.GetOrAddNode(nodesById, symbol);
            symbolsById.TryAdd(node.symbol_id, symbol);
            return node;
        }

        /// <summary>Calls made by the code of the node: edges whose caller is the node.</summary>
        public IReadOnlyList<CallHierarchyEdge> GetOutgoingEdges(string nodeId, CancellationToken cancellationToken)
        {
            if (outgoingCache.TryGetValue(nodeId, out List<CallHierarchyEdge>? cached))
            {
                return cached;
            }

            List<CallHierarchyEdge> edges = new();
            outgoingCache[nodeId] = edges;
            if (!symbolsById.TryGetValue(nodeId, out ISymbol? symbol))
            {
                return edges;
            }

            HashSet<SyntaxTree> declaringTrees = GetDeclaringTrees(symbol);
            if (declaringTrees.Count == 0)
            {
                return edges;
            }

            HashSet<string> edgeKeys = new(StringComparer.Ordinal);
            foreach (SearchUnit unit in scope.Units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!declaringTrees.Contains(unit.Tree))
                {
                    continue;
                }

                scannedTrees.Add(unit.Tree);
                SourceText? sourceText = null;
                foreach (CallSiteAnalysis.CallSite callSite in GetOutgoingCandidateSites(symbol, unit, cancellationToken))
                {
                    if (callSite.caller is null)
                    {
                        continue;
                    }

                    ISymbol caller = NormalizeGraphSymbol(callSite.caller);
                    if (!string.Equals(SymbolResolution.GetUniqueGraphSymbolId(caller), nodeId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    IMethodSymbol callee = (IMethodSymbol)NormalizeGraphSymbol(callSite.callee);
                    if (!includeExternal && (!IsInSource(caller) || !IsInSource(callee)))
                    {
                        continue;
                    }

                    sourceText ??= GetSourceText(unit.Tree, cancellationToken);
                    CallHierarchyEdge? edge = CreateEdge(
                        edgeKeys,
                        GetNode(caller),
                        GetNode(callee),
                        callSite.call_kind,
                        callSite.via_lambda,
                        callSite.node.Span,
                        sourceText,
                        unit.FilePath,
                        contextLines,
                        brief,
                        implementedMember: null);
                    if (edge is not null)
                    {
                        edges.Add(edge);
                    }
                }
            }

            return edges;
        }

        /// <summary>
        /// Calls of the node: edges whose callee is the node, or (for an interface implementation) an interface member
        /// the node implements. Only trees that can spell a call to one of those methods are examined.
        /// </summary>
        public IReadOnlyList<CallHierarchyEdge> GetIncomingEdges(string nodeId, CancellationToken cancellationToken)
        {
            if (incomingCache.TryGetValue(nodeId, out List<CallHierarchyEdge>? cached))
            {
                return cached;
            }

            List<CallHierarchyEdge> edges = new();
            incomingCache[nodeId] = edges;

            // Only methods are callees of a call site; a property/field/event node has no incoming calls.
            if (!symbolsById.TryGetValue(nodeId, out ISymbol? symbol) || symbol is not IMethodSymbol method)
            {
                return edges;
            }

            // Callee id -> the interface member it was called as (null for a direct call of the node itself).
            Dictionary<string, IMethodSymbol?> acceptedCalleeIds = new(StringComparer.Ordinal) { [nodeId] = null };
            List<ISymbol> targets = new() { method };
            foreach (IMethodSymbol interfaceMethod in GetImplementedInterfaceMethods(method))
            {
                string interfaceId = SymbolResolution.GetUniqueGraphSymbolId(NormalizeGraphSymbol(interfaceMethod));
                if (acceptedCalleeIds.TryAdd(interfaceId, interfaceMethod))
                {
                    targets.Add(interfaceMethod);
                }
            }

            aliasScope ??= CallSiteLookup.GetCompilations(scope.Units);
            CallSiteLookup lookup = CallSiteLookup.ForTargets(targets, aliasScope);
            HashSet<string> edgeKeys = new(StringComparer.Ordinal);

            foreach (SearchUnit unit in scope.Units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lookup.MightMention(unit))
                {
                    continue;
                }

                scannedTrees.Add(unit.Tree);
                SourceText? sourceText = null;
                foreach (CallSiteAnalysis.CallSite callSite in CallSiteAnalysis.GetCallSitesForLookup(
                             unit.Compilation,
                             unit.Tree,
                             lookup,
                             includeObjectCreations,
                             cancellationToken))
                {
                    if (callSite.caller is null)
                    {
                        continue;
                    }

                    IMethodSymbol callee = (IMethodSymbol)NormalizeGraphSymbol(callSite.callee);
                    string calleeId = SymbolResolution.GetUniqueGraphSymbolId(callee);
                    if (!acceptedCalleeIds.TryGetValue(calleeId, out IMethodSymbol? viaInterface))
                    {
                        continue;
                    }

                    ISymbol caller = NormalizeGraphSymbol(callSite.caller);
                    if (!includeExternal && (!IsInSource(caller) || !IsInSource(method)))
                    {
                        continue;
                    }

                    sourceText ??= GetSourceText(unit.Tree, cancellationToken);
                    CallHierarchyEdge? edge = CreateEdge(
                        edgeKeys,
                        GetNode(caller),
                        GetNode(method),
                        callSite.call_kind,
                        callSite.via_lambda,
                        callSite.node.Span,
                        sourceText,
                        unit.FilePath,
                        contextLines,
                        brief,
                        implementedMember: viaInterface?.ToDisplayString());
                    if (edge is not null)
                    {
                        edges.Add(edge);
                    }
                }
            }

            return edges;
        }

        /// <summary>
        /// The call sites of <paramref name="unit"/> that can belong to <paramref name="symbol"/>'s own code. A warm tree
        /// answers from its cache; a cold tree binds just the symbol's declaration (method, constructor, accessor, local
        /// function) when every declaration of the symbol in that tree is one, and the whole tree otherwise.
        /// </summary>
        private IReadOnlyList<CallSiteAnalysis.CallSite> GetOutgoingCandidateSites(
            ISymbol symbol,
            SearchUnit unit,
            CancellationToken cancellationToken)
        {
            if (symbol is IMethodSymbol method && !CallSiteAnalysis.IsCached(unit.Compilation, unit.Tree))
            {
                List<SyntaxNode> declarations = new();
                bool selfContained = true;
                foreach (ISymbol? part in new ISymbol?[] { method, method.PartialImplementationPart, method.PartialDefinitionPart })
                {
                    if (part is null)
                    {
                        continue;
                    }

                    foreach (SyntaxReference reference in part.DeclaringSyntaxReferences)
                    {
                        if (reference.SyntaxTree != unit.Tree)
                        {
                            continue;
                        }

                        SyntaxNode node = reference.GetSyntax(cancellationToken);
                        if (CallSiteAnalysis.IsSelfContainedDeclaration(node))
                        {
                            declarations.Add(node);
                        }
                        else
                        {
                            selfContained = false;
                        }
                    }
                }

                if (selfContained && declarations.Count > 0)
                {
                    return CallSiteAnalysis.GetCallSitesInDeclarations(
                        unit.Compilation,
                        unit.Tree,
                        declarations,
                        includeObjectCreations,
                        cancellationToken);
                }
            }

            return CallSiteAnalysis.GetCallSites(unit.Compilation, unit.Tree, includeObjectCreations, cancellationToken);
        }

        public void TryUpdateNodeSourceLocation(string nodeId, string filePath, int line, int column)
        {
            if (!nodesById.TryGetValue(nodeId, out CallHierarchyNode? node))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(node.file_path) && node.line.HasValue && node.column.HasValue)
            {
                return;
            }

            nodesById[nodeId] = node with
            {
                file_path = Path.GetFullPath(filePath),
                line = line,
                column = column,
            };
        }

        private CallHierarchyNode GetNode(ISymbol symbol) => GetOrAddNode(symbol);

        private SourceText GetSourceText(SyntaxTree tree, CancellationToken cancellationToken)
        {
            if (!sourceTexts.TryGetValue(tree, out SourceText? text))
            {
                text = tree.GetText(cancellationToken);
                sourceTexts[tree] = text;
            }

            return text;
        }

        /// <summary>The trees holding the code of <paramref name="symbol"/> (all parts of a partial method).</summary>
        private static HashSet<SyntaxTree> GetDeclaringTrees(ISymbol symbol)
        {
            HashSet<SyntaxTree> trees = new();
            void AddFrom(ISymbol? candidate)
            {
                if (candidate is null)
                {
                    return;
                }

                foreach (SyntaxReference reference in candidate.DeclaringSyntaxReferences)
                {
                    trees.Add(reference.SyntaxTree);
                }
            }

            AddFrom(symbol);
            if (symbol is IMethodSymbol method)
            {
                AddFrom(method.PartialImplementationPart);
                AddFrom(method.PartialDefinitionPart);
            }

            return trees;
        }
    }

    private sealed record CallHierarchyNode(
        string symbol_id,
        string symbol_display,
        string symbol_kind,
        string? method_kind,
        string? containing_type,
        bool is_external,
        string? file_path,
        int? line,
        int? column);

    private sealed record CallHierarchyEdge(
        string edge_key,
        string from_symbol_id,
        string to_symbol_id,
        string call_kind,
        bool via_lambda,
        string file_path,
        int line,
        int column,
        string snippet,
        string? implemented_member);
}
