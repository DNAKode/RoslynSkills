using Microsoft.CodeAnalysis;

namespace RoslynSkills.Core.Commands;

/// <summary>One syntax tree to search, together with the compilation that gives it meaning.</summary>
internal sealed record SearchUnit(SyntaxTree Tree, Compilation Compilation, string FilePath);

/// <summary>
/// The syntax trees a usage search (references, invocations, call graph, impact) has to visit.
/// <see cref="Kind"/> is <c>solution</c> when the anchor file belongs to a loaded Roslyn solution and
/// <c>compilation</c> when only the anchor's own compilation is available (ad-hoc or project-less analysis).
/// </summary>
internal sealed record SearchScope(
    IReadOnlyList<SearchUnit> Units,
    string Kind,
    int ProjectCount)
{
    public bool IsSolutionWide => string.Equals(Kind, SolutionSearchScope.SolutionKind, StringComparison.Ordinal);

    public object ToPayload() => new
    {
        search_scope = Kind,
        searched_projects = ProjectCount,
        searched_syntax_trees = Units.Count,
    };
}

/// <summary>
/// Resolves search scopes over a whole <see cref="Solution"/>. Usage searches used to read
/// <c>analysis.Compilation</c> only, which is the single project that owns the anchor file; callers in sibling
/// projects were invisible even when the solution was fully loaded.
/// </summary>
internal static class SolutionSearchScope
{
    public const string SolutionKind = "solution";
    public const string CompilationKind = "compilation";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Trees that can mention <paramref name="target"/>: the target's own project plus every project that
    /// transitively depends on it. With no target (graph-building commands) every project is included.
    /// </summary>
    public static async Task<SearchScope> ResolveAsync(
        CommandFileAnalysis analysis,
        ISymbol? target,
        bool includeGenerated,
        CancellationToken cancellationToken)
    {
        Solution? solution = analysis.Document?.Project.Solution;
        if (solution is null)
        {
            return BuildCompilationScope(analysis, includeGenerated, cancellationToken);
        }

        List<Project> projects = SelectProjects(solution, analysis.Document!.Project, target);
        List<SearchUnit> units = new();
        HashSet<string> seenPaths = new(PathComparer);
        int searchedProjects = 0;

        foreach (Project project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            searchedProjects++;
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                string filePath = NormalizeTreePath(tree);
                if (!includeGenerated && CommandFileFilters.IsGeneratedPath(filePath))
                {
                    continue;
                }

                // A file linked into several projects (multi-targeting) is searched once, in the first project.
                if (!seenPaths.Add(filePath))
                {
                    continue;
                }

                units.Add(new SearchUnit(tree, compilation, filePath));
            }
        }

