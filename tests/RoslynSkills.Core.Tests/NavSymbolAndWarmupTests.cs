using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.Core.Tests;

public sealed class NavSymbolAndWarmupTests
{
    private const string CoreCommitSource = """
        namespace Core.Transactions;

        public enum CommitKind
        {
            QueryDef,
            TableDefAppend,
        }

        public sealed record DeferredCommit(
            int Id,
            CommitKind Kind = CommitKind.QueryDef);

        public sealed record ExplicitPropertyRecord(int Value)
        {
            public int Value { get; init; } = Value;
        }
        """;

    private const string CoreServicesSource = """
        namespace Core.Services;

        public interface IStore
        {
            void Save(int id);
        }

        public abstract class BaseWorker
        {
            public virtual int Compute(int x) => x;
        }

        public sealed class Service
        {
            public int Run(int x) => x + 1;
            public static Service Create() => new Service();
            public string Format(int value) => value.ToString();
            public string Format(string value) => value.Trim();
            public void SharedName() { }
        }

        public sealed class OtherService
        {
            public void SharedName() { }
        }

        public sealed class Widget
        {
            public Widget(int count) => Count = count;
            public int Count { get; }
        }
        """;

    private const string AppConsumerSource = """
        using System;
        using Core.Services;
        using Core.Transactions;

        namespace App;

        public sealed class FileStore : IStore
        {
            public void Save(int id) { }
        }

        public sealed class FastWorker : BaseWorker
        {
            public override int Compute(int x) => x * 2;
        }

        public sealed class Consumer
        {
            public static readonly Func<Service> Factory = Service.Create;

            public bool IsTableDef(DeferredCommit? commit)
            {
                if (commit is { Kind: CommitKind.TableDefAppend })
                {
                    return true;
                }

                if (commit is { Kind: CommitKind.QueryDef, Id: > 0 })
                {
                    return false;
                }

                return commit?.Kind == CommitKind.TableDefAppend;
            }

            public DeferredCommit CreateTableCommit()
                => new DeferredCommit(42, Kind: CommitKind.TableDefAppend);

            public string UseService(Service service)
            {
                int v = service.Run(3);
                string a = service.Format(v);
                string b = service.Format("hello");
                Widget w1 = new Widget(1);
                Widget w2 = new(2);
                return $"{a}:{b}:{w1.Count}:{w2.Count}";
            }
        }
        """;

