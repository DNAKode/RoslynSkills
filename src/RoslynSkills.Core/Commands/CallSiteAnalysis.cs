using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace RoslynSkills.Core.Commands;

internal static class CallSiteAnalysis
{
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<(SyntaxTree Tree, bool IncludeObjectCreations), CallSite[]>> CallSiteCache = new();

    /// <summary>
    /// Call sites of one tree, computed once per compilation. A hot workspace keeps the same
    /// <see cref="Compilation"/> instance for every project an edit did not touch, so repeated solution-wide
    /// graph queries only re-bind the changed project and its dependents; the cache entry dies with the compilation.
    /// </summary>
    public static IReadOnlyList<CallSite> GetCallSites(
        Compilation compilation,
        SyntaxTree tree,
        bool includeObjectCreations,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<(SyntaxTree, bool), CallSite[]> perCompilation = CallSiteCache.GetValue(
            compilation,
            static _ => new ConcurrentDictionary<(SyntaxTree, bool), CallSite[]>());
        if (perCompilation.TryGetValue((tree, includeObjectCreations), out CallSite[]? cached))
        {
            return cached;
        }

        SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        CallSite[] sites = EnumerateCallSites(semanticModel, tree.GetRoot(cancellationToken), includeObjectCreations, cancellationToken).ToArray();
        perCompilation[(tree, includeObjectCreations)] = sites;
        return sites;
    }

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
                case IMethodReferenceOperation methodReference
                    when methodReference.Parent is IDelegateCreationOperation or IAddressOfOperation:
                    // A method group converted to a delegate (a registered factory, an event handler, a callback):
                    // the method runs later, but this is the only place that names it.
                    callee = methodReference.Method;
                    callKind = "method_group";
                    break;
            }

            if (callee is null || callKind is null)
            {
                continue;
            }

            // Null in field/property initializers: still a use of the callee, but no method contains it.
            IMethodSymbol? caller = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken) as IMethodSymbol;

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
        IMethodSymbol? caller,
        IMethodSymbol callee,
        string call_kind,
        SyntaxNode node);
}
