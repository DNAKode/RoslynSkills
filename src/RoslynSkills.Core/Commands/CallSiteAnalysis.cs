using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace RoslynSkills.Core.Commands;

internal static class CallSiteAnalysis
{
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<SyntaxTree, Lazy<TreeCallSites>>> CallSiteCache = new();

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
        TreeCallSites entry = GetOrCompute(compilation, tree, cancellationToken);
        return includeObjectCreations ? entry.AllSites : entry.NonObjectCreationSites;
    }

    /// <summary>
    /// Returns only the call sites in <paramref name="tree"/> whose callee has <see cref="ISymbol.Name"/> equal to
    /// <paramref name="calleeName"/> (O(1) lookup on a warm tree instead of scanning every call site in the file).
    /// </summary>
    public static IReadOnlyList<CallSite> GetCallSitesByName(
        Compilation compilation,
        SyntaxTree tree,
        string calleeName,
        bool includeObjectCreations,
        CancellationToken cancellationToken)
    {
        TreeCallSites entry = GetOrCompute(compilation, tree, cancellationToken);
        if (!entry.ByCalleeName.TryGetValue(calleeName, out CallSite[]? named))
        {
            return Array.Empty<CallSite>();
        }

        if (includeObjectCreations)
        {
            return named;
        }

        List<CallSite>? filtered = null;
        foreach (CallSite site in named)
        {
            if (string.Equals(site.call_kind, "object_creation", StringComparison.Ordinal))
            {
                continue;
            }

            filtered ??= new List<CallSite>(named.Length);
            filtered.Add(site);
        }

        return filtered ?? (IReadOnlyList<CallSite>)Array.Empty<CallSite>();
    }

    /// <summary>Whether <paramref name="tree"/> already has cached call sites for <paramref name="compilation"/>.</summary>
    internal static bool IsCached(Compilation compilation, SyntaxTree tree)
        => CallSiteCache.TryGetValue(compilation, out ConcurrentDictionary<SyntaxTree, Lazy<TreeCallSites>>? perCompilation) &&
           perCompilation.TryGetValue(tree, out Lazy<TreeCallSites>? entry) &&
           entry.IsValueCreated;

    private static TreeCallSites GetOrCompute(
        Compilation compilation,
        SyntaxTree tree,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConcurrentDictionary<SyntaxTree, Lazy<TreeCallSites>> perCompilation = CallSiteCache.GetValue(
            compilation,
            static _ => new ConcurrentDictionary<SyntaxTree, Lazy<TreeCallSites>>());
        if (perCompilation.TryGetValue(tree, out Lazy<TreeCallSites>? existing) && existing.IsValueCreated)
        {
            return existing.Value;
        }

        Lazy<TreeCallSites> created = new(
            () => BuildTreeCallSites(compilation, tree),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<TreeCallSites> entry = perCompilation.GetOrAdd(tree, created);
        try
        {
            return entry.Value;
        }
        catch
        {
            perCompilation.TryRemove(new KeyValuePair<SyntaxTree, Lazy<TreeCallSites>>(tree, entry));
            throw;
        }
    }

    private static TreeCallSites BuildTreeCallSites(Compilation compilation, SyntaxTree tree)
    {
        SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        SyntaxNode root = tree.GetRoot(CancellationToken.None);
        CallSite[] all = EnumerateCallSites(semanticModel, root, includeObjectCreations: true, CancellationToken.None).ToArray();
        return TreeCallSites.Build(all);
    }

    public static IEnumerable<CallSite> EnumerateCallSites(
        SemanticModel semanticModel,
        SyntaxNode root,
        bool includeObjectCreations,
        CancellationToken cancellationToken)
    {
        if (root is CompilationUnitSyntax csharpUnit)
        {
            return EnumerateCSharpUnitCallSites(semanticModel, csharpUnit, includeObjectCreations, cancellationToken);
        }

        return EnumerateFallbackCallSites(semanticModel, root, includeObjectCreations, cancellationToken);
    }

    /// <summary>
    /// Walks only executable member/initializer <see cref="IOperation"/> roots in a C# compilation unit instead of
    /// calling <see cref="SemanticModel.GetOperation(SyntaxNode, CancellationToken)"/> on every descendant syntax node.
    /// </summary>
    private static IEnumerable<CallSite> EnumerateCSharpUnitCallSites(
        SemanticModel semanticModel,
        CompilationUnitSyntax unit,
        bool includeObjectCreations,
        CancellationToken cancellationToken)
    {
        HashSet<string> yielded = new(StringComparer.Ordinal);
        List<IOperation> operationRoots = new();
        CollectCSharpOperationRoots(semanticModel, unit, operationRoots, cancellationToken);

        foreach (IOperation rootOp in operationRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (IOperation operation in rootOp.DescendantsAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.IsImplicit)
                {
                    continue;
                }

                if (!TryExtractCallSite(operation, includeObjectCreations, out IMethodSymbol? callee, out string? callKind))
                {
                    continue;
                }

                SyntaxNode node = operation.Syntax;
                (ISymbol? caller, bool viaLambda) = ResolveEnclosingCaller(semanticModel, node, cancellationToken);
                if (!TryRecordYielded(yielded, callKind!, callee!, node))
                {
                    continue;
                }

                yield return new CallSite(caller, callee!, callKind!, node, viaLambda);
            }
        }
    }

    private static void CollectCSharpOperationRoots(
        SemanticModel semanticModel,
        CompilationUnitSyntax unit,
        List<IOperation> roots,
        CancellationToken cancellationToken)
    {
        void AddOperation(SyntaxNode? node, SyntaxNode? fallback = null)
        {
            if (node is null)
            {
                return;
            }

            IOperation? op = semanticModel.GetOperation(node, cancellationToken);
            if (op is null && fallback is not null)
            {
                op = semanticModel.GetOperation(fallback, cancellationToken);
            }

            if (op is not null)
            {
                roots.Add(op);
            }
        }

        void CollectParameterDefaults(ParameterListSyntax? parameterList)
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

        void CollectBracketedParameterDefaults(BracketedParameterListSyntax? parameterList)
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

        void CollectMembers(SyntaxList<MemberDeclarationSyntax> members)
        {
            foreach (MemberDeclarationSyntax member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (member)
                {
                    case BaseNamespaceDeclarationSyntax ns:
                        CollectMembers(ns.Members);
                        break;

                    case TypeDeclarationSyntax typeDecl:
                        CollectParameterDefaults(typeDecl.ParameterList);
                        if (typeDecl.BaseList is not null)
                        {
                            foreach (BaseTypeSyntax baseType in typeDecl.BaseList.Types)
                            {
                                if (baseType is PrimaryConstructorBaseTypeSyntax primaryBase)
                                {
                                    IOperation? primaryOp = semanticModel.GetOperation(primaryBase, cancellationToken);
                                    if (primaryOp is not null)
                                    {
                                        roots.Add(primaryOp);
                                    }
                                    else if (primaryBase.ArgumentList is not null)
                                    {
                                        foreach (ArgumentSyntax arg in primaryBase.ArgumentList.Arguments)
                                        {
                                            AddOperation(arg.Expression);
                                        }
                                    }
                                }
                            }
                        }

                        CollectMembers(typeDecl.Members);
                        break;

                    case EnumDeclarationSyntax enumDecl:
                        foreach (EnumMemberDeclarationSyntax enumMember in enumDecl.Members)
                        {
                            if (enumMember.EqualsValue is not null)
                            {
                                AddOperation(enumMember.EqualsValue, enumMember.EqualsValue.Value);
                            }
                        }

                        break;

                    case DelegateDeclarationSyntax delegateDecl:
                        CollectParameterDefaults(delegateDecl.ParameterList);
                        break;

                    case BaseMethodDeclarationSyntax methodDecl:
                        IOperation? methodOp = semanticModel.GetOperation(methodDecl, cancellationToken);
                        if (methodOp is not null)
                        {
                            roots.Add(methodOp);
                        }
                        else
                        {
                            if (methodDecl is ConstructorDeclarationSyntax { Initializer: not null } ctor)
                            {
                                AddOperation(ctor.Initializer);
                            }

                            AddOperation(methodDecl.Body);
                            if (methodDecl.ExpressionBody is not null)
                            {
                                AddOperation(methodDecl.ExpressionBody, methodDecl.ExpressionBody.Expression);
                            }
                        }

                        CollectParameterDefaults(methodDecl.ParameterList);
                        break;

                    case PropertyDeclarationSyntax propDecl:
                        if (propDecl.ExpressionBody is not null)
                        {
                            AddOperation(propDecl.ExpressionBody, propDecl.ExpressionBody.Expression);
                        }

                        if (propDecl.AccessorList is not null)
                        {
                            foreach (AccessorDeclarationSyntax accessor in propDecl.AccessorList.Accessors)
                            {
                                IOperation? accOp = semanticModel.GetOperation(accessor, cancellationToken);
                                if (accOp is not null)
                                {
                                    roots.Add(accOp);
                                }
                                else
                                {
                                    AddOperation(accessor.Body);
                                    if (accessor.ExpressionBody is not null)
                                    {
                                        AddOperation(accessor.ExpressionBody, accessor.ExpressionBody.Expression);
                                    }
                                }
                            }
                        }

                        if (propDecl.Initializer is not null)
                        {
                            AddOperation(propDecl.Initializer, propDecl.Initializer.Value);
                        }

                        break;

                    case IndexerDeclarationSyntax indexerDecl:
                        if (indexerDecl.ExpressionBody is not null)
                        {
                            AddOperation(indexerDecl.ExpressionBody, indexerDecl.ExpressionBody.Expression);
                        }

                        if (indexerDecl.AccessorList is not null)
                        {
                            foreach (AccessorDeclarationSyntax accessor in indexerDecl.AccessorList.Accessors)
                            {
                                IOperation? accOp = semanticModel.GetOperation(accessor, cancellationToken);
                                if (accOp is not null)
                                {
                                    roots.Add(accOp);
                                }
                                else
                                {
                                    AddOperation(accessor.Body);
                                    if (accessor.ExpressionBody is not null)
                                    {
                                        AddOperation(accessor.ExpressionBody, accessor.ExpressionBody.Expression);
                                    }
                                }
                            }
                        }

                        CollectBracketedParameterDefaults(indexerDecl.ParameterList);
                        break;

                    case EventDeclarationSyntax eventDecl:
                        if (eventDecl.AccessorList is not null)
                        {
                            foreach (AccessorDeclarationSyntax accessor in eventDecl.AccessorList.Accessors)
                            {
                                IOperation? accOp = semanticModel.GetOperation(accessor, cancellationToken);
                                if (accOp is not null)
                                {
                                    roots.Add(accOp);
                                }
                                else
                                {
                                    AddOperation(accessor.Body);
                                    if (accessor.ExpressionBody is not null)
                                    {
                                        AddOperation(accessor.ExpressionBody, accessor.ExpressionBody.Expression);
                                    }
                                }
                            }
                        }

                        break;

                    case BaseFieldDeclarationSyntax fieldDecl:
                        foreach (VariableDeclaratorSyntax variable in fieldDecl.Declaration.Variables)
                        {
                            if (variable.Initializer is not null)
                            {
                                AddOperation(variable.Initializer, variable.Initializer.Value);
                            }
                        }

                        break;

                    case GlobalStatementSyntax globalStatement:
                        AddOperation(globalStatement, globalStatement.Statement);
                        break;
                }
            }
        }

        CollectMembers(unit.Members);
    }

    private static IEnumerable<CallSite> EnumerateFallbackCallSites(
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

            if (!TryExtractCallSite(operation, includeObjectCreations, out IMethodSymbol? callee, out string? callKind))
            {
                continue;
            }

            (ISymbol? caller, bool viaLambda) = ResolveEnclosingCaller(semanticModel, node, cancellationToken);
            if (!TryRecordYielded(yielded, callKind!, callee!, node))
            {
                continue;
            }

            yield return new CallSite(caller, callee!, callKind!, node, viaLambda);
        }
    }

    private static bool TryExtractCallSite(
        IOperation operation,
        bool includeObjectCreations,
        out IMethodSymbol? callee,
        out string? callKind)
    {
        callee = null;
        callKind = null;
        switch (operation)
        {
            case IInvocationOperation invocationOperation:
                callee = invocationOperation.TargetMethod;
                callKind = "invocation";
                return callee is not null;
            case IObjectCreationOperation objectCreationOperation when includeObjectCreations:
                callee = objectCreationOperation.Constructor;
                callKind = "object_creation";
                return callee is not null;
            case IMethodReferenceOperation methodReference
                when methodReference.Parent is IDelegateCreationOperation or IAddressOfOperation:
                // A method group converted to a delegate (a registered factory, an event handler, a callback):
                // the method runs later, but this is the only place that names it.
                callee = methodReference.Method;
                callKind = "method_group";
                return callee is not null;
            default:
                return false;
        }
    }

    private static bool TryRecordYielded(HashSet<string> yielded, string callKind, IMethodSymbol callee, SyntaxNode node)
    {
        string calleeId = CommandTextFormatting.GetStableSymbolId(callee)
            ?? callee.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string key = $"{callKind}|{calleeId}|{node.SpanStart}|{node.Span.Length}";
        return yielded.Add(key);
    }

    private static (ISymbol? Caller, bool ViaLambda) ResolveEnclosingCaller(
        SemanticModel semanticModel,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        ISymbol? enclosing = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
        bool viaLambda = false;
        while (enclosing is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } anonymous)
        {
            viaLambda = true;
            enclosing = anonymous.ContainingSymbol;
        }

        return (enclosing, viaLambda);
    }

    private sealed class TreeCallSites
    {
        public CallSite[] AllSites { get; }
        public CallSite[] NonObjectCreationSites { get; }
        public Dictionary<string, CallSite[]> ByCalleeName { get; }

        private TreeCallSites(CallSite[] allSites, CallSite[] nonObjectCreationSites, Dictionary<string, CallSite[]> byCalleeName)
        {
            AllSites = allSites;
            NonObjectCreationSites = nonObjectCreationSites;
            ByCalleeName = byCalleeName;
        }

        public static TreeCallSites Build(CallSite[] allSites)
        {
            List<CallSite> nonObjectCreations = new(allSites.Length);
            Dictionary<string, List<CallSite>> byName = new(StringComparer.Ordinal);
            foreach (CallSite site in allSites)
            {
                if (!string.Equals(site.call_kind, "object_creation", StringComparison.Ordinal))
                {
                    nonObjectCreations.Add(site);
                }

                string name = site.callee.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    if (!byName.TryGetValue(name, out List<CallSite>? list))
                    {
                        list = new List<CallSite>();
                        byName[name] = list;
                    }

                    list.Add(site);
                }
            }

            Dictionary<string, CallSite[]> finalizedByName = new(byName.Count, StringComparer.Ordinal);
            foreach ((string name, List<CallSite> list) in byName)
            {
                finalizedByName[name] = list.ToArray();
            }

            return new TreeCallSites(allSites, nonObjectCreations.ToArray(), finalizedByName);
        }
    }

    internal sealed record CallSite(
        ISymbol? caller,
        IMethodSymbol callee,
        string call_kind,
        SyntaxNode node,
        bool via_lambda = false);
}
