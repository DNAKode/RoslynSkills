using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.Core.Tests;

/// <summary>
/// Pass 3: per-compilation <see cref="SymbolNameIndex"/>, selective (pre-filtered) call-site discovery for
/// <c>nav.find_invocations</c>, and lazy <c>nav.call_hierarchy</c> with interface-implementation callers (<c>854z</c>).
/// </summary>
public sealed class SymbolIndexAndLazyHierarchyTests
{
    private const string RecordSource = """
        namespace Core.Transactions;

        public enum CommitKind { QueryDef, TableDefAppend }

        public sealed record DeferredCatalogCommit(int Id, CommitKind Kind = CommitKind.QueryDef);

        public interface IStore { void Save(int id); }

        public sealed class Catalog : IStore, System.IDisposable
        {
            public sealed class Entry { public int Value; }
            public void Save(int id) { }
            void System.IDisposable.Dispose() { }
        }
        """;

    // ---- (a) SymbolNameIndex -----------------------------------------------------------------------------------

    [Fact]
    public async Task SymbolNameIndex_ResolvesPositionalRecordPropertyTypesNestedAndExplicitMembers()
    {
        (AdhocWorkspace workspace, Project project, _) = CreateProject("P1", ("Record.cs", RecordSource));
        using (workspace)
        {
            Solution solution = workspace.CurrentSolution;

            List<ISymbol> qualified = await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "DeferredCatalogCommit.Kind", CancellationToken.None);
            ISymbol kind = Assert.Single(qualified);
            Assert.Equal(SymbolKind.Property, kind.Kind);

            List<ISymbol> full = await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "Core.Transactions.DeferredCatalogCommit.Kind", CancellationToken.None);
            Assert.Single(full);

