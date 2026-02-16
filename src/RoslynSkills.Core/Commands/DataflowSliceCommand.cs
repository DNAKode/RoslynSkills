using Microsoft.CodeAnalysis;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class DataflowSliceCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "analyze.dataflow_slice",
        Summary: "Compute data-flow sets for a bounded region around a source location.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: [CommandTrait.Heuristic, CommandTrait.DerivedAnalysis, CommandTrait.PotentiallySlow]);

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
        InputParsing.ValidateOptionalInt(input, "max_symbols", errors, minValue: 1, maxValue: 20_000);
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
        int maxSymbols = InputParsing.GetOptionalInt(input, "max_symbols", defaultValue: 200, minValue: 1, maxValue: 20_000);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath).ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, analysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

        if (!FlowAnalysisSupport.TryGetDataFlowRegion(
                analysis,
                line,
                column,
                cancellationToken,
                out SyntaxNode? regionNode,
                out DataFlowAnalysis? dataFlow,
                out CommandError? dataFlowError))
        {
            return new CommandExecutionResult(null, new[] { dataFlowError ?? new CommandError("analysis_failed", "Unable to compute data-flow region.") });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        ISymbol? anchorSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);

        SymbolSliceSet variablesDeclared = BuildSymbolSet(dataFlow!.VariablesDeclared, maxSymbols, brief);
        SymbolSliceSet dataFlowsIn = BuildSymbolSet(dataFlow.DataFlowsIn, maxSymbols, brief);
        SymbolSliceSet dataFlowsOut = BuildSymbolSet(dataFlow.DataFlowsOut, maxSymbols, brief);
        SymbolSliceSet readInside = BuildSymbolSet(dataFlow.ReadInside, maxSymbols, brief);
        SymbolSliceSet writtenInside = BuildSymbolSet(dataFlow.WrittenInside, maxSymbols, brief);
        SymbolSliceSet readOutside = BuildSymbolSet(dataFlow.ReadOutside, maxSymbols, brief);
        SymbolSliceSet writtenOutside = BuildSymbolSet(dataFlow.WrittenOutside, maxSymbols, brief);
        SymbolSliceSet alwaysAssigned = BuildSymbolSet(dataFlow.AlwaysAssigned, maxSymbols, brief);
        SymbolSliceSet captured = BuildSymbolSet(dataFlow.Captured, maxSymbols, brief);
        SymbolSliceSet capturedInside = BuildSymbolSet(dataFlow.CapturedInside, maxSymbols, brief);
        SymbolSliceSet capturedOutside = BuildSymbolSet(dataFlow.CapturedOutside, maxSymbols, brief);
        SymbolSliceSet unsafeAddressTaken = BuildSymbolSet(dataFlow.UnsafeAddressTaken, maxSymbols, brief);

        bool anchorReadInside = ContainsSymbol(dataFlow.ReadInside, anchorSymbol);
        bool anchorWrittenInside = ContainsSymbol(dataFlow.WrittenInside, anchorSymbol);
        bool anchorDataFlowsIn = ContainsSymbol(dataFlow.DataFlowsIn, anchorSymbol);
        bool anchorDataFlowsOut = ContainsSymbol(dataFlow.DataFlowsOut, anchorSymbol);

        if (regionNode is null)
        {
            return new CommandExecutionResult(null, new[] { new CommandError("analysis_failed", "Unable to resolve data-flow region.") });
        }

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                brief,
                max_symbols = maxSymbols,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            region = new
            {
                node_kind = CommandLanguageServices.GetSyntaxKindName(regionNode),
                line_span = BuildLineSpan(regionNode.GetLocation().GetLineSpan()),
                language = analysis.Language,
            },
            anchor_symbol = anchorSymbol is null
                ? null
                : new
                {
                    symbol_display = anchorSymbol.ToDisplayString(),
                    symbol_kind = anchorSymbol.Kind.ToString(),
                    symbol_id = CommandTextFormatting.GetStableSymbolId(anchorSymbol),
                    read_inside = anchorReadInside,
                    written_inside = anchorWrittenInside,
                    data_flows_in = anchorDataFlowsIn,
                    data_flows_out = anchorDataFlowsOut,
                },
            dataflow = new
            {
                succeeded = dataFlow.Succeeded,
                variables_declared = variablesDeclared.Payload,
                data_flows_in = dataFlowsIn.Payload,
                data_flows_out = dataFlowsOut.Payload,
                read_inside = readInside.Payload,
                written_inside = writtenInside.Payload,
                read_outside = readOutside.Payload,
                written_outside = writtenOutside.Payload,
                always_assigned = alwaysAssigned.Payload,
                captured = captured.Payload,
                captured_inside = capturedInside.Payload,
                captured_outside = capturedOutside.Payload,
                unsafe_address_taken = unsafeAddressTaken.Payload,
            },
            counts = new
            {
                variables_declared = dataFlow.VariablesDeclared.Length,
                data_flows_in = dataFlow.DataFlowsIn.Length,
                data_flows_out = dataFlow.DataFlowsOut.Length,
                read_inside = dataFlow.ReadInside.Length,
                written_inside = dataFlow.WrittenInside.Length,
                read_outside = dataFlow.ReadOutside.Length,
                written_outside = dataFlow.WrittenOutside.Length,
                always_assigned = dataFlow.AlwaysAssigned.Length,
                captured = dataFlow.Captured.Length,
                captured_inside = dataFlow.CapturedInside.Length,
                captured_outside = dataFlow.CapturedOutside.Length,
                unsafe_address_taken = dataFlow.UnsafeAddressTaken.Length,
            },
            caveats = new[]
            {
                "Data-flow slice is lexical/semantic and does not model reflection or external runtime effects.",
                "Region selection is anchor-based and may be broader than a single statement when required by Roslyn data-flow APIs.",
            },
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

    private static bool ContainsSymbol(IEnumerable<ISymbol> symbols, ISymbol? target)
    {
        if (target is null)
        {
            return false;
        }

        foreach (ISymbol symbol in symbols)
        {
            if (SymbolEqualityComparer.Default.Equals(symbol, target) ||
                SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, target.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static SymbolSliceSet BuildSymbolSet(IEnumerable<ISymbol> symbols, int maxSymbols, bool brief)
    {
        SymbolSliceItem[] items = symbols
            .Select(symbol => new SymbolSliceItem(
                symbol_display: symbol.ToDisplayString(),
                symbol_kind: symbol.Kind.ToString(),
                symbol_id: CommandTextFormatting.GetStableSymbolId(symbol)))
            .Distinct()
            .OrderBy(item => item.symbol_display, StringComparer.Ordinal)
            .Take(maxSymbols)
            .ToArray();

        object payload = brief
            ? items.Select(item => item.symbol_display).ToArray()
            : items;

        return new SymbolSliceSet(items.Length, payload);
    }

    private sealed record SymbolSliceItem(
        string symbol_display,
        string symbol_kind,
        string? symbol_id);

    private sealed record SymbolSliceSet(int Count, object Payload);
}
