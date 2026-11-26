using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Runtime.CompilerServices;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Describes which call sites a usage search is after, so cold trees can be narrowed by text and bound
/// selectively instead of being analysed whole. Built from the target symbol(s) of <c>nav.find_invocations</c> and the
/// incoming frontier of <c>nav.call_hierarchy</c>.
/// <para>
/// Completeness rules (a true call site is never skipped):
/// </para>
/// <list type="bullet">
///   <item>An ordinary method or local function is called by spelling its name (plain call, member access, extension
///   call, <c>using static</c> call, method group, <c>?.</c> call); a tree that never spells the name cannot call it.</item>
///   <item>Names the compiler calls without spelling them (collection-initializer <c>Add</c>, interpolated-string handler
///   <c>Append*</c>, <c>Deconstruct</c>, <c>GetEnumerator</c>, query-clause <c>Select</c>/<c>Where</c>, awaiter members, ...)
///   switch the lookup to <see cref="Targeted"/> = false: every tree stays a candidate and is analysed whole.</item>
///   <item>A constructor (or a named type, whose constructors are its call sites) is called by spelling the type name or
///   a type alias of it (aliases are resolved to a fixpoint across the compilations in scope, <c>global using</c> aliases
///   included), by a target-typed <c>new(...)</c>, or by a <c>: base(...)</c>/<c>: this(...)</c> initializer; trees with
///   none of these cannot construct the type.</item>
///   <item>Operators, accessors, delegate <c>Invoke</c> and every other target kind are never narrowed.</item>
/// </list>
/// </summary>
internal sealed class CallSiteLookup
{
    /// <summary>
    /// Method names that the compiler invokes implicitly (the call is not spelled in source). They are never used to
    /// narrow trees.
    /// </summary>
    private static readonly HashSet<string> ImplicitlyCalledNames = new(StringComparer.Ordinal)
    {
        "Add", "AppendLiteral", "AppendFormatted", "Deconstruct", "GetEnumerator", "GetAsyncEnumerator", "MoveNext",
        "MoveNextAsync", "Dispose", "DisposeAsync", "GetAwaiter", "GetResult", "OnCompleted", "UnsafeOnCompleted",
        "Slice", "Select", "SelectMany", "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "Join", "GroupJoin", "GroupBy", "Cast", "GetPinnableReference",
    };

    private static readonly ConditionalWeakTable<SyntaxTree, AliasDirectives> AliasCache = new();

    private readonly HashSet<string> methodNames;
    private readonly HashSet<string> constructorTypeNames;

    private CallSiteLookup(
        HashSet<string> methodNames,
        HashSet<string> constructorTypeNames,
        HashSet<string>? calleeNames,
        bool targeted)
    {
        this.methodNames = methodNames;
        this.constructorTypeNames = constructorTypeNames;
        CalleeNames = calleeNames;
        Targeted = targeted;
        CacheKey = BuildCacheKey(methodNames, constructorTypeNames);
    }

    /// <summary>
    /// <see cref="ISymbol.Name"/> values the callee of a matching call site can have (<c>.ctor</c> for constructors), or
    /// null when unknown. A warm tree answers from its by-callee-name index with these.
    /// </summary>
    public IReadOnlyCollection<string>? CalleeNames { get; }

    /// <summary>True when trees can be narrowed by text and bound selectively; false means analyse whole trees.</summary>
    public bool Targeted { get; }

    /// <summary>Spelled method identifiers a tree must contain (targeted lookups only).</summary>
    public IReadOnlySet<string> MethodNames => methodNames;

    /// <summary>Type names and type aliases that spell a constructor call (targeted lookups only).</summary>
    public IReadOnlySet<string> ConstructorTypeNames => constructorTypeNames;

    /// <summary>True when constructors are among the targets.</summary>
    public bool HasConstructors => constructorTypeNames.Count > 0;

    /// <summary>Identity of the targeted work for per-tree caching.</summary>
    public string CacheKey { get; }