            // Unqualified fallback finds the synthesized property that a declaration scan omits.
            List<ISymbol> unqualified = await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "Kind", CancellationToken.None);
            Assert.Contains(unqualified, symbol => symbol is IPropertySymbol { ContainingType.Name: "DeferredCatalogCommit" });

            // Nested type, method and constructor lookups.
            Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "Catalog.Entry", CancellationToken.None));
            Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "Catalog.Save", CancellationToken.None));
            Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "Catalog.Save(int)", CancellationToken.None));
            Assert.Empty(await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "Catalog.Save(string)", CancellationToken.None));
            Assert.NotEmpty(await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "DeferredCatalogCommit..ctor", CancellationToken.None));

            // Explicit interface implementation is found by its leaf name, as Roslyn's declaration index does.
            Assert.Contains(
                await SymbolQueryResolution.ResolveSymbolsByNameAsync(solution, "Dispose", CancellationToken.None),
                symbol => symbol is IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation });

            Compilation? compilation = await solution.GetProject(project.Id)!.GetCompilationAsync();
            Assert.True(SymbolNameIndex.IsCached(compilation!));
        }
    }

    [Fact]
    public async Task SymbolNameIndex_IsRebuiltOnDocumentEditAndKeptForUntouchedProjects()
    {
        const string edited = """
            namespace Core.Transactions;

            public sealed record DeferredCatalogCommit(int Id, CommitKind Kind = CommitKind.QueryDef, string Note = "");
            public sealed class Renamed { public void Added() { } }
            """;

        using AdhocWorkspace workspace = new();
        (Project p1, Document p1Doc) = AddProject(workspace, "P1", "Record.cs", RecordSource);
        (Project p2, _) = AddProject(workspace, "P2", "Other.cs", "public class Other { public void Stay() { } }");

        Solution before = workspace.CurrentSolution;
        Compilation c1Before = (await before.GetProject(p1.Id)!.GetCompilationAsync())!;
        Compilation c2Before = (await before.GetProject(p2.Id)!.GetCompilationAsync())!;

        Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(before, "Catalog.Save", CancellationToken.None));
        Assert.Empty(await SymbolQueryResolution.ResolveSymbolsByNameAsync(before, "Renamed.Added", CancellationToken.None));
        Assert.Empty(await SymbolQueryResolution.ResolveSymbolsByNameAsync(before, "DeferredCatalogCommit.Note", CancellationToken.None));
        SymbolNameIndex index1Before = SymbolNameIndex.GetOrCreate(c1Before);
        SymbolNameIndex index2Before = SymbolNameIndex.GetOrCreate(c2Before);

        // Edit one document of P1: P1 gets a new compilation (and index); P2 is untouched.
        Solution after = before.WithDocumentText(p1Doc.Id, SourceText.From(edited));
        Compilation c1After = (await after.GetProject(p1.Id)!.GetCompilationAsync())!;
        Compilation c2After = (await after.GetProject(p2.Id)!.GetCompilationAsync())!;

        Assert.NotSame(c1Before, c1After);
        Assert.NotSame(index1Before, SymbolNameIndex.GetOrCreate(c1After));
        Assert.Same(c2Before, c2After);
        Assert.Same(index2Before, SymbolNameIndex.GetOrCreate(c2After));

        // Never stale: removed symbols are gone, new ones are found, the old solution keeps answering for its own state.
        Assert.Empty(await SymbolQueryResolution.ResolveSymbolsByNameAsync(after, "Catalog.Save", CancellationToken.None));
        Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(after, "Renamed.Added", CancellationToken.None));
        Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(after, "DeferredCatalogCommit.Note", CancellationToken.None));
        Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(after, "Other.Stay", CancellationToken.None));
        Assert.Single(await SymbolQueryResolution.ResolveSymbolsByNameAsync(before, "Catalog.Save", CancellationToken.None));
        Assert.Empty(await SymbolQueryResolution.ResolveSymbolsByNameAsync(before, "Renamed.Added", CancellationToken.None));
    }

    // ---- (b) selective call-site discovery ---------------------------------------------------------------------

    private const string PrefilterWidgetSource = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using Alias = Demo.Widget;
        using static Demo.Util;

        namespace Demo;

        public class Widget
        {
            public Widget() { }
            public Widget(int x) { }
            public int Run<T>(T value) => 1;
            public int Run(int value) => 2;
            public void Stop() { }
        }

        public class Child : Widget
        {
            public Child() : base(3) { }
            public Child(string s) : this() { }
        }

        public static class Util
        {
            public static int Make(int n) => n;
            public static int Ext(this Widget w) => 1;
        }

        public sealed class Bag : IEnumerable
        {
            public void Add(int x) { }
            public IEnumerator GetEnumerator() => throw new NotImplementedException();
        }

        public class Consumer
        {
            private readonly Func<int, int> f = Make;

            public void Go(Widget w)
            {
                var a = new Widget();
                Alias b = new Alias(5);
                Widget c = new(7);
                var d = new Child();
                var d2 = new Child("x");
                w.Run(1);
                w.Run<string>("s");
                w?.Stop();
                w.Ext();
                Util.Ext(w);
                var e = new Bag { 1, 2 };
                Make(3);
                Action act = w.Stop;
                act();
            }
        }
        """;

    // Spells neither `Widget` nor any method name, but constructs it through a global alias declared elsewhere.
    private const string GlobalAliasSource = "global using Alias2 = Demo.Widget;\n";

    private const string AliasOnlyConsumerSource = """
        namespace Other;

        public class AliasOnly
        {
            public object Build() => new Alias2();
        }
        """;

    private const string UnrelatedSource = """
        namespace Other;

        public class Unrelated
        {
            public int Compute(int value) => value * 2;
        }
        """;

    private static IReadOnlyList<(string Path, string Source)> PrefilterSources => new[]
    {
        ("Widget.cs", PrefilterWidgetSource),
        ("GlobalAlias.cs", GlobalAliasSource),
        ("AliasOnly.cs", AliasOnlyConsumerSource),
        ("Unrelated.cs", UnrelatedSource),
    };

    [Fact]
    public void CallSiteLookup_SelectiveDiscovery_MatchesWholeTreeAnalysisForEveryTarget()
    {
        HashSet<string> wholeTree = CollectMatches(targeted: false, includeObjectCreations: true, out int targetCount);
        HashSet<string> selective = CollectMatches(targeted: true, includeObjectCreations: true, out _);

        Assert.True(targetCount >= 10, $"Expected many targets, saw {targetCount}.");
        Assert.True(wholeTree.Count >= 20, $"Expected many call sites, saw {wholeTree.Count}.");
        Assert.Equal(wholeTree.OrderBy(x => x, StringComparer.Ordinal), selective.OrderBy(x => x, StringComparer.Ordinal));

        HashSet<string> wholeTreeNoCreations = CollectMatches(targeted: false, includeObjectCreations: false, out _);
        HashSet<string> selectiveNoCreations = CollectMatches(targeted: true, includeObjectCreations: false, out _);
        Assert.Equal(wholeTreeNoCreations.OrderBy(x => x, StringComparer.Ordinal), selectiveNoCreations.OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(wholeTreeNoCreations.Count < wholeTree.Count);

        // The edge cases named by the pass are all really present.
        Assert.Contains(selective, key => key.StartsWith("Demo.Util.Make(int)->Widget.cs", StringComparison.Ordinal));
        Assert.Contains(selective, key => key.Contains("Demo.Widget.Stop()", StringComparison.Ordinal) && key.EndsWith(":method_group", StringComparison.Ordinal));
        Assert.Contains(selective, key => key.StartsWith("Demo.Widget.Widget(int)->Widget.cs", StringComparison.Ordinal));
        Assert.Contains(selective, key => key.StartsWith("Demo.Widget.Widget()->AliasOnly.cs", StringComparison.Ordinal));
        Assert.Contains(selective, key => key.StartsWith("Demo.Util.Ext(Demo.Widget)->Widget.cs", StringComparison.Ordinal));
        Assert.Contains(selective, key => key.StartsWith("Demo.Widget.Run<T>(T)->Widget.cs", StringComparison.Ordinal));
        Assert.Contains(selective, key => key.StartsWith("Demo.Widget.Run(int)->Widget.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void CallSiteAnalysis_DeclarationScopedDiscovery_MatchesWholeTreeCallersForEveryMethod()
    {
        const string source = """
            using System;
            namespace Demo;

            public static class Helper
            {
                public static int Make(int n) => n;
            }

            public class Holder
            {
                private int field = Helper.Make(0);

                public Holder() : this(1) { }
                public Holder(int seed) { Helper.Make(seed); }

                public int Expr(int x = 3) => Helper.Make(x);

                public int Body(int x)
                {
                    int Local(int y) => Helper.Make(y);
                    Func<int, int> lambda = z => Helper.Make(z);
                    var made = new Holder(x);
                    return Local(lambda(made.Expr()));
                }

                public int Prop
                {
                    get { return Helper.Make(2); }
                    set { Helper.Make(value); }
                }
            }
            """;

        static List<string> Collect(bool declarationScoped)
        {
            CSharpCompilation compilation = CSharpCompilation.Create(
                "DeclScoped",
                new[] { CSharpSyntaxTree.ParseText(source, path: "Holder.cs") },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
            SyntaxTree tree = compilation.SyntaxTrees.Single();
            SemanticModel model = compilation.GetSemanticModel(tree);
            List<string> keys = new();
            foreach (SyntaxNode declaration in tree.GetRoot().DescendantNodes().Where(CallSiteAnalysis.IsSelfContainedDeclaration))
            {
                ISymbol? declared = model.GetDeclaredSymbol(declaration);
                Assert.NotNull(declared);
                IReadOnlyList<CallSiteAnalysis.CallSite> sites = declarationScoped
                    ? CallSiteAnalysis.GetCallSitesInDeclarations(compilation, tree, new[] { declaration }, includeObjectCreations: true, CancellationToken.None)
                    : CallSiteAnalysis.GetCallSites(compilation, tree, includeObjectCreations: true, CancellationToken.None);
                foreach (CallSiteAnalysis.CallSite site in sites)
                {
                    if (SymbolEqualityComparer.Default.Equals(site.caller, declared))
                    {
                        keys.Add($"{declared.ToDisplayString()}->{site.callee.ToDisplayString()}@{site.node.SpanStart}:{site.call_kind}:{site.via_lambda}");
                    }
                }
            }

            return keys.Distinct().Order(StringComparer.Ordinal).ToList();
        }

        List<string> wholeTree = Collect(declarationScoped: false);
        List<string> scoped = Collect(declarationScoped: true);

        Assert.True(wholeTree.Count >= 8, $"Expected many caller edges, saw {wholeTree.Count}.");
        Assert.Equal(wholeTree, scoped);
    }

    [Fact]
    public void CallSiteLookup_ForOrdinaryMethod_BindsOnlyTreesThatSpellTheName()
    {
        CSharpCompilation compilation = BuildPrefilterCompilation();
        INamedTypeSymbol widget = compilation.GetTypeByMetadataName("Demo.Widget")!;
        IMethodSymbol stop = widget.GetMembers("Stop").OfType<IMethodSymbol>().Single();

        CallSiteLookup lookup = CallSiteLookup.ForTarget(stop, new[] { compilation });
        Assert.True(lookup.Targeted);

        Dictionary<string, bool> mentions = compilation.SyntaxTrees.ToDictionary(tree => Path.GetFileName(tree.FilePath), tree => lookup.MightMention(tree));
        Assert.True(mentions["Widget.cs"]);
        Assert.False(mentions["Unrelated.cs"]);
        Assert.False(mentions["AliasOnly.cs"]);

        int found = 0;
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            foreach (CallSiteAnalysis.CallSite site in CallSiteAnalysis.GetCallSitesForLookup(compilation, tree, lookup, includeObjectCreations: true, CancellationToken.None))
            {
                if (SolutionSearchScope.MatchesInvocationCallee(site.callee, stop))
                {
                    found++;
                }
            }

            // A narrowed cold lookup never builds the whole-tree cache.
            Assert.False(CallSiteAnalysis.IsCached(compilation, tree));
        }

        Assert.Equal(2, found); // `w?.Stop()` and the method group `w.Stop`
    }

    [Fact]
    public void CallSiteLookup_ForConstructor_AddsAliasesTargetTypedNewAndInitializers()
    {
        CSharpCompilation compilation = BuildPrefilterCompilation();
        INamedTypeSymbol widget = compilation.GetTypeByMetadataName("Demo.Widget")!;
        CallSiteLookup lookup = CallSiteLookup.ForTarget(widget, new[] { compilation });

        Assert.True(lookup.Targeted);
        Assert.Contains("Widget", lookup.ConstructorTypeNames);
        Assert.Contains("Alias", lookup.ConstructorTypeNames);
        Assert.Contains("Alias2", lookup.ConstructorTypeNames);

        SyntaxTree aliasOnly = compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("AliasOnly.cs", StringComparison.Ordinal));
        SyntaxTree unrelated = compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("Unrelated.cs", StringComparison.Ordinal));
        Assert.True(lookup.MightMention(aliasOnly));
        Assert.False(lookup.MightMention(unrelated));
    }

    [Theory]
    [InlineData("Add")]
    [InlineData("GetEnumerator")]
    [InlineData("Deconstruct")]
    public void CallSiteLookup_ImplicitlyCalledNames_KeepEveryTreeACandidate(string methodName)
    {
        const string source = """
            public class Holder
            {
                public void Add(int x) { }
                public System.Collections.IEnumerator GetEnumerator() => null!;
                public void Deconstruct(out int a, out int b) { a = 1; b = 2; }
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ImplicitNames",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Holder.cs"), CSharpSyntaxTree.ParseText("public class Z { }", path: "Z.cs") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        IMethodSymbol method = compilation.GetTypeByMetadataName("Holder")!.GetMembers(methodName).OfType<IMethodSymbol>().Single();

        CallSiteLookup lookup = CallSiteLookup.ForTarget(method, new[] { compilation });

        Assert.False(lookup.Targeted);
        Assert.All(compilation.SyntaxTrees, tree => Assert.True(lookup.MightMention(tree)));
    }

    [Fact]
    public async Task FindInvocationsCommand_PreFilterEdgeCases_ReportEveryTrueCallSite()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-prefilter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string projectPath = await WriteProjectAsync(root, PrefilterSources);
            FindInvocationsCommand command = new();

            // Constructor of the type via alias, target-typed `new` and a `: base(...)` initializer.
            JsonElement widgetCtor = await RunAsync(command, new
            {
                symbol = "Widget..ctor(int)",
                workspace_path = projectPath,
                brief = true,
            });
            Assert.Equal(3, widgetCtor.GetProperty("total_matches").GetInt32()); // new Alias(5), new(7), : base(3)

            // The type as a target reports every constructor call, including one that only a global alias spells.
            JsonElement widgetType = await RunAsync(command, new { symbol = "Demo.Widget", workspace_path = projectPath, brief = true });
            string[] widgetFiles = ExtractFiles(widgetType);
            Assert.Equal(5, widgetType.GetProperty("total_matches").GetInt32());
            Assert.Contains("AliasOnly.cs", widgetFiles);
            Assert.Equal(4, widgetFiles.Count(file => file == "Widget.cs"));
            Assert.True(
                widgetType.GetProperty("searched_syntax_trees").GetInt32() < 4,
                "The unrelated tree must be filtered out before binding.");

            // `using static` call, method group conversion and generic/non-generic overloads.
            JsonElement make = await RunAsync(command, new { symbol = "Util.Make", workspace_path = projectPath, brief = true });
            Assert.Equal(2, make.GetProperty("total_matches").GetInt32()); // `f = Make` method group + Make(3)

            JsonElement genericRun = await RunAsync(command, new { symbol = "Widget.Run<T>", workspace_path = projectPath, brief = true });
            Assert.Equal(1, genericRun.GetProperty("total_matches").GetInt32());

            JsonElement plainRun = await RunAsync(command, new { symbol = "Widget.Run(int)", workspace_path = projectPath, brief = true });
            Assert.Equal(1, plainRun.GetProperty("total_matches").GetInt32());

            JsonElement stop = await RunAsync(command, new { symbol = "Widget.Stop", workspace_path = projectPath, brief = true });
            Assert.Equal(2, stop.GetProperty("total_matches").GetInt32()); // w?.Stop() + method group

            // Extension method, called both as an instance and as a plain static call.
            JsonElement ext = await RunAsync(command, new { symbol = "Util.Ext", workspace_path = projectPath, brief = true });
            Assert.Equal(2, ext.GetProperty("total_matches").GetInt32());
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- (c) 854z: call_hierarchy incoming callers of interface implementations --------------------------------

    [Fact]
    public async Task CallHierarchy_IncomingOnInterfaceImplementation_IncludesCallersOfImplementedInterfaceMember()
    {
        const string source = """
            namespace Demo;

            public interface IStore { void Save(int id); }

            public sealed class FileStore : IStore
            {
                public void Save(int id) { }
            }

            public sealed class ExplicitStore : IStore
            {
                void IStore.Save(int id) { }
            }

            public sealed class Consumer
            {
                public void Persist(IStore store) { store.Save(1); }
                public void Direct(FileStore store) { store.Save(2); }
            }

            public static class Program
            {
                public static void Main() { new Consumer().Persist(new FileStore()); }
            }
            """;
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-854z-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string projectPath = await WriteProjectAsync(root, new[] { ("Demo.cs", source) });
            string demoPath = Path.Combine(root, "Demo.cs");
            CallHierarchyCommand command = new();

            // Implicit implementation anchored by file position.
            (int line, int column) = FindPosition(source, "public void Save(int id) { }", "Save");
            JsonElement implicitResult = await RunAsync(command, new
            {
                file_path = demoPath,
                line,
                column,
                direction = "incoming",
                max_depth = 2,
                workspace_path = projectPath,
                brief = true,
            });
            string implicitNodes = implicitResult.GetProperty("nodes").GetRawText();
            Assert.Contains("Consumer.Persist", implicitNodes); // via IStore.Save
            Assert.Contains("Consumer.Direct", implicitNodes);  // direct call of the concrete method
            Assert.Contains("Program.Main", implicitNodes);     // depth 2
            JsonElement[] implicitEdges = implicitResult.GetProperty("edges").EnumerateArray().ToArray();
            Assert.Contains(implicitEdges, edge => edge.GetProperty("implemented_member").GetString()?.Contains("IStore.Save", StringComparison.Ordinal) == true);
            Assert.Contains(implicitEdges, edge => edge.GetProperty("implemented_member").ValueKind == JsonValueKind.Null);

            // Explicit implementation: callers can only reach it through the interface.
            (int explicitLine, int explicitColumn) = FindPosition(source, "void IStore.Save(int id) { }", "Save");
            JsonElement explicitResult = await RunAsync(command, new
            {
                file_path = demoPath,
                line = explicitLine,
                column = explicitColumn,
                direction = "incoming",
                max_depth = 1,
                workspace_path = projectPath,
                brief = true,
            });
            string explicitNodes = explicitResult.GetProperty("nodes").GetRawText();
            Assert.Contains("Consumer.Persist", explicitNodes);
            Assert.DoesNotContain("Consumer.Direct", explicitNodes);

            // The interface member itself still lists its direct callers only.
            (int ifaceLine, int ifaceColumn) = FindPosition(source, "void Save(int id); }", "Save");
            JsonElement interfaceResult = await RunAsync(command, new
            {
                file_path = demoPath,
                line = ifaceLine,
                column = ifaceColumn,
                direction = "incoming",
                max_depth = 1,
                workspace_path = projectPath,
                brief = true,
            });
            string interfaceNodes = interfaceResult.GetProperty("nodes").GetRawText();
            Assert.Contains("Consumer.Persist", interfaceNodes);
            Assert.DoesNotContain("Consumer.Direct", interfaceNodes);

            // Outgoing discovery is scoped to the declaring tree and unchanged.
            (int mainLine, int mainColumn) = FindPosition(source, "public static void Main()", "Main");
            JsonElement outgoing = await RunAsync(command, new
            {
                file_path = demoPath,
                line = mainLine,
                column = mainColumn,
                direction = "outgoing",
                max_depth = 2,
                workspace_path = projectPath,
                brief = true,
            });
            string outgoingNodes = outgoing.GetProperty("nodes").GetRawText();
            Assert.Contains("Consumer.Persist", outgoingNodes);
            Assert.Contains("IStore.Save", outgoingNodes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static HashSet<string> CollectMatches(bool targeted, bool includeObjectCreations, out int targetCount)
    {
        // A fresh compilation per run keeps every cache cold.
        CSharpCompilation compilation = BuildPrefilterCompilation();
        List<ISymbol> targets = new();
        foreach (string typeName in new[] { "Demo.Widget", "Demo.Child", "Demo.Util", "Demo.Bag" })
        {
            INamedTypeSymbol type = compilation.GetTypeByMetadataName(typeName)!;
            if (typeName != "Demo.Util")
            {
                targets.Add(type);
            }

            targets.AddRange(type.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor));
        }

        targetCount = targets.Count;
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (ISymbol target in targets)
        {
            CallSiteLookup lookup = targeted
                ? CallSiteLookup.ForTarget(target, new[] { compilation })
                : CallSiteLookup.All;
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                if (targeted && !lookup.MightMention(tree))
                {
                    continue;
                }

                IReadOnlyList<CallSiteAnalysis.CallSite> sites = targeted
                    ? CallSiteAnalysis.GetCallSitesForLookup(compilation, tree, lookup, includeObjectCreations, CancellationToken.None)
                    : CallSiteAnalysis.GetCallSites(compilation, tree, includeObjectCreations, CancellationToken.None);
                foreach (CallSiteAnalysis.CallSite site in sites)
                {
                    if (SolutionSearchScope.MatchesInvocationCallee(site.callee, target))
                    {
                        keys.Add($"{target.ToDisplayString()}->{Path.GetFileName(tree.FilePath)}:{site.node.SpanStart}:{site.call_kind}");
                    }
                }
            }
        }

        return keys;
    }

    private static CSharpCompilation BuildPrefilterCompilation()
        => CSharpCompilation.Create(
            "PrefilterAsm",
            PrefilterSources.Select(entry => CSharpSyntaxTree.ParseText(entry.Source, path: entry.Path)).ToArray(),
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static (AdhocWorkspace Workspace, Project Project, Document Document) CreateProject(string name, (string Path, string Source) file)
    {
        AdhocWorkspace workspace = new();
        (Project project, Document document) = AddProject(workspace, name, file.Path, file.Source);
        return (workspace, project, document);
    }

    private static (Project Project, Document Document) AddProject(AdhocWorkspace workspace, string name, string fileName, string source)
    {
        ProjectInfo info = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        Project project = workspace.AddProject(info);
        Document document = workspace.AddDocument(project.Id, fileName, SourceText.From(source));
        return (workspace.CurrentSolution.GetProject(project.Id)!, workspace.CurrentSolution.GetDocument(document.Id)!);
    }

    private static async Task<string> WriteProjectAsync(string root, IEnumerable<(string Path, string Source)> files)
    {
        string projectPath = Path.Combine(root, "Harness.csproj");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        foreach ((string path, string source) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(root, path), source);
        }

        return projectPath;
    }

    private static async Task<JsonElement> RunAsync(IAgentCommand command, object payload)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement input = doc.RootElement.Clone();
        IReadOnlyList<CommandError> validation = command.Validate(input);
        Assert.True(validation.Count == 0, string.Join("; ", validation.Select(e => $"{e.Code}: {e.Message}")));
        CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        return JsonSerializer.SerializeToElement(result.Data);
    }

    private static string[] ExtractFiles(JsonElement data)
        => data.GetProperty("matches").EnumerateArray()
            .Select(match => Path.GetFileName(match.GetProperty("file_path").GetString()!))
            .ToArray();

    private static (int Line, int Column) FindPosition(string source, string lineContains, string token)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(lineContains, StringComparison.Ordinal))
            {
                int index = lines[i].IndexOf(token, StringComparison.Ordinal);
                Assert.True(index >= 0, $"'{token}' not found in '{lines[i]}'.");
                return (i + 1, index + 1);
            }
        }

        throw new InvalidOperationException($"No line contains '{lineContains}'.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
