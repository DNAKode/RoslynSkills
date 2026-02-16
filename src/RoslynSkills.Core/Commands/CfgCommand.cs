using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class CfgCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "analyze.cfg",
        Summary: "Build a control-flow graph (CFG) summary for the executable region around a source location.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Stable,
        Traits: [CommandTrait.DerivedAnalysis]);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalInt(input, "max_blocks", errors, minValue: 1, maxValue: 20_000);
        InputParsing.ValidateOptionalInt(input, "max_edges", errors, minValue: 1, maxValue: 50_000);
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

        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        int maxBlocks = InputParsing.GetOptionalInt(input, "max_blocks", defaultValue: 200, minValue: 1, maxValue: 20_000);
        int maxEdges = InputParsing.GetOptionalInt(input, "max_edges", defaultValue: 500, minValue: 1, maxValue: 50_000);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath).ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, analysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

        if (!FlowAnalysisSupport.TryGetControlFlowGraph(
                analysis,
                line,
                column,
                cancellationToken,
                out ControlFlowGraph? cfg,
                out SyntaxNode? executableNode,
                out CommandError? cfgError))
        {
            return new CommandExecutionResult(null, new[] { cfgError ?? new CommandError("analysis_failed", "Unable to build control-flow graph.") });
        }

        if (cfg is null || executableNode is null)
        {
            return new CommandExecutionResult(null, new[] { new CommandError("analysis_failed", "Unable to build control-flow graph.") });
        }

        List<CfgBlockEntry> blockEntries = new(capacity: Math.Min(cfg!.Blocks.Length, maxBlocks));
        List<CfgEdgeEntry> edgeEntries = new(capacity: Math.Min(cfg.Blocks.Length * 2, maxEdges));
        HashSet<string> edgeKeys = new(StringComparer.Ordinal);

        foreach (BasicBlock block in cfg.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blockEntries.Count >= maxBlocks)
            {
                break;
            }

            int[] predecessorOrdinals = block.Predecessors
                .Select(branch => branch.Source.Ordinal)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();

            blockEntries.Add(new CfgBlockEntry(
                ordinal: block.Ordinal,
                kind: block.Kind.ToString(),
                is_reachable: block.IsReachable,
                operation_count: block.Operations.Length,
                condition_kind: block.ConditionKind.ToString(),
                predecessor_ordinals: predecessorOrdinals,
                operation_kinds: brief
                    ? Array.Empty<string>()
                    : block.Operations.Select(operation => operation.Kind.ToString()).ToArray(),
                operation_preview: brief
                    ? block.Operations.Select(operation => operation.Kind.ToString()).Take(8).ToArray()
                    : Array.Empty<string>()));

            TryAddEdge(block.FallThroughSuccessor, edgeEntries, edgeKeys, maxEdges);
            TryAddEdge(block.ConditionalSuccessor, edgeEntries, edgeKeys, maxEdges);
        }

        object blocksPayload = brief
            ? blockEntries.Select(entry => new
            {
                entry.ordinal,
                entry.kind,
                entry.is_reachable,
                entry.operation_count,
                entry.condition_kind,
                entry.predecessor_ordinals,
                entry.operation_preview,
            }).ToArray()
            : blockEntries.ToArray();

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                brief,
                max_blocks = maxBlocks,
                max_edges = maxEdges,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            executable_region = new
            {
                node_kind = CommandLanguageServices.GetSyntaxKindName(executableNode),
                line_span = BuildLineSpan(executableNode.GetLocation().GetLineSpan()),
                language = analysis.Language,
            },
            cfg_summary = new
            {
                total_blocks = cfg.Blocks.Length,
                returned_blocks = blockEntries.Count,
                total_edges = edgeEntries.Count,
                entry_block_ordinal = TryGetBlockOrdinal(cfg, BasicBlockKind.Entry),
                exit_block_ordinal = TryGetBlockOrdinal(cfg, BasicBlockKind.Exit),
            },
            caveats = new[]
            {
                "CFG reflects static control flow and does not model runtime reflection or dynamic dispatch outcomes.",
            },
            blocks = blocksPayload,
            edges = edgeEntries,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static object BuildLineSpan(FileLinePositionSpan span) => new
    {
        start_line = span.StartLinePosition.Line + 1,
        start_column = span.StartLinePosition.Character + 1,
        end_line = span.EndLinePosition.Line + 1,
        end_column = span.EndLinePosition.Character + 1,
    };

    private static int? TryGetBlockOrdinal(ControlFlowGraph cfg, BasicBlockKind kind)
    {
        foreach (BasicBlock block in cfg.Blocks)
        {
            if (block.Kind == kind)
            {
                return block.Ordinal;
            }
        }

        return null;
    }

    private static void TryAddEdge(
        ControlFlowBranch? branch,
        ICollection<CfgEdgeEntry> edges,
        ISet<string> dedupe,
        int maxEdges)
    {
        if (branch is null || branch.Source is null || branch.Destination is null || edges.Count >= maxEdges)
        {
            return;
        }

        string key = $"{branch.Source.Ordinal}->{branch.Destination.Ordinal}:{branch.IsConditionalSuccessor}:{branch.Semantics}";
        if (!dedupe.Add(key))
        {
            return;
        }

        edges.Add(new CfgEdgeEntry(
            source_ordinal: branch.Source.Ordinal,
            destination_ordinal: branch.Destination.Ordinal,
            semantics: branch.Semantics.ToString(),
            is_conditional_successor: branch.IsConditionalSuccessor));
    }

    private sealed record CfgBlockEntry(
        int ordinal,
        string kind,
        bool is_reachable,
        int operation_count,
        string condition_kind,
        IReadOnlyList<int> predecessor_ordinals,
        IReadOnlyList<string> operation_kinds,
        IReadOnlyList<string> operation_preview);

    private sealed record CfgEdgeEntry(
        int source_ordinal,
        int destination_ordinal,
        string semantics,
        bool is_conditional_successor);
}