    /// <summary>A lookup that narrows nothing and knows no callee names: every call site of every tree is considered.</summary>
    public static CallSiteLookup All { get; } = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), calleeNames: null, targeted: false);

    /// <summary>
    /// Builds the lookup for <paramref name="targets"/>. <paramref name="aliasScope"/> supplies the compilations whose
    /// trees may declare a type alias of a constructor target.
    /// </summary>
    public static CallSiteLookup ForTargets(IEnumerable<ISymbol> targets, IEnumerable<Compilation> aliasScope)
    {
        HashSet<string> methods = new(StringComparer.Ordinal);
        HashSet<string> constructorTypes = new(StringComparer.Ordinal);
        HashSet<string> callee = new(StringComparer.Ordinal);
        bool targeted = true;

        foreach (ISymbol target in targets)
        {
            switch (target)
            {
                case IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } ctorType }:
                    constructorTypes.Add(ctorType.Name);
                    callee.Add(".ctor");
                    break;

                case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction } method:
                {
                    IMethodSymbol definition = method.ReducedFrom ?? method;
                    callee.Add(definition.Name);
                    if (ImplicitlyCalledNames.Contains(definition.Name))
                    {
                        targeted = false;
                    }
                    else
                    {
                        methods.Add(definition.Name);
                    }

                    break;
                }

                case IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation } explicitImpl:
                    // Only reachable through the interface member, so it adds nothing to search for itself.
                    callee.Add(explicitImpl.Name);
                    break;

                case IMethodSymbol other:
                    callee.Add(other.Name);
                    targeted = false;
                    break;

                case INamedTypeSymbol type:
                    constructorTypes.Add(type.Name);
                    callee.Add(".ctor");
                    break;

                default:
                    return All;
            }
        }

        if (constructorTypes.Count > 0 && targeted)
        {
            ExpandAliases(constructorTypes, aliasScope);
        }

        return new CallSiteLookup(methods, constructorTypes, callee, targeted);
    }

    /// <summary>Convenience for a single target.</summary>
    public static CallSiteLookup ForTarget(ISymbol target, IEnumerable<Compilation> aliasScope)
        => ForTargets(new[] { target }, aliasScope);

    /// <summary>The distinct compilations of <paramref name="units"/>, for alias discovery.</summary>
    public static IReadOnlyList<Compilation> GetCompilations(IEnumerable<SearchUnit> units)
    {
        HashSet<Compilation> seen = new(ReferenceEqualityComparer.Instance);
        List<Compilation> result = new();
        foreach (SearchUnit unit in units)
        {
            if (seen.Add(unit.Compilation))
            {
                result.Add(unit.Compilation);
            }
        }

        return result;
    }

    /// <summary>Whether <paramref name="tree"/> can contain a call site this lookup is after.</summary>
    public bool MightMention(SyntaxTree tree)
    {
        if (!Targeted)
        {
            return true;
        }

        if (!string.Equals(tree.Options.Language, LanguageNames.CSharp, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (string name in methodNames)
        {
            if (IdentifierIndex.MightMention(tree, name))
            {
                return true;
            }
        }

        if (constructorTypeNames.Count > 0)
        {
            foreach (string typeName in constructorTypeNames)
            {
                if (IdentifierIndex.MightMention(tree, typeName))
                {
                    return true;
                }
            }

            if (IdentifierIndex.MightHaveImplicitObjectCreation(tree) || IdentifierIndex.MightHaveConstructorInitializer(tree))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="tree"/> can contain a call site this lookup is after.</summary>
    public bool MightMention(SearchUnit unit) => MightMention(unit.Tree);

    private static string BuildCacheKey(HashSet<string> methods, HashSet<string> constructorTypes)
        => "m:" + string.Join(',', methods.Order(StringComparer.Ordinal)) + "|c:" + string.Join(',', constructorTypes.Order(StringComparer.Ordinal));

    private static void ExpandAliases(HashSet<string> names, IEnumerable<Compilation> aliasScope)
    {
        List<AliasDirective> aliases = new();
        foreach (Compilation compilation in aliasScope)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                aliases.AddRange(GetAliases(tree).Directives);
            }
        }

        if (aliases.Count == 0)
        {
            return;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (AliasDirective alias in aliases)
            {
                if (names.Contains(alias.Name))
                {
                    continue;
                }

                foreach (string identifier in alias.TargetIdentifiers)
                {
                    if (names.Contains(identifier))
                    {
                        names.Add(alias.Name);
                        changed = true;
                        break;
                    }
                }
            }
        }
    }

    private static AliasDirectives GetAliases(SyntaxTree tree)
        => AliasCache.GetValue(tree, static t => AliasDirectives.Build(t));

    private sealed record AliasDirective(string Name, string[] TargetIdentifiers);

    private sealed class AliasDirectives
    {
        private AliasDirectives(List<AliasDirective> directives) => Directives = directives;

        public List<AliasDirective> Directives { get; }

        public static AliasDirectives Build(SyntaxTree tree)
        {
            List<AliasDirective> directives = new();
            if (tree.GetRoot(CancellationToken.None) is CompilationUnitSyntax unit)
            {
                Collect(unit.Usings, directives);
                CollectNamespaces(unit.Members, directives);
            }

            return new AliasDirectives(directives);
        }

        private static void CollectNamespaces(SyntaxList<MemberDeclarationSyntax> members, List<AliasDirective> directives)
        {
            foreach (MemberDeclarationSyntax member in members)
            {
                if (member is BaseNamespaceDeclarationSyntax ns)
                {
                    Collect(ns.Usings, directives);
                    CollectNamespaces(ns.Members, directives);
                }
            }
        }

        private static void Collect(SyntaxList<UsingDirectiveSyntax> usings, List<AliasDirective> directives)
        {
            foreach (UsingDirectiveSyntax usingDirective in usings)
            {
                if (usingDirective.Alias is null)
                {
                    continue;
                }

                string name = usingDirective.Alias.Name.Identifier.ValueText;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string[] identifiers = usingDirective.NamespaceOrType
                    .DescendantTokens()
                    .Where(static token => token.IsKind(SyntaxKind.IdentifierToken))
                    .Select(static token => token.ValueText)
                    .ToArray();
                directives.Add(new AliasDirective(name, identifiers));
            }
        }
    }
}
