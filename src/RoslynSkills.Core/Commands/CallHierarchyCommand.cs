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
            summary: "Build incoming/outgoing call hierarchy for an anchored method with bounded depth.")
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
            Traits: [CommandTrait.Heuristic, CommandTrait.DerivedAnalysis, CommandTrait.PotentiallySlow]);
    }

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
        ISymbol? anchorSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        IMethodSymbol? anchorMethod = anchorSymbol as IMethodSymbol;
        if (anchorMethod is null)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError(
                        "invalid_target",
                        $"Symbol '{anchorSymbol?.ToDisplayString() ?? "<unknown>"}' is not a method. Anchor to a method declaration/reference and retry."),
                });
        }

        CallGraph graph = await BuildCallGraphAsync(
            analysis.Compilation,
            includeObjectCreations,
            includeExternal,
            includeGenerated,
            contextLines,
            brief,
            cancellationToken).ConfigureAwait(false);

        string anchorId = graph.GetOrAddNode(anchorMethod).symbol_id;
        graph.TryUpdateNodeSourceLocation(anchorId, analysis.FilePath, line, column);

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

            foreach (CallHierarchyEdge edge in EnumerateTraversalEdges(graph, currentNodeId, direction))
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

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
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
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            symbol = new
            {
                symbol_display = anchorMethod.ToDisplayString(),
                symbol_kind = anchorMethod.Kind.ToString(),
                symbol_id = anchorId,
                method_kind = anchorMethod.MethodKind.ToString(),
            },
            scanned_syntax_trees = graph.scannedSyntaxTreeCount,
            total_nodes = selectedNodes.Count,
            total_edges = selectedEdgesByKey.Count,
            truncated,
            levels = levelPayload,
            nodes = nodePayload,
            edges = edgePayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static IEnumerable<CallHierarchyEdge> EnumerateTraversalEdges(CallGraph graph, string nodeId, CallHierarchyDirection direction)
    {
        if (direction is CallHierarchyDirection.Outgoing or CallHierarchyDirection.Both)
        {
            if (graph.outgoingEdgesByNodeId.TryGetValue(nodeId, out List<CallHierarchyEdge>? outgoing))
            {
                foreach (CallHierarchyEdge edge in outgoing)
                {
                    yield return edge;
                }
            }
        }

        if (direction is CallHierarchyDirection.Incoming or CallHierarchyDirection.Both)
        {
            if (graph.incomingEdgesByNodeId.TryGetValue(nodeId, out List<CallHierarchyEdge>? incoming))
            {
                foreach (CallHierarchyEdge edge in incoming)
                {
                    yield return edge;
                }
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

    private static async Task<CallGraph> BuildCallGraphAsync(
        Compilation compilation,
        bool includeObjectCreations,
        bool includeExternal,
        bool includeGenerated,
        int contextLines,
        bool brief,
        CancellationToken cancellationToken)
    {
        Dictionary<string, CallHierarchyNode> nodesById = new(StringComparer.Ordinal);
        Dictionary<string, List<CallHierarchyEdge>> outgoingEdgesByNodeId = new(StringComparer.Ordinal);
        Dictionary<string, List<CallHierarchyEdge>> incomingEdgesByNodeId = new(StringComparer.Ordinal);
        HashSet<string> edgeKeys = new(StringComparer.Ordinal);
        int scannedSyntaxTreeCount = 0;

        foreach (SyntaxTree syntaxTree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string treeFilePath = string.IsNullOrWhiteSpace(syntaxTree.FilePath)
                ? "<unknown>"
                : Path.GetFullPath(syntaxTree.FilePath);

            if (!includeGenerated && CommandFileFilters.IsGeneratedPath(treeFilePath))
            {
                continue;
            }

            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
            SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SourceText sourceText = await syntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            scannedSyntaxTreeCount++;

            foreach (CallSiteAnalysis.CallSite callSite in CallSiteAnalysis.EnumerateCallSites(
                         semanticModel,
                         root,
                         includeObjectCreations,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!includeExternal && (!IsInSource(callSite.caller) || !IsInSource(callSite.callee)))
                {
                    continue;
                }

                AddEdge(
                    nodesById,
                    outgoingEdgesByNodeId,
                    incomingEdgesByNodeId,
                    edgeKeys,
                    callSite.caller,
                    callSite.callee,
                    callKind: callSite.call_kind,
                    callSite.node.Span,
                    sourceText,
                    treeFilePath,
                    contextLines,
                    brief);
            }
        }

        return new CallGraph(nodesById, outgoingEdgesByNodeId, incomingEdgesByNodeId, scannedSyntaxTreeCount);
    }

    private static bool IsInSource(ISymbol symbol)
        => symbol.Locations.Any(location => location.IsInSource);

    private static void AddEdge(
        Dictionary<string, CallHierarchyNode> nodesById,
        Dictionary<string, List<CallHierarchyEdge>> outgoingEdgesByNodeId,
        Dictionary<string, List<CallHierarchyEdge>> incomingEdgesByNodeId,
        HashSet<string> edgeKeys,
        IMethodSymbol caller,
        IMethodSymbol callee,
        string callKind,
        TextSpan callSpan,
        SourceText sourceText,
        string filePath,
        int contextLines,
        bool brief)
    {
        CallHierarchyNode callerNode = GetOrAddNode(nodesById, caller);
        CallHierarchyNode calleeNode = GetOrAddNode(nodesById, callee);

        LinePositionSpan lineSpan = sourceText.Lines.GetLinePositionSpan(callSpan);
        int line = lineSpan.Start.Line + 1;
        int column = lineSpan.Start.Character + 1;
        string snippet = brief ? string.Empty : CommandTextFormatting.BuildSnippet(sourceText, line, contextLines);

        string edgeKey = $"{callerNode.symbol_id}|{calleeNode.symbol_id}|{filePath}|{line}|{column}|{callKind}";
        if (!edgeKeys.Add(edgeKey))
        {
            return;
        }

        CallHierarchyEdge edge = new(
            edge_key: edgeKey,
            from_symbol_id: callerNode.symbol_id,
            to_symbol_id: calleeNode.symbol_id,
            call_kind: callKind,
            file_path: filePath,
            line: line,
            column: column,
            snippet: snippet);

        AddAdjacency(outgoingEdgesByNodeId, callerNode.symbol_id, edge);
        AddAdjacency(incomingEdgesByNodeId, calleeNode.symbol_id, edge);
    }

    private static void AddAdjacency(
        Dictionary<string, List<CallHierarchyEdge>> map,
        string key,
        CallHierarchyEdge edge)
    {
        if (!map.TryGetValue(key, out List<CallHierarchyEdge>? list))
        {
            list = new List<CallHierarchyEdge>();
            map[key] = list;
        }

        list.Add(edge);
    }

    private static CallHierarchyNode GetOrAddNode(Dictionary<string, CallHierarchyNode> nodesById, IMethodSymbol methodSymbol)
    {
        string symbolId = CommandTextFormatting.GetStableSymbolId(methodSymbol)
            ?? methodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (nodesById.TryGetValue(symbolId, out CallHierarchyNode? existing))
        {
            return existing;
        }

        Location? sourceLocation = methodSymbol.Locations.FirstOrDefault(location => location.IsInSource);
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
            symbol_display: methodSymbol.ToDisplayString(),
            symbol_kind: methodSymbol.Kind.ToString(),
            method_kind: methodSymbol.MethodKind.ToString(),
            containing_type: methodSymbol.ContainingType?.ToDisplayString(),
            is_external: !IsInSource(methodSymbol),
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

    private sealed class CallGraph
    {
        public Dictionary<string, CallHierarchyNode> nodesById { get; }
        public Dictionary<string, List<CallHierarchyEdge>> outgoingEdgesByNodeId { get; }
        public Dictionary<string, List<CallHierarchyEdge>> incomingEdgesByNodeId { get; }
        public int scannedSyntaxTreeCount { get; }

        public CallGraph(
            Dictionary<string, CallHierarchyNode> nodesById,
            Dictionary<string, List<CallHierarchyEdge>> outgoingEdgesByNodeId,
            Dictionary<string, List<CallHierarchyEdge>> incomingEdgesByNodeId,
            int scannedSyntaxTreeCount)
        {
            this.nodesById = nodesById;
            this.outgoingEdgesByNodeId = outgoingEdgesByNodeId;
            this.incomingEdgesByNodeId = incomingEdgesByNodeId;
            this.scannedSyntaxTreeCount = scannedSyntaxTreeCount;
        }

        public CallHierarchyNode GetOrAddNode(IMethodSymbol methodSymbol)
            => CallHierarchyCommand.GetOrAddNode(nodesById, methodSymbol);

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
    }

    private sealed record CallHierarchyNode(
        string symbol_id,
        string symbol_display,
        string symbol_kind,
        string method_kind,
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
        string file_path,
        int line,
        int column,
        string snippet);
}