    [Fact]
    public async Task PositionalRecordProperty_ReferencesMatchAcrossSymbolDeclarationAndPropertyPatterns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-record-refs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string? handle = null;
        try
        {
            (string solutionPath, string commitPath, _, string consumerPath) = await CreateTwoProjectSolutionAsync(root);
            ICommandRegistry registry = DefaultRegistryFactory.Create();
            handle = await PreloadWorkspaceAsync(registry, solutionPath);

            // 1. --symbol DeferredCommit.Kind (short Type.Member)
            JsonElement byShortSymbol = await ExecuteOkAsync(registry, "nav.find_references", new
            {
                workspace_handle = handle,
                symbol = "DeferredCommit.Kind",
            });

            // 2. --symbol Core.Transactions.DeferredCommit.Kind (full namespace)
            JsonElement byFullSymbol = await ExecuteOkAsync(registry, "nav.find_references", new
            {
                workspace_handle = handle,
                symbol = "Core.Transactions.DeferredCommit.Kind",
            });

            // 3. Positional parameter declaration in DeferredCommit.cs
            (int declLine, int declCol) = FindTokenPosition(CoreCommitSource, "CommitKind Kind =", " Kind =");
            declCol += 1;
            JsonElement byDecl = await ExecuteOkAsync(registry, "nav.find_references", new
            {
                workspace_handle = handle,
                file_path = commitPath,
                line = declLine,
                column = declCol,
                require_workspace = true,
            });

            // 4. Property pattern `{ Kind: CommitKind.TableDefAppend }` in Consumer.cs
            (int patternLine, int patternCol) = FindTokenPosition(AppConsumerSource, "commit is { Kind: CommitKind.TableDefAppend }", "Kind:");
            JsonElement byPattern = await ExecuteOkAsync(registry, "nav.find_references", new
            {
                workspace_handle = handle,
                file_path = consumerPath,
                line = patternLine,
                column = patternCol,
                require_workspace = true,
            });

            string[] shortCoords = ExtractMatchCoords(byShortSymbol);
            string[] fullCoords = ExtractMatchCoords(byFullSymbol);
            string[] declCoords = ExtractMatchCoords(byDecl);
            string[] patternCoords = ExtractMatchCoords(byPattern);

            // Declaration + 2 property patterns + 1 member access + 1 named constructor argument = 5 total.
            Assert.Equal(5, shortCoords.Length);
            Assert.Equal(shortCoords, fullCoords);
            Assert.Equal(shortCoords, declCoords);
            Assert.Equal(shortCoords, patternCoords);

            Assert.Equal("Property", byShortSymbol.GetProperty("symbol").GetProperty("symbol_kind").GetString());
            Assert.Equal("Property", byDecl.GetProperty("symbol").GetProperty("symbol_kind").GetString());
            Assert.Contains(shortCoords, c => c.StartsWith("DeferredCommit.cs:", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(4, shortCoords.Count(c => c.StartsWith("Consumer.cs:", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await CloseWorkspaceAsync(handle);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void PositionalRecordCounterpart_DoesNotPairExplicitlyShadowedProperty()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(CoreCommitSource, path: "DeferredCommit.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestAsm",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        INamedTypeSymbol? explicitRecord = compilation.GetTypeByMetadataName("Core.Transactions.ExplicitPropertyRecord");
        Assert.NotNull(explicitRecord);

        IPropertySymbol valueProp = Assert.Single(explicitRecord!.GetMembers("Value").OfType<IPropertySymbol>());
        Assert.Null(SolutionSearchScope.TryGetPositionalRecordCounterpart(valueProp));

        INamedTypeSymbol? deferredRecord = compilation.GetTypeByMetadataName("Core.Transactions.DeferredCommit");
        Assert.NotNull(deferredRecord);
        IPropertySymbol kindProp = Assert.Single(deferredRecord!.GetMembers("Kind").OfType<IPropertySymbol>());
        IParameterSymbol? counterpart = SolutionSearchScope.TryGetPositionalRecordCounterpart(kindProp) as IParameterSymbol;
        Assert.NotNull(counterpart);
        Assert.Equal("Kind", counterpart!.Name);
        Assert.True(SymbolEqualityComparer.Default.Equals(kindProp, SolutionSearchScope.TryGetPositionalRecordCounterpart(counterpart)));
    }

    [Fact]
    public async Task NavCommands_SupportSymbolOption_IncludingOverloadsAndSiblingCommands()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-nav-symbol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string? handle = null;
        try
        {
            (string solutionPath, _, _, _) = await CreateTwoProjectSolutionAsync(root);
            ICommandRegistry registry = DefaultRegistryFactory.Create();
            handle = await PreloadWorkspaceAsync(registry, solutionPath);

            // nav.find_invocations --symbol (unambiguous method)
            JsonElement runInvocations = await ExecuteOkAsync(registry, "nav.find_invocations", new
            {
                workspace_handle = handle,
                symbol = "Service.Run",
                brief = true,
            });
            Assert.Equal(1, runInvocations.GetProperty("total_matches").GetInt32());
            Assert.Contains("Consumer.cs", runInvocations.GetProperty("matches")[0].GetProperty("file_path").GetString()!);

            // nav.find_invocations --symbol (method group conversion)
            JsonElement createInvocations = await ExecuteOkAsync(registry, "nav.find_invocations", new
            {
                workspace_handle = handle,
                symbol = "Service.Create",
                brief = true,
            });
            Assert.Equal(1, createInvocations.GetProperty("total_matches").GetInt32());
            Assert.Equal("method_group", createInvocations.GetProperty("matches")[0].GetProperty("call_kind").GetString());

            // nav.find_invocations --symbol (overloaded same-type method returns all overloads)
            JsonElement allFormats = await ExecuteOkAsync(registry, "nav.find_invocations", new
            {
                workspace_handle = handle,
                symbol = "Service.Format",
                brief = true,
            });
            Assert.Equal(2, allFormats.GetProperty("total_matches").GetInt32());
            Assert.Equal(2, allFormats.GetProperty("symbol").GetProperty("overload_count").GetInt32());
            Assert.Equal(2, allFormats.GetProperty("matched_overloads").GetArrayLength());

            // nav.find_invocations --symbol (parameter-type disambiguation picks one overload)
            JsonElement intFormat = await ExecuteOkAsync(registry, "nav.find_invocations", new
            {
                workspace_handle = handle,
                symbol = "Service.Format(int)",
                brief = true,
            });
            Assert.Equal(1, intFormat.GetProperty("total_matches").GetInt32());
            Assert.Contains("(int)", intFormat.GetProperty("matches")[0].GetProperty("symbol_display").GetString()!);

            JsonElement stringFormat = await ExecuteOkAsync(registry, "nav.find_invocations", new
            {
                workspace_handle = handle,
                symbol = "Service.Format(string)",
                brief = true,
            });
            Assert.Equal(1, stringFormat.GetProperty("total_matches").GetInt32());
            Assert.Contains("(string)", stringFormat.GetProperty("matches")[0].GetProperty("symbol_display").GetString()!);

            // nav.find_invocations --symbol (constructible type includes explicit + target-typed new)
            JsonElement widgetInvocations = await ExecuteOkAsync(registry, "nav.find_invocations", new
            {
                workspace_handle = handle,
                symbol = "Widget",
                brief = true,
            });
            Assert.Equal(2, widgetInvocations.GetProperty("total_matches").GetInt32());

            // nav.find_invocations --symbol (different types sharing a method name -> ambiguous_symbol)
            CommandExecutionResult ambiguous = await ExecuteRawAsync(registry, "nav.find_invocations", new
            {
                workspace_handle = handle,
                symbol = "SharedName",
            });
            Assert.False(ambiguous.Ok);
            Assert.Contains(ambiguous.Errors, e => e.Code == "ambiguous_symbol");

            // nav.find_implementations --symbol
            JsonElement storeImpls = await ExecuteOkAsync(registry, "nav.find_implementations", new
            {
                workspace_handle = handle,
                symbol = "IStore",
            });
            Assert.Equal(1, storeImpls.GetProperty("total_matches").GetInt32());
            Assert.Equal("App.FileStore", storeImpls.GetProperty("matches")[0].GetProperty("symbol_display").GetString());

            JsonElement saveImpls = await ExecuteOkAsync(registry, "nav.find_implementations", new
            {
                workspace_handle = handle,
                symbol = "IStore.Save",
            });
            Assert.Equal(1, saveImpls.GetProperty("total_matches").GetInt32());

            // nav.find_overrides --symbol
            JsonElement overrides = await ExecuteOkAsync(registry, "nav.find_overrides", new
            {
                workspace_handle = handle,
                symbol = "BaseWorker.Compute",
            });
            Assert.Equal(1, overrides.GetProperty("total_matches").GetInt32());
            Assert.Contains("FastWorker.Compute", overrides.GetProperty("matches")[0].GetProperty("symbol_display").GetString()!);

            // nav.go_to_definition --symbol (positional record property + overloaded method)
            JsonElement kindDef = await ExecuteOkAsync(registry, "nav.go_to_definition", new
            {
                workspace_handle = handle,
                symbol = "DeferredCommit.Kind",
                brief = true,
            });
            Assert.Equal(1, kindDef.GetProperty("total_definitions").GetInt32());
            Assert.EndsWith("DeferredCommit.cs", kindDef.GetProperty("definitions")[0].GetProperty("file_path").GetString()!, StringComparison.OrdinalIgnoreCase);

            JsonElement formatDefs = await ExecuteOkAsync(registry, "nav.go_to_definition", new
            {
                workspace_handle = handle,
                symbol = "Service.Format",
                brief = true,
            });
            Assert.Equal(2, formatDefs.GetProperty("total_definitions").GetInt32());

            JsonElement formatStringDef = await ExecuteOkAsync(registry, "nav.go_to_definition", new
            {
                workspace_handle = handle,
                symbol = "Service.Format(string)",
                brief = true,
            });
            Assert.Equal(1, formatStringDef.GetProperty("total_definitions").GetInt32());

            // nav.call_hierarchy --symbol
            JsonElement hierarchy = await ExecuteOkAsync(registry, "nav.call_hierarchy", new
            {
                workspace_handle = handle,
                symbol = "Service.Run",
                direction = "incoming",
                max_depth = 1,
                brief = true,
            });
            Assert.Contains("UseService", hierarchy.GetProperty("nodes").GetRawText());
        }
        finally
        {
            await CloseWorkspaceAsync(handle);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task HotWorkspaceWarmup_WarmsIdentifierIndexAndCallSiteCacheForSolutionTrees()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-warmup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string? handle = null;
        try
        {
            (string solutionPath, _, _, _) = await CreateTwoProjectSolutionAsync(root);
            ICommandRegistry registry = DefaultRegistryFactory.Create();
            handle = await PreloadWorkspaceAsync(registry, solutionPath);

            Assert.True(WorkspaceHostStoreProvider.Current.TryGet(handle, out HostedWorkspace? hosted));
            Solution solution = hosted!.Workspace.Solution!;

            // Await the background warmup kicked off by workspace.preload.
            await HotWorkspaceWarmup.WaitForWarmupAsync(solution).WaitAsync(TimeSpan.FromSeconds(60));

            int verifiedTrees = 0;
            foreach (Project project in solution.Projects)
            {
                Compilation? compilation = await project.GetCompilationAsync();
                Assert.NotNull(compilation);
                foreach (SyntaxTree tree in compilation!.SyntaxTrees)
                {
                    if (string.IsNullOrWhiteSpace(tree.FilePath) || CommandFileFilters.IsGeneratedPath(tree.FilePath))
                    {
                        continue;
                    }

                    Assert.True(IdentifierIndex.IsCached(tree), $"IdentifierIndex was not warmed for {tree.FilePath}");
                    Assert.True(CallSiteAnalysis.IsCached(compilation, tree), $"CallSiteAnalysis was not warmed for {tree.FilePath}");
                    Assert.True(SymbolNameIndex.IsCached(compilation), $"SymbolNameIndex was not warmed for {project.Name}");
                    verifiedTrees++;
                }
            }

            Assert.True(verifiedTrees >= 3, $"Expected at least 3 warmed trees, saw {verifiedTrees}.");
        }
        finally
        {
            await CloseWorkspaceAsync(handle);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void CallSiteAnalysis_CSharpOperationRootWalk_CapturesAllCallKinds()
    {
        const string source = """
            using System;
            namespace Demo;

            public class BaseBox(int seed)
            {
                public int Seed { get; } = seed;
            }

            public class DerivedBox(int seed) : BaseBox(Helper.Make(seed))
            {
                private readonly int _field = Helper.Make(1);
                public int ExprProp => Helper.Make(2);
                public int InitProp { get; } = Helper.Make(3);
                public Func<int, int> DelegateRef { get; } = Helper.Make;

                public int Run(int x)
                {
                    int Local(int y) => Helper.Make(y);
                    Func<int, int> lambda = z => Helper.Make(z);
                    BaseBox b1 = new BaseBox(x);
                    BaseBox b2 = new(x);
                    return Local(lambda(b1.Seed + b2.Seed + _field));
                }
            }

            public static class Helper
            {
                public static int Make(int n) => n + 1;
            }
            """;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: "Demo.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "DemoAsm",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        IReadOnlyList<CallSiteAnalysis.CallSite> all = CallSiteAnalysis.GetCallSites(compilation, tree, includeObjectCreations: true, CancellationToken.None);
        IReadOnlyList<CallSiteAnalysis.CallSite> makeSites = CallSiteAnalysis.GetCallSitesByName(compilation, tree, "Make", includeObjectCreations: true, CancellationToken.None);

        // 6 calls to Helper.Make (primary ctor base arg, field init, expr prop, prop init, local func, lambda) + 1 method_group.
        Assert.Equal(7, makeSites.Count);
        Assert.Contains(makeSites, s => s.call_kind == "method_group");
        Assert.Contains(makeSites, s => s.via_lambda);
        Assert.Equal(2, all.Count(s => s.call_kind == "object_creation"));
        Assert.DoesNotContain(
            CallSiteAnalysis.GetCallSites(compilation, tree, includeObjectCreations: false, CancellationToken.None),
            s => s.call_kind == "object_creation");
    }

    private static async Task<string> PreloadWorkspaceAsync(ICommandRegistry registry, string solutionPath)
    {
        JsonElement data = await ExecuteOkAsync(registry, "workspace.preload", new
        {
            workspace_path = solutionPath,
            require_solution = true,
            max_files = 100,
        });
        return data.GetProperty("workspace_handle").GetString()!;
    }

    private static async Task CloseWorkspaceAsync(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        try
        {
            ICommandRegistry registry = DefaultRegistryFactory.Create();
            await ExecuteRawAsync(registry, "workspace.close", new { workspace_handle = handle });
        }
        catch
        {
        }
    }

    private static async Task<JsonElement> ExecuteOkAsync(ICommandRegistry registry, string commandId, object payload)
    {
        CommandExecutionResult result = await ExecuteRawAsync(registry, commandId, payload);
        Assert.True(result.Ok, $"{commandId} failed: {string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))}");
        return JsonSerializer.SerializeToElement(result.Data);
    }

    private static async Task<CommandExecutionResult> ExecuteRawAsync(ICommandRegistry registry, string commandId, object payload)
    {
        Assert.True(registry.TryGet(commandId, out IAgentCommand? command));
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        IReadOnlyList<CommandError> validation = command!.Validate(doc.RootElement);
        if (validation.Count > 0)
        {
            return new CommandExecutionResult(null, validation);
        }

        return await command.ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    private static string[] ExtractMatchCoords(JsonElement data)
        => data.GetProperty("matches").EnumerateArray()
            .Select(m => $"{Path.GetFileName(m.GetProperty("file_path").GetString()!)}:{m.GetProperty("line").GetInt32()}:{m.GetProperty("column").GetInt32()}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static (int Line, int Column) FindTokenPosition(string source, string lineContains, string token)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(lineContains, StringComparison.Ordinal))
            {
                int idx = lines[i].IndexOf(token, StringComparison.Ordinal);
                Assert.True(idx >= 0, $"Token '{token}' not found on line '{lines[i]}'.");
                return (i + 1, idx + 1);
            }
        }

        throw new InvalidOperationException($"Line containing '{lineContains}' was not found.");
    }

    private static async Task<(string SolutionPath, string CommitPath, string ServicesPath, string ConsumerPath)> CreateTwoProjectSolutionAsync(string root)
    {
        string coreDir = Path.Combine(root, "Core");
        string appDir = Path.Combine(root, "App");
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(appDir);

        string coreCsproj = Path.Combine(coreDir, "Core.csproj");
        string appCsproj = Path.Combine(appDir, "App.csproj");
        string commitPath = Path.Combine(coreDir, "DeferredCommit.cs");
        string servicesPath = Path.Combine(coreDir, "Services.cs");
        string consumerPath = Path.Combine(appDir, "Consumer.cs");
        string slnxPath = Path.Combine(root, "Sample.slnx");

        await File.WriteAllTextAsync(coreCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(appCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(commitPath, CoreCommitSource);
        await File.WriteAllTextAsync(servicesPath, CoreServicesSource);
        await File.WriteAllTextAsync(consumerPath, AppConsumerSource);
        await File.WriteAllTextAsync(slnxPath, """
            <Solution>
              <Project Path="Core/Core.csproj" />
              <Project Path="App/App.csproj" />
            </Solution>
            """);

        return (slnxPath, commitPath, servicesPath, consumerPath);
    }

    private static void TryDeleteDirectory(string path)
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
