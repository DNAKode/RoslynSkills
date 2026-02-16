using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynSkills.Core.Commands;

internal static class CallSiteAnalysis
{
    public static IEnumerable<CallSite> EnumerateCallSites(
        SemanticModel semanticModel,
        SyntaxNode root,
        bool includeObjectCreations,
        CancellationToken cancellationToken)
    {
        HashSet<string> yielded = new(StringComparer.Ordinal);
        foreach (SyntaxNode node in root.DescendantNodes(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IOperation? operation = semanticModel.GetOperation(node, cancellationToken);
            if (operation is null)
            {
                continue;
            }

            IMethodSymbol? callee = null;
            string? callKind = null;
            switch (operation)
            {
                case IInvocationOperation invocationOperation:
                    callee = invocationOperation.TargetMethod;
                    callKind = "invocation";
                    break;
                case IObjectCreationOperation objectCreationOperation when includeObjectCreations:
                    callee = objectCreationOperation.Constructor;
                    callKind = "object_creation";
                    break;
            }

            if (callee is null || callKind is null)
            {
                continue;
            }

            if (semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken) is not IMethodSymbol caller)
            {
                continue;
            }

            string calleeId = CommandTextFormatting.GetStableSymbolId(callee)
                ?? callee.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string key = $"{callKind}|{calleeId}|{node.SpanStart}|{node.Span.Length}";
            if (!yielded.Add(key))
            {
                continue;
            }

            yield return new CallSite(caller, callee, callKind, node);
        }
    }

    internal sealed record CallSite(
        IMethodSymbol caller,
        IMethodSymbol callee,
        string call_kind,
        SyntaxNode node);
}
