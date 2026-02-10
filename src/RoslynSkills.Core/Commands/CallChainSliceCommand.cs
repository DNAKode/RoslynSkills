using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class CallChainSliceCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.call_chain_slice",
        Summary: "Return a call-chain slice around a method symbol with bounded depth.",
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

        int depth = InputParsing.GetOptionalInt(input, "depth", defaultValue: 2, minValue: 1, maxValue: 8);
        int maxNodes = InputParsing.GetOptionalInt(input, "max_nodes", defaultValue: 200, minValue: 1, maxValue: 2_000);
        string direction = GetDirection(input);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        ISymbol? anchorSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        if (anchorSymbol is not IMethodSymbol anchorMethod)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "Call-chain slice requires a method symbol target.") });
        }

        string anchorId = CommandTextFormatting.GetStableSymbolId(anchorMethod) ?? anchorMethod.ToDisplayString();
        Dictionary<string, NodeInfo> nodesById = new(StringComparer.Ordinal);
        List<EdgeInfo> edges = new();

        foreach (MethodDeclarationSyntax declaration in analysis.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMethodSymbol? caller = analysis.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
            if (caller is null)
            {
                continue;
            }

            string callerId = CommandTextFormatting.GetStableSymbolId(caller) ?? caller.ToDisplayString();
            AddNode(nodesById, callerId, caller, declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

            foreach (InvocationExpressionSyntax invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                IMethodSymbol? callee = analysis.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                if (callee is null)
                {
                    continue;
                }

                string calleeId = CommandTextFormatting.GetStableSymbolId(callee) ?? callee.ToDisplayString();
                AddNode(nodesById, calleeId, callee, GetLine(analysis, invocation.SpanStart));
                edges.Add(new EdgeInfo(callerId, calleeId, GetLine(analysis, invocation.SpanStart), GetColumn(analysis, invocation.SpanStart)));
            }
        }

        HashSet<string> selected = SelectSlice(anchorId, edges, depth, maxNodes, direction);
        if (!selected.Contains(anchorId))
        {
            selected.Add(anchorId);
            AddNode(nodesById, anchorId, anchorMethod, line);
        }

        NodeInfo[] nodes = selected
            .Where(nodesById.ContainsKey)
            .Select(id => nodesById[id])
            .OrderBy(n => n.symbol_display, StringComparer.Ordinal)
            .ToArray();

        EdgeInfo[] sliceEdges = edges
            .Where(e => selected.Contains(e.from_symbol_id) && selected.Contains(e.to_symbol_id))
            .ToArray();

        object data = new
        {
            file_path = filePath,
            anchor_symbol_id = anchorId,
            direction,
            depth,
            node_count = nodes.Length,
            edge_count = sliceEdges.Length,
            nodes,
            edges = sliceEdges,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static int GetLine(CommandFileAnalysis analysis, int position)
        => analysis.SourceText.Lines.GetLineFromPosition(position).LineNumber + 1;

    private static int GetColumn(CommandFileAnalysis analysis, int position)
        => position - analysis.SourceText.Lines.GetLineFromPosition(position).Start + 1;

    private static string GetDirection(JsonElement input)
    {
        if (!input.TryGetProperty("direction", out JsonElement directionProperty) || directionProperty.ValueKind != JsonValueKind.String)
        {
            return "both";
        }

        string? value = directionProperty.GetString();
        if (string.Equals(value, "inbound", StringComparison.OrdinalIgnoreCase))
        {
            return "inbound";
        }

        if (string.Equals(value, "outbound", StringComparison.OrdinalIgnoreCase))
        {
            return "outbound";
        }

        return "both";
    }

    private static void AddNode(Dictionary<string, NodeInfo> nodesById, string id, ISymbol symbol, int line)
    {
        if (nodesById.ContainsKey(id))
        {
            return;
        }

        nodesById[id] = new NodeInfo(
            symbol_id: id,
            symbol_display: symbol.ToDisplayString(),
            symbol_kind: symbol.Kind.ToString(),
            line: line,
            containing_type: symbol.ContainingType?.ToDisplayString(),
            is_external: symbol.Locations.All(l => !l.IsInSource));
    }

    private static HashSet<string> SelectSlice(
        string anchorId,
        IReadOnlyList<EdgeInfo> edges,
        int maxDepth,
        int maxNodes,
        string direction)
    {
        Dictionary<string, List<string>> outgoing = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> incoming = new(StringComparer.Ordinal);

        foreach (EdgeInfo edge in edges)
        {
            if (!outgoing.TryGetValue(edge.from_symbol_id, out List<string>? outList))
            {
                outList = new List<string>();
                outgoing[edge.from_symbol_id] = outList;
            }

            outList.Add(edge.to_symbol_id);

            if (!incoming.TryGetValue(edge.to_symbol_id, out List<string>? inList))
            {
                inList = new List<string>();
                incoming[edge.to_symbol_id] = inList;
            }

            inList.Add(edge.from_symbol_id);
        }

        HashSet<string> selected = new(StringComparer.Ordinal) { anchorId };
        Queue<(string node, int depth)> queue = new();
        queue.Enqueue((anchorId, 0));

        while (queue.Count > 0 && selected.Count < maxNodes)
        {
            (string node, int depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> neighbors = direction switch
            {
                "inbound" => incoming.TryGetValue(node, out List<string>? inList) ? inList : Enumerable.Empty<string>(),
                "outbound" => outgoing.TryGetValue(node, out List<string>? outList) ? outList : Enumerable.Empty<string>(),
                _ => (outgoing.TryGetValue(node, out List<string>? outBoth) ? outBoth : Enumerable.Empty<string>())
                    .Concat(incoming.TryGetValue(node, out List<string>? inBoth) ? inBoth : Enumerable.Empty<string>()),
            };

            foreach (string neighbor in neighbors)
            {
                if (selected.Add(neighbor))
                {
                    queue.Enqueue((neighbor, depth + 1));
                    if (selected.Count >= maxNodes)
                    {
                        break;
                    }
                }
            }
        }

        return selected;
    }

    private sealed record NodeInfo(
        string symbol_id,
        string symbol_display,
        string symbol_kind,
        int line,
        string? containing_type,
        bool is_external);

    private sealed record EdgeInfo(
        string from_symbol_id,
        string to_symbol_id,
        int line,
        int column);
}

