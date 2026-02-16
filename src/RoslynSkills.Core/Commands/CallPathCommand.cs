using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class CallPathCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.call_path",
        Summary: "Find a shortest call path between two method anchors using workspace semantic analysis.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Experimental,
        Traits: [CommandTrait.Heuristic, CommandTrait.DerivedAnalysis, CommandTrait.PotentiallySlow]);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "source_file_path", errors, out string sourceFilePath))
        {
            return errors;
        }

        InputParsing.TryGetRequiredInt(input, "source_line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "source_column", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredString(input, "target_file_path", errors, out string targetFilePath);
        InputParsing.TryGetRequiredInt(input, "target_line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "target_column", errors, out _, minValue: 1, maxValue: 1_000_000);

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalBool(input, "include_object_creations", errors);
        InputParsing.ValidateOptionalBool(input, "include_external", errors);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);

        if (!File.Exists(sourceFilePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input source file '{sourceFilePath}' does not exist."));
        }

        if (!File.Exists(targetFilePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input target file '{targetFilePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "source_file_path", errors, out string sourceFilePath) ||
            !InputParsing.TryGetRequiredInt(input, "source_line", errors, out int sourceLine, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "source_column", errors, out int sourceColumn, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredString(input, "target_file_path", errors, out string targetFilePath) ||
            !InputParsing.TryGetRequiredInt(input, "target_line", errors, out int targetLine, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "target_column", errors, out int targetColumn, minValue: 1, maxValue: 1_000_000))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(sourceFilePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input source file '{sourceFilePath}' does not exist.") });
        }

        if (!File.Exists(targetFilePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input target file '{targetFilePath}' does not exist.") });
        }

        int maxDepth = InputParsing.GetOptionalInt(input, "max_depth", defaultValue: 8, minValue: 1, maxValue: 40);
        int maxNodes = InputParsing.GetOptionalInt(input, "max_nodes", defaultValue: 2_000, minValue: 1, maxValue: 50_000);
        int maxGraphEdges = InputParsing.GetOptionalInt(input, "max_graph_edges", defaultValue: 30_000, minValue: 1, maxValue: 200_000);
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 1, minValue: 0, maxValue: 10);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        bool includeObjectCreations = InputParsing.GetOptionalBool(input, "include_object_creations", defaultValue: true);
        bool includeExternal = InputParsing.GetOptionalBool(input, "include_external", defaultValue: false);
        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        CommandFileAnalysis sourceAnalysis = await CommandFileAnalysis.LoadAsync(sourceFilePath, cancellationToken, workspacePath).ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, sourceAnalysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

        (bool SourceOk, IMethodSymbol? Method, string? ErrorMessage) sourceResolution = await ResolveMethodAnchorAsync(
            sourceAnalysis.Compilation,
            sourceFilePath,
            sourceLine,
            sourceColumn,
            cancellationToken).ConfigureAwait(false);
        if (!sourceResolution.SourceOk || sourceResolution.Method is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_source_target", sourceResolution.ErrorMessage ?? "Could not resolve source method anchor.") });
        }

        (bool SourceOk, IMethodSymbol? Method, string? ErrorMessage) targetResolution = await ResolveMethodAnchorAsync(
            sourceAnalysis.Compilation,
            targetFilePath,
            targetLine,
            targetColumn,
            cancellationToken).ConfigureAwait(false);
        if (!targetResolution.SourceOk || targetResolution.Method is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", targetResolution.ErrorMessage ?? "Could not resolve target method anchor.") });
        }

        CallGraph graph = await BuildCallGraphAsync(
            sourceAnalysis.Compilation,
            includeObjectCreations,
            includeExternal,
            includeGenerated,
            contextLines,
            brief,
            maxGraphEdges,
            cancellationToken).ConfigureAwait(false);

        string sourceId = graph.GetOrAddNode(sourceResolution.Method).symbol_id;
        string targetId = graph.GetOrAddNode(targetResolution.Method).symbol_id;
        graph.TryUpdateNodeSourceLocation(sourceId, sourceFilePath, sourceLine, sourceColumn);
        graph.TryUpdateNodeSourceLocation(targetId, targetFilePath, targetLine, targetColumn);

        PathSearchResult search = FindShortestPath(graph, sourceId, targetId, maxDepth, maxNodes, cancellationToken);
        IReadOnlyList<CallPathEdge> pathEdges = search.pathFound
            ? search.pathEdges
            : Array.Empty<CallPathEdge>();

        List<string> pathNodeIds = BuildPathNodeIds(sourceId, pathEdges);
        IEnumerable<CallPathNode> orderedPathNodes = pathNodeIds
            .Distinct(StringComparer.Ordinal)
            .Where(graph.nodesById.ContainsKey)
            .Select(id => graph.nodesById[id]);

        object pathNodePayload = brief
            ? orderedPathNodes.Select((node, index) => new
            {
                step = index,
                node.symbol_id,
                node.symbol_display,
                node.symbol_kind,
                node.method_kind,
                node.is_external,
                node.file_path,
                node.line,
                node.column,
            }).ToArray()
            : orderedPathNodes.ToArray();

        object pathEdgePayload = brief
            ? pathEdges.Select((edge, index) => new
            {
                step = index + 1,
                edge.from_symbol_id,
                edge.to_symbol_id,
                edge.call_kind,
                edge.file_path,
                edge.line,
                edge.column,
            }).ToArray()
            : pathEdges;

        string[] caveats = BuildCaveats(graph, search);
        object data = new
        {
            file_path = sourceAnalysis.FilePath,
            query = new
            {
                source_file_path = Path.GetFullPath(sourceFilePath),
                source_line = sourceLine,
                source_column = sourceColumn,
                target_file_path = Path.GetFullPath(targetFilePath),
                target_line = targetLine,
                target_column = targetColumn,
                max_depth = maxDepth,
                max_nodes = maxNodes,
                max_graph_edges = maxGraphEdges,
                context_lines = contextLines,
                brief,
                include_object_creations = includeObjectCreations,
                include_external = includeExternal,
                include_generated = includeGenerated,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(sourceAnalysis.WorkspaceContext),
            },
            source_symbol = BuildSymbolPayload(sourceResolution.Method, sourceId),
            target_symbol = BuildSymbolPayload(targetResolution.Method, targetId),
            scanned_syntax_trees = graph.scannedSyntaxTreeCount,
            total_nodes = graph.nodesById.Count,
            total_edges = graph.totalEdges,
            graph_truncated = graph.truncatedByEdgeLimit,
            path_found = search.pathFound,
            path_edge_length = pathEdges.Count,
            visited_nodes = search.visitedNodeCount,
            explored_edges = search.exploredEdgeCount,
            search_truncated = search.truncated,
            caveats,
            path_nodes = pathNodePayload,
            path_edges = pathEdgePayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static object BuildSymbolPayload(IMethodSymbol method, string symbolId)
        => new
        {
            symbol_display = method.ToDisplayString(),
            symbol_kind = method.Kind.ToString(),
            symbol_id = symbolId,
            method_kind = method.MethodKind.ToString(),
        };

    private static string[] BuildCaveats(CallGraph graph, PathSearchResult search)
    {
        List<string> caveats = new();
        if (graph.truncatedByEdgeLimit)
        {
            caveats.Add("Graph collection hit max_graph_edges; path results may be incomplete.");
        }

        if (search.truncated)
        {
            caveats.Add("Path search hit max_depth/max_nodes limits; deeper routes may exist.");
        }

        if (caveats.Count == 0)
        {
            caveats.Add("Call-path analysis is heuristic and may miss dynamic dispatch, reflection, or DI-only edges.");
        }

        return caveats.ToArray();
    }

    private static List<string> BuildPathNodeIds(string sourceId, IReadOnlyList<CallPathEdge> pathEdges)
    {
        List<string> nodeIds = new() { sourceId };
        foreach (CallPathEdge edge in pathEdges)
        {
            nodeIds.Add(edge.to_symbol_id);
        }

        return nodeIds;
    }

    private static async Task<(bool SourceOk, IMethodSymbol? Method, string? ErrorMessage)> ResolveMethodAnchorAsync(
        Compilation compilation,
        string filePath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        SyntaxTree? tree = compilation.SyntaxTrees.FirstOrDefault(t =>
            string.Equals(Path.GetFullPath(t.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (tree is null)
        {
            return (false, null, $"File '{normalizedPath}' is not in the active compilation. Use --workspace-path to load a shared workspace.");
        }

        SourceText sourceText = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (line > sourceText.Lines.Count)
        {
            return (false, null, $"Requested line '{line}' exceeds file line count ({sourceText.Lines.Count}) for '{normalizedPath}'.");
        }

        SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        int position = GetPosition(sourceText, line, column);
        SyntaxToken anchorToken = CommandFileAnalysis.FindAnchorToken(root, position);
        SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        ISymbol? symbol = SymbolResolution.GetSymbolForToken(anchorToken, semanticModel, cancellationToken);
        if (symbol is not IMethodSymbol method)
        {
            return (false, null, $"Symbol '{symbol?.ToDisplayString() ?? "<unknown>"}' at '{normalizedPath}:{line}:{column}' is not a method.");
        }

        return (true, method, null);
    }

    private static int GetPosition(SourceText sourceText, int line, int column)
    {
        TextLine textLine = sourceText.Lines[line - 1];
        int requestedOffset = Math.Max(0, column - 1);
        int maxOffset = Math.Max(0, textLine.Span.Length - 1);
        int clampedOffset = Math.Min(requestedOffset, maxOffset);
        return textLine.Start + clampedOffset;
    }

    private static async Task<CallGraph> BuildCallGraphAsync(
        Compilation compilation,
        bool includeObjectCreations,
        bool includeExternal,
        bool includeGenerated,
        int contextLines,
        bool brief,
        int maxGraphEdges,
        CancellationToken cancellationToken)
    {
        Dictionary<string, CallPathNode> nodesById = new(StringComparer.Ordinal);
        Dictionary<string, List<CallPathEdge>> outgoingEdgesByNodeId = new(StringComparer.Ordinal);
        HashSet<string> edgeKeys = new(StringComparer.Ordinal);
        int scannedSyntaxTreeCount = 0;
        bool truncatedByEdgeLimit = false;

        foreach (SyntaxTree syntaxTree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (edgeKeys.Count >= maxGraphEdges)
            {
                truncatedByEdgeLimit = true;
                break;
            }

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
                if (edgeKeys.Count >= maxGraphEdges)
                {
                    truncatedByEdgeLimit = true;
                    break;
                }

                if (!includeExternal && (!IsInSource(callSite.caller) || !IsInSource(callSite.callee)))
                {
                    continue;
                }

                AddEdge(
                    nodesById,
                    outgoingEdgesByNodeId,
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

        return new CallGraph(
            nodesById,
            outgoingEdgesByNodeId,
            scannedSyntaxTreeCount,
            edgeKeys.Count,
            truncatedByEdgeLimit);
    }

    private static PathSearchResult FindShortestPath(
        CallGraph graph,
        string sourceId,
        string targetId,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
        {
            return new PathSearchResult(
                pathFound: true,
                pathEdges: Array.Empty<CallPathEdge>(),
                visitedNodeCount: 1,
                exploredEdgeCount: 0,
                truncated: false);
        }

        Queue<string> queue = new();
        HashSet<string> visited = new(StringComparer.Ordinal) { sourceId };
        Dictionary<string, int> depths = new(StringComparer.Ordinal) { [sourceId] = 0 };
        Dictionary<string, CallPathEdge> predecessorEdges = new(StringComparer.Ordinal);
        bool found = false;
        bool truncated = false;
        int exploredEdges = 0;

        queue.Enqueue(sourceId);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = queue.Dequeue();
            int currentDepth = depths[current];
            if (currentDepth >= maxDepth)
            {
                continue;
            }

            if (!graph.outgoingEdgesByNodeId.TryGetValue(current, out List<CallPathEdge>? outgoing))
            {
                continue;
            }

            foreach (CallPathEdge edge in outgoing)
            {
                exploredEdges++;

                if (visited.Contains(edge.to_symbol_id))
                {
                    continue;
                }

                if (visited.Count >= maxNodes)
                {
                    truncated = true;
                    break;
                }

                visited.Add(edge.to_symbol_id);
                depths[edge.to_symbol_id] = currentDepth + 1;
                predecessorEdges[edge.to_symbol_id] = edge;

                if (string.Equals(edge.to_symbol_id, targetId, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }

                queue.Enqueue(edge.to_symbol_id);
            }

            if (found || truncated)
            {
                break;
            }
        }

        if (!found)
        {
            return new PathSearchResult(
                pathFound: false,
                pathEdges: Array.Empty<CallPathEdge>(),
                visitedNodeCount: visited.Count,
                exploredEdgeCount: exploredEdges,
                truncated: truncated);
        }

        List<CallPathEdge> pathEdges = new();
        string cursor = targetId;
        while (!string.Equals(cursor, sourceId, StringComparison.Ordinal) &&
               predecessorEdges.TryGetValue(cursor, out CallPathEdge? edge))
        {
            pathEdges.Add(edge);
            cursor = edge.from_symbol_id;
        }

        pathEdges.Reverse();
        return new PathSearchResult(
            pathFound: true,
            pathEdges: pathEdges,
            visitedNodeCount: visited.Count,
            exploredEdgeCount: exploredEdges,
            truncated: truncated);
    }

    private static bool IsInSource(ISymbol symbol)
        => symbol.Locations.Any(location => location.IsInSource);

    private static void AddEdge(
        Dictionary<string, CallPathNode> nodesById,
        Dictionary<string, List<CallPathEdge>> outgoingEdgesByNodeId,
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
        CallPathNode callerNode = GetOrAddNode(nodesById, caller);
        CallPathNode calleeNode = GetOrAddNode(nodesById, callee);

        LinePositionSpan lineSpan = sourceText.Lines.GetLinePositionSpan(callSpan);
        int line = lineSpan.Start.Line + 1;
        int column = lineSpan.Start.Character + 1;
        string snippet = brief ? string.Empty : CommandTextFormatting.BuildSnippet(sourceText, line, contextLines);

        string edgeKey = $"{callerNode.symbol_id}|{calleeNode.symbol_id}|{filePath}|{line}|{column}|{callKind}";
        if (!edgeKeys.Add(edgeKey))
        {
            return;
        }

        CallPathEdge edge = new(
            edge_key: edgeKey,
            from_symbol_id: callerNode.symbol_id,
            to_symbol_id: calleeNode.symbol_id,
            call_kind: callKind,
            file_path: filePath,
            line: line,
            column: column,
            snippet: snippet);

        AddAdjacency(outgoingEdgesByNodeId, callerNode.symbol_id, edge);
    }

    private static void AddAdjacency(
        Dictionary<string, List<CallPathEdge>> map,
        string key,
        CallPathEdge edge)
    {
        if (!map.TryGetValue(key, out List<CallPathEdge>? list))
        {
            list = new List<CallPathEdge>();
            map[key] = list;
        }

        list.Add(edge);
    }

    private static CallPathNode GetOrAddNode(Dictionary<string, CallPathNode> nodesById, IMethodSymbol methodSymbol)
    {
        string symbolId = CommandTextFormatting.GetStableSymbolId(methodSymbol)
            ?? methodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (nodesById.TryGetValue(symbolId, out CallPathNode? existing))
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

        CallPathNode created = new(
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

    private sealed class CallGraph
    {
        public Dictionary<string, CallPathNode> nodesById { get; }
        public Dictionary<string, List<CallPathEdge>> outgoingEdgesByNodeId { get; }
        public int scannedSyntaxTreeCount { get; }
        public int totalEdges { get; }
        public bool truncatedByEdgeLimit { get; }

        public CallGraph(
            Dictionary<string, CallPathNode> nodesById,
            Dictionary<string, List<CallPathEdge>> outgoingEdgesByNodeId,
            int scannedSyntaxTreeCount,
            int totalEdges,
            bool truncatedByEdgeLimit)
        {
            this.nodesById = nodesById;
            this.outgoingEdgesByNodeId = outgoingEdgesByNodeId;
            this.scannedSyntaxTreeCount = scannedSyntaxTreeCount;
            this.totalEdges = totalEdges;
            this.truncatedByEdgeLimit = truncatedByEdgeLimit;
        }

        public CallPathNode GetOrAddNode(IMethodSymbol methodSymbol)
            => CallPathCommand.GetOrAddNode(nodesById, methodSymbol);

        public void TryUpdateNodeSourceLocation(string nodeId, string filePath, int line, int column)
        {
            if (!nodesById.TryGetValue(nodeId, out CallPathNode? node))
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

    private sealed record PathSearchResult(
        bool pathFound,
        IReadOnlyList<CallPathEdge> pathEdges,
        int visitedNodeCount,
        int exploredEdgeCount,
        bool truncated);

    private sealed record CallPathNode(
        string symbol_id,
        string symbol_display,
        string symbol_kind,
        string method_kind,
        string? containing_type,
        bool is_external,
        string? file_path,
        int? line,
        int? column);

    private sealed record CallPathEdge(
        string edge_key,
        string from_symbol_id,
        string to_symbol_id,
        string call_kind,
        string file_path,
        int line,
        int column,
        string snippet);
}
