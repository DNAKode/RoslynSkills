using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

internal static class FlowAnalysisSupport
{
    public static bool TryGetControlFlowGraph(
        CommandFileAnalysis analysis,
        int line,
        int column,
        CancellationToken cancellationToken,
        out ControlFlowGraph? controlFlowGraph,
        out SyntaxNode? executableNode,
        out CommandError? error)
    {
        controlFlowGraph = null;
        executableNode = null;
        error = null;

        if (line < 1 || line > analysis.SourceText.Lines.Count)
        {
            error = new CommandError(
                "invalid_input",
                $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).");
            return false;
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        SyntaxNode? startNode = anchorToken.Parent ?? analysis.Root;

        foreach (SyntaxNode candidate in EnumerateAncestorCandidates(startNode))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ControlFlowGraph? cfg = ControlFlowGraph.Create(candidate, analysis.SemanticModel, cancellationToken);
                if (cfg is null)
                {
                    continue;
                }

                controlFlowGraph = cfg;
                executableNode = candidate;
                return true;
            }
            catch (ArgumentException)
            {
                // Candidate cannot be used as an executable region root.
            }
            catch (InvalidOperationException)
            {
                // Candidate cannot be bound to a CFG in current semantic context.
            }
        }

        error = new CommandError(
            "analysis_failed",
            "Unable to build a control-flow graph for the provided location. Anchor a method, constructor, accessor, lambda, or executable block.");
        return false;
    }

    public static bool TryGetDataFlowRegion(
        CommandFileAnalysis analysis,
        int line,
        int column,
        CancellationToken cancellationToken,
        out SyntaxNode? regionNode,
        out DataFlowAnalysis? dataFlow,
        out CommandError? error)
    {
        regionNode = null;
        dataFlow = null;
        error = null;

        if (line < 1 || line > analysis.SourceText.Lines.Count)
        {
            error = new CommandError(
                "invalid_input",
                $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).");
            return false;
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        SyntaxNode? startNode = anchorToken.Parent ?? analysis.Root;

        SyntaxNode? fallbackNode = null;
        DataFlowAnalysis? fallbackDataFlow = null;

        foreach (SyntaxNode candidate in EnumerateAncestorCandidates(startNode))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DataFlowAnalysis? candidateDataFlow = TryAnalyzeDataFlow(analysis.SemanticModel, candidate);
            if (candidateDataFlow is null || !candidateDataFlow.Succeeded)
            {
                continue;
            }

            if (HasAnyDataFlowSignal(candidateDataFlow))
            {
                regionNode = candidate;
                dataFlow = candidateDataFlow;
                return true;
            }

            fallbackNode ??= candidate;
            fallbackDataFlow ??= candidateDataFlow;
        }

        if (fallbackNode is not null && fallbackDataFlow is not null)
        {
            regionNode = fallbackNode;
            dataFlow = fallbackDataFlow;
            return true;
        }

        error = new CommandError(
            "analysis_failed",
            "Unable to compute data-flow analysis for the provided location. Anchor a statement, expression, or executable block.");
        return false;
    }

    private static DataFlowAnalysis? TryAnalyzeDataFlow(SemanticModel semanticModel, SyntaxNode candidate)
    {
        try
        {
            return semanticModel.AnalyzeDataFlow(candidate);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool HasAnyDataFlowSignal(DataFlowAnalysis dataFlow)
    {
        return dataFlow.VariablesDeclared.Length > 0 ||
               dataFlow.DataFlowsIn.Length > 0 ||
               dataFlow.DataFlowsOut.Length > 0 ||
               dataFlow.ReadInside.Length > 0 ||
               dataFlow.WrittenInside.Length > 0 ||
               dataFlow.ReadOutside.Length > 0 ||
               dataFlow.WrittenOutside.Length > 0 ||
               dataFlow.AlwaysAssigned.Length > 0 ||
               dataFlow.Captured.Length > 0 ||
               dataFlow.CapturedInside.Length > 0 ||
               dataFlow.CapturedOutside.Length > 0 ||
               dataFlow.UnsafeAddressTaken.Length > 0;
    }

    private static IEnumerable<SyntaxNode> EnumerateAncestorCandidates(SyntaxNode start)
    {
        SyntaxNode? cursor = start;
        while (cursor is not null)
        {
            yield return cursor;
            cursor = cursor.Parent;
        }
    }
}
