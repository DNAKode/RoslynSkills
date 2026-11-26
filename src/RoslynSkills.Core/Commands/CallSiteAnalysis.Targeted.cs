using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Selective ("targeted") call-site discovery for a cold tree: instead of binding every executable member of the tree
/// (<see cref="GetCallSites"/>), bind only the syntax nodes a <see cref="CallSiteLookup"/> says can be a call to the
/// target. A tree whose full call-site cache already exists answers from that cache; targeted results are cached per
/// (compilation, tree, lookup) and die with the compilation like every other cache here.
/// </summary>
internal static partial class CallSiteAnalysis
{
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<(SyntaxTree Tree, string Key), Lazy<CallSite[]>>> TargetedCache = new();

    /// <summary>
    /// The call sites of <paramref name="tree"/> that <paramref name="lookup"/> can match: a superset of the true call
    /// sites of the target, with the callee still to be compared by the caller. Equivalent to
    /// <see cref="GetCallSitesByName"/> on the whole tree, but a cold tree is bound only where the lookup's names appear.
    /// </summary>
    public static IReadOnlyList<CallSite> GetCallSitesForLookup(
        Compilation compilation,
        SyntaxTree tree,
        CallSiteLookup lookup,
        bool includeObjectCreations,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string>? calleeNames = lookup.CalleeNames;
        if (calleeNames is null)
        {
            return GetCallSites(compilation, tree, includeObjectCreations, cancellationToken);
        }

        bool canNarrow = lookup.Targeted &&
                         string.Equals(tree.Options.Language, LanguageNames.CSharp, StringComparison.Ordinal);
        if (!canNarrow || IsCached(compilation, tree))
        {
            return CollectByCalleeNames(GetOrCompute(compilation, tree, cancellationToken), calleeNames, includeObjectCreations);
        }

        ConcurrentDictionary<(SyntaxTree Tree, string Key), Lazy<CallSite[]>> perCompilation = TargetedCache.GetValue(
            compilation,
            static _ => new ConcurrentDictionary<(SyntaxTree Tree, string Key), Lazy<CallSite[]>>());
        (SyntaxTree, string) cacheKey = (tree, lookup.CacheKey);
        if (!perCompilation.TryGetValue(cacheKey, out Lazy<CallSite[]>? entry))
        {
            Lazy<CallSite[]> created = new(
                () => ComputeTargeted(compilation, tree, lookup),
                LazyThreadSafetyMode.ExecutionAndPublication);
            entry = perCompilation.GetOrAdd(cacheKey, created);
        }

        CallSite[] sites;
        try
        {
            sites = entry.Value;
        }
        catch
        {
            perCompilation.TryRemove(new KeyValuePair<(SyntaxTree Tree, string Key), Lazy<CallSite[]>>(cacheKey, entry));
            throw;
        }

        if (includeObjectCreations)
        {
            return sites;
        }

        return sites.Where(static site => !string.Equals(site.call_kind, "object_creation", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Whether targeted work for <paramref name="lookup"/> is already cached for <paramref name="tree"/>.</summary>
    internal static bool IsLookupCached(Compilation compilation, SyntaxTree tree, CallSiteLookup lookup)
    {
        if (IsCached(compilation, tree))
        {
            return true;
        }

        return TargetedCache.TryGetValue(compilation, out ConcurrentDictionary<(SyntaxTree Tree, string Key), Lazy<CallSite[]>>? perCompilation) &&
               perCompilation.TryGetValue((tree, lookup.CacheKey), out Lazy<CallSite[]>? entry) &&
               entry.IsValueCreated;
    }

    private static IReadOnlyList<CallSite> CollectByCalleeNames(
        TreeCallSites entry,
        IReadOnlyCollection<string> calleeNames,
        bool includeObjectCreations)
    {
        List<CallSite>? collected = null;
        CallSite[]? single = null;
        foreach (string name in calleeNames)
        {
            if (!entry.ByCalleeName.TryGetValue(name, out CallSite[]? named))
            {
                continue;
            }

            if (single is null && collected is null)
            {
                single = named;
                continue;
            }

            collected ??= new List<CallSite>(single!);
            collected.AddRange(named);
        }

        if (single is null)
        {
            return Array.Empty<CallSite>();
        }

        IEnumerable<CallSite> sites = collected is null
            ? single
            : collected.OrderBy(static site => site.node.SpanStart);
        if (includeObjectCreations)
        {
            return collected is null ? single : sites.ToArray();
        }

        return sites.Where(static site => !string.Equals(site.call_kind, "object_creation", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>
    /// Whether <paramref name="node"/> is a declaration whose body <see cref="GetCallSitesInDeclarations"/> can bind on
    /// its own (a method, constructor, operator, local function or explicit accessor).
    /// </summary>
    internal static bool IsSelfContainedDeclaration(SyntaxNode node)
        => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax;

    /// <summary>
    /// Call sites inside <paramref name="declarations"/> only (each satisfying <see cref="IsSelfContainedDeclaration"/>),
    /// binding just those bodies instead of the whole tree. Sites of nested local functions and lambdas are included; the
    /// caller picks the ones it wants by <see cref="CallSite.caller"/>.
    /// </summary>
    public static IReadOnlyList<CallSite> GetCallSitesInDeclarations(
        Compilation compilation,
        SyntaxTree tree,
        IReadOnlyList<SyntaxNode> declarations,
        bool includeObjectCreations,
        CancellationToken cancellationToken)
    {
        SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        List<IOperation> roots = new();

        void AddOperation(SyntaxNode? node, SyntaxNode? fallback = null)
        {
            if (node is null)
            {
                return;
            }

            IOperation? operation = semanticModel.GetOperation(node, cancellationToken);
            if (operation is null && fallback is not null)
            {
                operation = semanticModel.GetOperation(fallback, cancellationToken);
            }

            if (operation is not null)
            {
                roots.Add(operation);
            }
        }

        void AddParameterDefaults(ParameterListSyntax? parameterList)
        {
            if (parameterList is null)
            {
                return;
            }

            foreach (ParameterSyntax parameter in parameterList.Parameters)
            {
                if (parameter.Default is not null)
                {
                    AddOperation(parameter.Default, parameter.Default.Value);
                }
            }
        }

        foreach (SyntaxNode declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (declaration)
            {
                case BaseMethodDeclarationSyntax method:
                    IOperation? methodOperation = semanticModel.GetOperation(method, cancellationToken);
                    if (methodOperation is not null)
                    {
                        roots.Add(methodOperation);
                    }
                    else
                    {
                        if (method is ConstructorDeclarationSyntax { Initializer: not null } constructor)
                        {
                            AddOperation(constructor.Initializer);
                        }

                        AddOperation(method.Body);
                        AddOperation(method.ExpressionBody, method.ExpressionBody?.Expression);
                    }

                    AddParameterDefaults(method.ParameterList);
                    break;

                case LocalFunctionStatementSyntax localFunction:
                    IOperation? localOperation = semanticModel.GetOperation(localFunction, cancellationToken);
                    if (localOperation is not null)
                    {
                        roots.Add(localOperation);
                    }
                    else
                    {
                        AddOperation(localFunction.Body);
                        AddOperation(localFunction.ExpressionBody, localFunction.ExpressionBody?.Expression);
                    }

                    AddParameterDefaults(localFunction.ParameterList);
                    break;

                case AccessorDeclarationSyntax accessor:
                    IOperation? accessorOperation = semanticModel.GetOperation(accessor, cancellationToken);
                    if (accessorOperation is not null)
                    {
                        roots.Add(accessorOperation);
                    }
                    else
                    {
                        AddOperation(accessor.Body);
                        AddOperation(accessor.ExpressionBody, accessor.ExpressionBody?.Expression);
                    }

                    break;
            }
        }

        return EnumerateRootCallSites(semanticModel, roots, includeObjectCreations, cancellationToken).ToArray();
    }

    private static CallSite[] ComputeTargeted(Compilation compilation, SyntaxTree tree, CallSiteLookup lookup)
    {
        SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        SyntaxNode root = tree.GetRoot(CancellationToken.None);
        HashSet<string> yielded = new(StringComparer.Ordinal);
        List<CallSite> sites = new();

        foreach (SyntaxNode candidate in EnumerateCandidateNodes(root, lookup))
        {
            IOperation? operation = semanticModel.GetOperation(candidate, CancellationToken.None);
            if (operation is null || operation.IsImplicit)
            {
                continue;
            }

            if (!TryExtractCallSite(operation, includeObjectCreations: true, out IMethodSymbol? callee, out string? callKind))
            {
                continue;
            }

            SyntaxNode node = operation.Syntax;
            (ISymbol? caller, bool viaLambda) = ResolveEnclosingCaller(semanticModel, node, CancellationToken.None);
            if (!TryRecordYielded(yielded, callKind!, callee!, node))
            {
                continue;
            }

            sites.Add(new CallSite(caller, callee!, callKind!, node, viaLambda));
        }

        sites.Sort(static (left, right) => left.node.SpanStart.CompareTo(right.node.SpanStart));
        return sites.ToArray();
    }

    /// <summary>
    /// The syntax nodes whose operation can be a call to what <paramref name="lookup"/> targets. Every node is a
    /// candidate only; <see cref="TryExtractCallSite"/> and the caller's callee comparison decide.
    /// </summary>
    private static IEnumerable<SyntaxNode> EnumerateCandidateNodes(SyntaxNode root, CallSiteLookup lookup)
    {
        if (lookup.MethodNames.Count > 0)
        {
            foreach (SyntaxToken token in root.DescendantTokens(descendIntoTrivia: false))
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken) ||
                    token.Parent is not SimpleNameSyntax name ||
                    !lookup.MethodNames.Contains(token.ValueText))
                {
                    continue;
                }

                // `a.Foo`, `a?.Foo` and bare `Foo` all carry the same operation at the outermost name expression.
                SyntaxNode expression = name;
                if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
                {
                    expression = memberAccess;
                }
                else if (name.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == name)
                {
                    expression = memberBinding;
                }

                if (expression.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expression)
                {
                    yield return invocation;
                }
                else
                {
                    // A method group converted to a delegate (or `&Foo`).
                    yield return expression;
                }
            }
        }

        if (lookup.HasConstructors)
        {
            foreach (SyntaxNode node in root.DescendantNodes(descendIntoTrivia: false))
            {
                switch (node)
                {
                    case ObjectCreationExpressionSyntax creation when SpellsOneOf(creation.Type, lookup.ConstructorTypeNames):
                    case PrimaryConstructorBaseTypeSyntax primaryBase when SpellsOneOf(primaryBase.Type, lookup.ConstructorTypeNames):
                    case ImplicitObjectCreationExpressionSyntax:
                    case ConstructorInitializerSyntax:
                        yield return node;
                        break;
                }
            }
        }
    }

    /// <summary>Whether the rightmost simple name of <paramref name="type"/> is one of <paramref name="names"/> (unknown shapes count as a match).</summary>
    private static bool SpellsOneOf(TypeSyntax type, IReadOnlySet<string> names)
    {
        TypeSyntax current = type;
        while (true)
        {
            switch (current)
            {
                case IdentifierNameSyntax identifier:
                    return names.Contains(identifier.Identifier.ValueText);
                case GenericNameSyntax generic:
                    return names.Contains(generic.Identifier.ValueText);
                case QualifiedNameSyntax qualified:
                    current = qualified.Right;
                    continue;
                case AliasQualifiedNameSyntax aliasQualified:
                    current = aliasQualified.Name;
                    continue;
                case NullableTypeSyntax nullable:
                    current = nullable.ElementType;
                    continue;
                default:
                    return true;
            }
        }
    }
}
