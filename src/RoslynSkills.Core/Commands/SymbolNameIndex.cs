using Microsoft.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// In-memory simple-name index of the source types and members declared in one <see cref="Compilation"/>, used by
/// <c>--symbol Type.Member</c> resolution instead of scanning <c>SymbolFinder.FindDeclarationsAsync</c> per project.
/// <para>
/// The index is keyed by <see cref="Compilation"/> instance in a <see cref="ConditionalWeakTable{TKey, TValue}"/>, so it
/// is incremental by construction: a hot workspace keeps the same compilation for every project an edit did not touch
/// (those keep their index), an edit to a document or a reference gives the changed project and its dependents a new
/// compilation (re-indexed on first use), and an index dies with its compilation. A stale symbol is therefore never
/// served after an edit.
/// </para>
/// <para>
/// It also contains members Roslyn's declaration index omits: synthesized positional-record properties
/// (<c>record DeferredCatalogCommit(..., Kind)</c>), constructors, and other implicitly declared members.
/// </para>
/// </summary>
internal sealed class SymbolNameIndex
{
    private static readonly ConditionalWeakTable<Compilation, Lazy<SymbolNameIndex>> Cache = new();
    private static readonly IReadOnlyList<ISymbol> NoSymbols = Array.Empty<ISymbol>();

    // Types plus explicitly declared members (the set FindDeclarationsAsync(TypeAndMember) reports).
    private readonly Dictionary<string, List<ISymbol>> declaredByName;

    // Every source type by simple name (parent lookup for Type.Member queries).
    private readonly Dictionary<string, List<ISymbol>> typesByName;

    // Every member, including implicit ones (synthesized record properties, constructors); the unqualified fallback.
    private readonly Dictionary<string, List<ISymbol>> allMembersByName;

    private SymbolNameIndex(
        Dictionary<string, List<ISymbol>> declaredByName,
        Dictionary<string, List<ISymbol>> typesByName,
        Dictionary<string, List<ISymbol>> allMembersByName,
        int symbolCount)
    {
        this.declaredByName = declaredByName;
        this.typesByName = typesByName;
        this.allMembersByName = allMembersByName;
        SymbolCount = symbolCount;
    }

    /// <summary>Number of indexed symbols (types and members), for diagnostics and tests.</summary>
    public int SymbolCount { get; }

    /// <summary>Returns the index of <paramref name="compilation"/>, building it on first use.</summary>
    public static SymbolNameIndex GetOrCreate(Compilation compilation, CancellationToken cancellationToken = default)
    {
        Lazy<SymbolNameIndex> lazy = Cache.GetValue(
            compilation,
            static c => new Lazy<SymbolNameIndex>(() => Build(c), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    /// <summary>Whether <paramref name="compilation"/> already has a built index (diagnostics and tests).</summary>
    internal static bool IsCached(Compilation compilation)
        => Cache.TryGetValue(compilation, out Lazy<SymbolNameIndex>? lazy) && lazy.IsValueCreated;

    /// <summary>Source types and explicitly declared members whose simple name is <paramref name="name"/>.</summary>
    public IReadOnlyList<ISymbol> FindDeclared(string name)
        => declaredByName.TryGetValue(name, out List<ISymbol>? list) ? list : NoSymbols;

    /// <summary>Source named types whose simple name is <paramref name="name"/>.</summary>
    public IReadOnlyList<ISymbol> FindTypes(string name)
        => typesByName.TryGetValue(name, out List<ISymbol>? list) ? list : NoSymbols;

    /// <summary>Every member (implicit ones included) whose simple name is <paramref name="name"/>.</summary>
    public IReadOnlyList<ISymbol> FindAnyMember(string name)
        => allMembersByName.TryGetValue(name, out List<ISymbol>? list) ? list : NoSymbols;

    private static SymbolNameIndex Build(Compilation compilation)
    {
        Dictionary<string, List<ISymbol>> declared = new(StringComparer.Ordinal);
        Dictionary<string, List<ISymbol>> types = new(StringComparer.Ordinal);
        Dictionary<string, List<ISymbol>> members = new(StringComparer.Ordinal);
        int count = 0;

        static void Add(Dictionary<string, List<ISymbol>> map, string name, ISymbol symbol)
        {
            if (!map.TryGetValue(name, out List<ISymbol>? list))
            {
                list = new List<ISymbol>(1);
                map[name] = list;
            }

            list.Add(symbol);
        }

        void VisitType(INamedTypeSymbol type)
        {
            if (!HasSourceLocation(type))
            {
                return;
            }

            count++;
            Add(types, type.Name, type);
            Add(declared, type.Name, type);

            foreach (ISymbol member in type.GetMembers())
            {
                if (member is INamedTypeSymbol nested)
                {
                    VisitType(nested);
                    continue;
                }

                // Accessors are reported through their property/event, never by their `get_X` name.
                if (member is IMethodSymbol { AssociatedSymbol: not null })
                {
                    continue;
                }

                count++;
                foreach (string key in GetIndexKeys(member))
                {
                    Add(members, key, member);
                    if (!member.IsImplicitlyDeclared && member is not IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
                    {
                        Add(declared, key, member);
                    }
                }
            }
        }

        void VisitNamespace(INamespaceSymbol ns)
        {
            foreach (ISymbol member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    VisitNamespace(childNamespace);
                }
                else if (member is INamedTypeSymbol type)
                {
                    VisitType(type);
                }
            }
        }

        VisitNamespace(compilation.Assembly.GlobalNamespace);
        return new SymbolNameIndex(declared, types, members, count);
    }

    /// <summary>
    /// The simple name a member is found by. An explicit interface implementation (<c>IFoo.Bar</c>) is also found by
    /// its leaf name, as Roslyn's declaration index does.
    /// </summary>
    private static IEnumerable<string> GetIndexKeys(ISymbol member)
    {
        string name = member.Name;
        yield return name;

        int lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && lastDot < name.Length - 1 && !string.Equals(name, ".ctor", StringComparison.Ordinal) && !string.Equals(name, ".cctor", StringComparison.Ordinal))
        {
            yield return name[(lastDot + 1)..];
        }
    }

    private static bool HasSourceLocation(ISymbol symbol)
    {
        foreach (Location location in symbol.Locations)
        {
            if (location.IsInSource)
            {
                return true;
            }
        }

        return false;
    }
}