        return new SearchScope(units, SolutionKind, searchedProjects);
    }

    /// <summary>Finds the tree and compilation of a file anywhere in the loaded solution (or the anchor compilation).</summary>
    public static async Task<(SyntaxTree Tree, Compilation Compilation)?> FindTreeAsync(
        CommandFileAnalysis analysis,
        string filePath,
        CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        SyntaxTree? local = analysis.Compilation.SyntaxTrees.FirstOrDefault(tree =>
            PathComparer.Equals(NormalizeTreePath(tree), normalizedPath));
        if (local is not null)
        {
            return (local, analysis.Compilation);
        }

        Solution? solution = analysis.Document?.Project.Solution;
        if (solution is null)
        {
            return null;
        }

        foreach (Project project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document? document = project.Documents.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.FilePath) &&
                PathComparer.Equals(Path.GetFullPath(candidate.FilePath), normalizedPath));
            if (document is null)
            {
                continue;
            }

            SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (tree is not null && compilation is not null)
            {
                return (tree, compilation);
            }
        }

        return null;
    }

    /// <summary>
    /// Cheap text pre-filter: false only when the tree cannot mention <paramref name="name"/>.
    /// A null name means the symbol can be used without spelling it (constructors, accessors, operators).
    /// </summary>
    public static bool MightMention(SearchUnit unit, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        return unit.Tree.GetText().ToString().Contains(name, StringComparison.Ordinal);
    }

    /// <summary>The identifier that must appear in a file for it to use <paramref name="symbol"/>, or null when unknown.</summary>
    public static string? GetSearchName(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction } method:
                return method.Name;
            case IPropertySymbol or IFieldSymbol or IEventSymbol:
                return symbol.Name;
            // Named types can be instantiated via target-typed `new(...)` or type aliases without spelling their name.
            default:
                return null;
        }
    }

    /// <summary>Whether <paramref name="candidate"/> refers to the same semantic symbol as <paramref name="target"/>.</summary>
    public static bool IsSameSymbol(ISymbol? candidate, ISymbol target)
    {
        if (candidate is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(candidate, target) ||
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition))
        {
            return true;
        }

        if (candidate is IMethodSymbol candidateMethod && candidateMethod.ReducedFrom is { } candidateReduced)
        {
            if (SymbolEqualityComparer.Default.Equals(candidateReduced, target) ||
                SymbolEqualityComparer.Default.Equals(candidateReduced.OriginalDefinition, target.OriginalDefinition))
            {
                return true;
            }
        }

        if (target is IMethodSymbol targetMethod && targetMethod.ReducedFrom is { } targetReduced)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, targetReduced) ||
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, targetReduced.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a <c>SymbolFinder.FindReferencesAsync</c> group belongs to <paramref name="target"/>:
    /// the target itself, or any constructor of <paramref name="target"/> when the target is a named type
    /// (so <c>new Type(...)</c> and target-typed <c>new(...)</c> object creations are included).
    /// </summary>
    public static bool MatchesReferenceDefinition(ISymbol? definition, ISymbol target, out bool isConstructorOfTargetType)
    {
        isConstructorOfTargetType = false;
        if (definition is null)
        {
            return false;
        }

        if (IsSameSymbol(definition, target))
        {
            return true;
        }

        if (target is INamedTypeSymbol targetType &&
            definition is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor &&
            IsSameSymbol(ctor.ContainingType, targetType))
        {
            isConstructorOfTargetType = true;
            return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="symbol"/> can be invoked or constructed (a method, constructor, or instantiable type).</summary>
    public static bool IsInvocationTarget(ISymbol? symbol)
        => symbol is IMethodSymbol
            || symbol is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Delegate };

    /// <summary>Whether a call site's <paramref name="callee"/> invokes <paramref name="target"/> (including constructors of a target type).</summary>
    public static bool MatchesInvocationCallee(IMethodSymbol? callee, ISymbol target)
    {
        if (callee is null)
        {
            return false;
        }

        if (target is IMethodSymbol targetMethod)
        {
            return IsSameSymbol(callee, targetMethod);
        }

        if (target is INamedTypeSymbol targetType)
        {
            return callee.MethodKind == MethodKind.Constructor &&
                   IsSameSymbol(callee.ContainingType, targetType);
        }

        return false;
    }


    private static List<Project> SelectProjects(Solution solution, Project anchorProject, ISymbol? target)
    {
        Project? definingProject = target?.ContainingAssembly is { } assembly
            ? solution.GetProject(assembly)
            : null;

        IEnumerable<Project> selected;
        if (target is null || definingProject is null)
        {
            // Graph commands and symbols defined outside the solution (metadata): nothing narrows the search.
            selected = solution.Projects;
        }
        else
        {
            HashSet<ProjectId> ids = new() { definingProject.Id };
            foreach (ProjectId dependent in solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(definingProject.Id))
            {
                ids.Add(dependent);
            }

            selected = solution.Projects.Where(project => ids.Contains(project.Id));
        }

        // Anchor project first so its own trees win when a file is linked into several projects.
        return selected
            .OrderBy(project => project.Id == anchorProject.Id ? 0 : 1)
            .ThenBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SearchScope BuildCompilationScope(
        CommandFileAnalysis analysis,
        bool includeGenerated,
        CancellationToken cancellationToken)
    {
        List<SearchUnit> units = new();
        foreach (SyntaxTree tree in analysis.Compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string filePath = string.IsNullOrWhiteSpace(tree.FilePath)
                ? analysis.FilePath
                : NormalizeTreePath(tree);
            if (!includeGenerated && CommandFileFilters.IsGeneratedPath(filePath))
            {
                continue;
            }

            units.Add(new SearchUnit(tree, analysis.Compilation, filePath));
        }

        return new SearchScope(units, CompilationKind, 1);
    }

    private static string NormalizeTreePath(SyntaxTree tree)
        => string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);
}
