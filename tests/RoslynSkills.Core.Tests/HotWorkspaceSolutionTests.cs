using System.Diagnostics;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.WorkspaceHost;

namespace RoslynSkills.Core.Tests;

/// <summary>
/// Read-only semantic commands must run on the workspace host's loaded solution, see every project of it, and see
/// edits made while the host is running. These tests drive the real host process through its JSON-lines protocol.
/// </summary>
public sealed class HotWorkspaceSolutionTests
{
    private const string ServiceSource = """
        namespace Lib;

        public class Service
        {
            public int Run(int x) => x + 1;
            public static Service Create() => new Service();
        }
        """;

    private const string StoreSource = """
        namespace Lib;

        public interface IStore
        {
            void Save();
        }

        public class FileStore : IStore
        {
            public void Save() { }
        }

        public class Widget
        {
            public Widget(int n) { }
        }
        """;

    private const string AppSource = """
        namespace App;

        public class AppRunner
        {
            public int Go() => new Lib.Service().Run(1);
        }

        public class MemStore : Lib.IStore
        {
            public void Save() { }
        }
        """;

    private const string ConsumerSource = """
        namespace Consumer;

        public class Caller
        {
            public int Use() => new Lib.Service().Run(2);
            public static readonly System.Func<Lib.Service> Factory = Lib.Service.Create;
            public object MakeWidget() => new Lib.Widget(1);
        }

        public class TestStore : Lib.IStore
        {
            public void Save() { }
        }
        """;

    [Fact]
    public void ReadOnlySemanticCommands_DeclareHotWorkspaceRouting()
    {
        IReadOnlyList<CommandDescriptor> commands = DefaultRegistryFactory.Create().ListCommands();
        HashSet<string> hot = commands.Where(command => command.RunsOnHotWorkspace).Select(command => command.Id).ToHashSet(StringComparer.Ordinal);

        string[] expectedHot =
        {
            "nav.find_symbol", "nav.find_symbol_batch", "nav.go_to_definition", "nav.find_references", "nav.find_invocations",
            "nav.call_hierarchy", "nav.call_path", "nav.find_implementations", "nav.find_overrides",
            "ctx.member_source", "ctx.call_chain_slice", "ctx.dependency_slice", "ctx.symbol_envelope",
            "analyze.impact_slice", "analyze.control_flow_graph", "analyze.dataflow_slice",
            "analyze.unused_private_symbols", "analyze.dependency_violations", "analyze.override_coverage", "analyze.async_risk_scan",
            "diag.get_file_diagnostics", "diag.get_after_edit", "diag.get_workspace_snapshot",
            "repair.propose_from_diagnostics", "query.batch",
        };
        foreach (string id in expectedHot)
        {
            Assert.Contains(id, hot);
        }

        // Session state, claims and in-process-only commands must never be routed to the host.
        foreach (CommandDescriptor command in commands.Where(c => c.Id.StartsWith("session.", StringComparison.Ordinal) || c.Id == "edit.claim"))
        {
            Assert.False(command.RunsOnHotWorkspace, command.Id);
        }
    }

    [Fact]
    public void EveryNavAndAnalyzeCommand_RunsOnHotWorkspace()
    {
        // ROSCLI_DAEMON=required fails closed for a nav/analyze command that is not hot; this keeps a newly added one
        // from being left out of the daemon (the original defect) or failing closed by surprise.
        string[] left = DefaultRegistryFactory.Create().ListCommands()
            .Where(command => (command.Id.StartsWith("nav.", StringComparison.Ordinal) || command.Id.StartsWith("analyze.", StringComparison.Ordinal)) &&
                              !command.RunsOnHotWorkspace)
            .Select(command => command.Id)
            .ToArray();
        Assert.Empty(left);
    }

    [Fact]
    public void HotWorkspaceCommands_AcceptWorkspaceHandleInput()
    {
        // The host injects workspace_handle into the input of every routed command; a command that rejects or
        // ignores it would silently reload the solution on every call (the original defect).
        JsonElement input = JsonSerializer.SerializeToElement(new
        {
            workspace_handle = "ws_does_not_exist",
            workspace_path = Path.GetTempPath(),
            file_path = typeof(HotWorkspaceSolutionTests).Assembly.Location,
        });
        foreach (IAgentCommand command in HotCommands().Where(c => !c.Descriptor.MutatesState && !c.Descriptor.Id.StartsWith("workspace.", StringComparison.Ordinal)))
        {
            IReadOnlyList<CommandError> errors = command.Validate(input);
            Assert.DoesNotContain(errors, error => error.Message.Contains("workspace_handle", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task HotHost_SearchesSpanTheWholeSolution_AndSeeEditsMadeWhileRunning()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-hot-solution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using Process process = StartHostProcess();

        try
        {
            (string solutionPath, string servicePath, string appPath, string consumerPath, string storePath) = await CreateSolutionAsync(root);
            HostSession host = new(process);

            JsonElement preload = await host.SendAsync(new
            {
                id = "preload",
                method = WorkspaceHostProtocol.Method.WorkspacePreload,
                workspace_alias = "default",
                input = new { workspace_path = solutionPath, require_solution = true, max_files = 100 },
            }, timeoutSeconds: 120);
            Assert.True(preload.GetProperty("ok").GetBoolean(), preload.GetRawText());
            string handle = preload.GetProperty("envelope").GetProperty("Data").GetProperty("workspace_handle").GetString()!;

            // nav.find_references anchored on Service.Run (Lib): declaration + callers in App and Consumer.
            JsonElement references = await host.CallAsync("nav.find_references", new
            {
                workspace_handle = handle, file_path = servicePath, line = 5, column = 16, require_workspace = true,
            });
            JsonElement referencesData = DataOf(references);
            Assert.Equal("solution", referencesData.GetProperty("search_scope").GetString());
            string[] referenceFiles = referencesData.GetProperty("matches").EnumerateArray()
                .Select(match => Path.GetFileName(match.GetProperty("file_path").GetString()!)).ToArray();
            Assert.Contains("Service.cs", referenceFiles);
            Assert.Contains("AppRunner.cs", referenceFiles);
            Assert.Contains("Caller.cs", referenceFiles);

            // Implementers in three projects; a method group registered in a field initializer; a constructor's `new` site.
            JsonElement implementations = await host.CallAsync("nav.find_implementations", new
            {
                workspace_handle = handle, file_path = storePath, line = 3, column = 18, require_workspace = true,
            });
            JsonElement implementationsData = DataOf(implementations);
            string[] implementers = implementationsData.GetProperty("matches").EnumerateArray()
                .Select(match => match.GetProperty("symbol_display").GetString()!).ToArray();
            Assert.Contains("Lib.FileStore", implementers);
            Assert.Contains("App.MemStore", implementers);
            Assert.Contains("Consumer.TestStore", implementers);
            Assert.Equal("solution", implementationsData.GetProperty("search_scope").GetString());

            JsonElement memberImplementations = await host.CallAsync("nav.find_implementations", new
            {
                workspace_handle = handle, file_path = storePath, line = 5, column = 10, require_workspace = true,
            });
            Assert.Equal(3, DataOf(memberImplementations).GetProperty("total_matches").GetInt32());

            JsonElement methodGroup = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = servicePath, line = 6, column = 27, require_workspace = true,
            });
            JsonElement methodGroupMatches = DataOf(methodGroup).GetProperty("matches");
            Assert.Equal(1, methodGroupMatches.GetArrayLength());
            Assert.Equal("method_group", methodGroupMatches[0].GetProperty("call_kind").GetString());
            Assert.Equal("Caller.cs", Path.GetFileName(methodGroupMatches[0].GetProperty("file_path").GetString()!));

            JsonElement constructorReferences = await host.CallAsync("nav.find_references", new
            {
                workspace_handle = handle, file_path = storePath, line = 15, column = 12, require_workspace = true,
            });
            Assert.Contains(
                "Caller.cs",
                DataOf(constructorReferences).GetProperty("matches").EnumerateArray().Select(match => Path.GetFileName(match.GetProperty("file_path").GetString()!)));

            JsonElement constructorInvocations = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = storePath, line = 15, column = 12, require_workspace = true,
            });
            Assert.Equal(1, DataOf(constructorInvocations).GetProperty("total_matches").GetInt32());

            // nav.find_invocations must not stop at the anchor's project.
            JsonElement invocations = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = servicePath, line = 5, column = 16, require_workspace = true,
            });
            JsonElement invocationsData = DataOf(invocations);
            Assert.Equal("solution", invocationsData.GetProperty("search").GetProperty("search_scope").GetString());
            Assert.Equal(2, invocationsData.GetProperty("total_matches").GetInt32());

            // nav.call_hierarchy (incoming) reaches callers in both sibling projects.
            JsonElement hierarchy = await host.CallAsync("nav.call_hierarchy", new
            {
                workspace_handle = handle, file_path = servicePath, line = 5, column = 16, direction = "incoming", max_depth = 1, require_workspace = true,
            });
            JsonElement hierarchyData = DataOf(hierarchy);
            Assert.Equal("process_hot", hierarchyData.GetProperty("query").GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());
            string hierarchyText = hierarchyData.GetProperty("nodes").GetRawText();
            Assert.Contains("AppRunner.Go", hierarchyText);
            Assert.Contains("Caller.Use", hierarchyText);

            // analyze.impact_slice sees both callers as well.
            JsonElement impact = await host.CallAsync("analyze.impact_slice", new
            {
                workspace_handle = handle, file_path = servicePath, line = 5, column = 16, require_workspace = true,
            });
            JsonElement impactData = DataOf(impact);
            Assert.Equal(2, impactData.GetProperty("impact_counts").GetProperty("callers").GetInt32());

            // ctx.call_chain_slice, nav.call_path and a workspace-scanning analyzer take the handle instead of reloading.
            JsonElement chain = await host.CallAsync("ctx.call_chain_slice", new
            {
                workspace_handle = handle, file_path = appPath, line = 5, column = 16, require_workspace = true,
            });
            Assert.Equal("process_hot", DataOf(chain).GetProperty("query").GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());

            JsonElement path = await host.CallAsync("nav.call_path", new
            {
                workspace_handle = handle,
                source_file_path = appPath, source_line = 5, source_column = 16,
                target_file_path = servicePath, target_line = 5, target_column = 16,
                require_workspace = true,
            });
            Assert.True(path.GetProperty("ok").GetBoolean(), path.GetRawText());

            JsonElement scan = await host.CallAsync("analyze.async_risk_scan", new
            {
                workspace_handle = handle, workspace_path = solutionPath,
            });
            Assert.True(scan.GetProperty("ok").GetBoolean(), scan.GetRawText());
            Assert.Equal(3, DataOf(scan).GetProperty("analysis_scope").GetProperty("project_count").GetInt32());

            // An edit made outside the host while it runs: policy "none" leaves the workspace stale, the default sees it.
            await File.WriteAllTextAsync(appPath, AppSource.Replace(
                "public int Go() => new Lib.Service().Run(1);",
                "public int Go() => new Lib.Service().Run(1);\n    public int Go2() => new Lib.Service().Run(3);"));
            File.SetLastWriteTimeUtc(appPath, DateTime.UtcNow.AddMinutes(1));

            JsonElement stale = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = servicePath, line = 5, column = 16, require_workspace = true,
            }, refreshPolicy: "none");
            Assert.Equal(2, DataOf(stale).GetProperty("total_matches").GetInt32());

            JsonElement fresh = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = servicePath, line = 5, column = 16, require_workspace = true,
            });
            Assert.Equal(3, DataOf(fresh).GetProperty("total_matches").GetInt32());
            Assert.Equal("incremental_document_update", fresh.GetProperty("workspace").GetProperty("refresh_action").GetString());

            JsonElement freshHierarchy = await host.CallAsync("nav.call_hierarchy", new
            {
                workspace_handle = handle, file_path = servicePath, line = 5, column = 16, direction = "incoming", max_depth = 1, require_workspace = true,
            });
            Assert.Contains("AppRunner.Go2", DataOf(freshHierarchy).GetProperty("nodes").GetRawText());

            await process.StandardInput.WriteLineAsync("{\"id\":\"shutdown\",\"method\":\"shutdown\"}");
            _ = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after shutdown.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InProcessSolutionWorkspace_FindReferencesAndInvocationsSpanProjects()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-inproc-solution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            (string solutionPath, string servicePath, _, _, _) = await CreateSolutionAsync(root);
            IAgentCommand references = DefaultRegistryFactory.Create().TryGet("nav.find_references", out IAgentCommand? r) ? r! : throw new InvalidOperationException();
            IAgentCommand invocations = DefaultRegistryFactory.Create().TryGet("nav.find_invocations", out IAgentCommand? i) ? i! : throw new InvalidOperationException();
            JsonElement input = JsonSerializer.SerializeToElement(new
            {
                file_path = servicePath, line = 5, column = 16, workspace_path = solutionPath, require_workspace = true,
            });

            CommandExecutionResult referencesResult = await references.ExecuteAsync(input, CancellationToken.None);
            Assert.True(referencesResult.Ok, string.Join("; ", referencesResult.Errors.Select(e => e.Message)));
            JsonElement referencesData = JsonSerializer.SerializeToElement(referencesResult.Data);
            Assert.Equal("solution", referencesData.GetProperty("search_scope").GetString());
            Assert.Equal(3, referencesData.GetProperty("total_matches").GetInt32());

            CommandExecutionResult invocationsResult = await invocations.ExecuteAsync(input, CancellationToken.None);
            Assert.True(invocationsResult.Ok, string.Join("; ", invocationsResult.Errors.Select(e => e.Message)));
            Assert.Equal(2, JsonSerializer.SerializeToElement(invocationsResult.Data).GetProperty("total_matches").GetInt32());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HotHost_NavigationCompletenessAndMultiProjectSnapshot_CoversAllFiveRequirements()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-nav-completeness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using Process process = StartHostProcess();

        try
        {
            const string libCsproj = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            const string appCsproj = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
                </Project>
                """;
            const string consumerCsproj = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                    <ProjectReference Include="../App/App.csproj" />
                  </ItemGroup>
                </Project>
                """;

            string srcDir = Path.Combine(root, "src");
            string libDir = Path.Combine(srcDir, "Lib");
            string appDir = Path.Combine(srcDir, "App");
            string consumerDir = Path.Combine(srcDir, "Consumer");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(consumerDir);

            string libProjPath = Path.Combine(libDir, "Lib.csproj");
            string appProjPath = Path.Combine(appDir, "App.csproj");
            string consumerProjPath = Path.Combine(consumerDir, "Consumer.csproj");
            await File.WriteAllTextAsync(libProjPath, libCsproj);
            await File.WriteAllTextAsync(appProjPath, appCsproj);
            await File.WriteAllTextAsync(consumerProjPath, consumerCsproj);

            const string libTypesSource = """
                namespace Lib;

                public interface IPageSource
                {
                    int PageCount { get; }
                }

                public sealed class FilePageSource : IPageSource
                {
                    public int PageCount => 1;
                }

                public sealed class EditFrame
                {
                    public int Id { get; }
                    public EditFrame(int id) => Id = id;
                    public void Update(RowWriter writer) => writer.CompileInsert();
                }

                public sealed class PrimaryNode(int value)
                {
                    public int Value => value;
                }

                public sealed class PlainMarker
                {
                }

                public sealed class RowWriter
                {
                    public void CompileInsert() { }
                }

                public sealed class RecordsetObject
                {
                    public static RecordsetObject Create() => new RecordsetObject();
                }

                public partial class SplitModel
                {
                    public string Name { get; set; } = string.Empty;
                }

                public static class LibApi
                {
                    public static int Compute(int value) => value + 1;
                }
                """;

            const string libPartialSource = """
                namespace Lib;

                public partial class SplitModel
                {
                    public int Version { get; set; }
                }
                """;

            const string appRunnerSource = """
                using System.Text;

                namespace App;

                public sealed class MemoryPageSource : Lib.IPageSource
                {
                    public int PageCount => 2;
                }

                public static class AppHost
                {
                    public static void CommitRowChange(Lib.EditFrame frame, Action action) => action();
                    public static int AcceptFrame(Lib.EditFrame frame) => frame.Id;
                    public static int AcceptPrimary(Lib.PrimaryNode node) => node.Value;

                    public static void InsertOneRow(Lib.RowWriter writer)
                    {
                        Lib.EditFrame explicitFrame = new Lib.EditFrame(1);
                        Lib.EditFrame targetTypedFrame = new(2);
                        CommitRowChange(explicitFrame, () => explicitFrame.Update(writer));
                        _ = targetTypedFrame;
                    }

                    public static int UseTypes(Lib.SplitModel model, string text)
                    {
                        Lib.PrimaryNode p1 = new Lib.PrimaryNode(10);
                        Lib.PrimaryNode p2 = new(20);
                        Lib.PlainMarker marker = new Lib.PlainMarker();
                        string normalized = text.Normalize(NormalizationForm.FormC);
                        return Lib.LibApi.Compute(p1.Value + p2.Value + model.Version + normalized.Length + marker.GetHashCode());
                    }
                }
                """;

            // Notice TargetTypedOnly.cs deliberately never spells "EditFrame" or "PrimaryNode".
            const string consumerTargetTypedSource = """
                namespace Consumer;

                public sealed class TargetTypedOnly
                {
                    public static int Invoke()
                    {
                        int a = App.AppHost.AcceptFrame(new(3));
                        int b = App.AppHost.AcceptPrimary(new(30));
                        return a + b + Lib.LibApi.Compute(5);
                    }
                }
                """;

            const string consumerActivationSource = """
                namespace Consumer;

                public sealed class DiskPageSource : Lib.IPageSource
                {
                    public int PageCount => 3;
                }

                public sealed record ClassDescriptor(string Name, Func<Lib.RecordsetObject> Factory);

                public static class Activation
                {
                    public static readonly ClassDescriptor OwnRecordset = new("Own", Lib.RecordsetObject.Create);
                    public static readonly ClassDescriptor CompatibilityRecordset = new("Compat", Lib.RecordsetObject.Create);

                    public static object[] CreateAllSources() =>
                    [
                        new Lib.FilePageSource(),
                        new App.MemoryPageSource(),
                        new DiskPageSource(),
                    ];
                }
                """;

            string libTypesPath = Path.Combine(libDir, "LibTypes.cs");
            string libPartialPath = Path.Combine(libDir, "SplitModel.Part2.cs");
            string appRunnerPath = Path.Combine(appDir, "AppHost.cs");
            string consumerTargetTypedPath = Path.Combine(consumerDir, "TargetTypedOnly.cs");
            string consumerActivationPath = Path.Combine(consumerDir, "Activation.cs");

            await File.WriteAllTextAsync(libTypesPath, libTypesSource);
            await File.WriteAllTextAsync(libPartialPath, libPartialSource);
            await File.WriteAllTextAsync(appRunnerPath, appRunnerSource);
            await File.WriteAllTextAsync(consumerTargetTypedPath, consumerTargetTypedSource);
            await File.WriteAllTextAsync(consumerActivationPath, consumerActivationSource);

            string solutionPath = Path.Combine(root, "NavSuite.slnx");
            await File.WriteAllTextAsync(
                solutionPath,
                """
                <Solution>
                  <Project Path="src/Lib/Lib.csproj" />
                  <Project Path="src/App/App.csproj" />
                  <Project Path="src/Consumer/Consumer.csproj" />
                </Solution>
                """);

            HostSession host = new(process);
            JsonElement preload = await host.SendAsync(new
            {
                id = "preload",
                method = WorkspaceHostProtocol.Method.WorkspacePreload,
                workspace_alias = "default",
                input = new { workspace_path = solutionPath, require_solution = true, max_files = 100 },
            }, timeoutSeconds: 120);
            Assert.True(preload.GetProperty("ok").GetBoolean(), preload.GetRawText());
            string handle = preload.GetProperty("envelope").GetProperty("Data").GetProperty("workspace_handle").GetString()!;

            // 1. Object creations & constructors in nav.find_references and nav.find_invocations (including target-typed new & primary ctor).
            (int editFrameLine, int editFrameCol) = FindTokenPosition(libTypesSource, "class EditFrame", "EditFrame");
            JsonElement editFrameRefs = await host.CallAsync("nav.find_references", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = editFrameLine, column = editFrameCol, require_workspace = true,
            });
            JsonElement editFrameRefsData = DataOf(editFrameRefs);
            JsonElement[] editFrameRefMatches = editFrameRefsData.GetProperty("matches").EnumerateArray().ToArray();
            Assert.Contains(editFrameRefMatches, m => Path.GetFileName(m.GetProperty("file_path").GetString()!) == "AppHost.cs" && m.GetProperty("line").GetInt32() == FindLineNumber(appRunnerSource, "new Lib.EditFrame(1)"));
            Assert.Contains(editFrameRefMatches, m => Path.GetFileName(m.GetProperty("file_path").GetString()!) == "AppHost.cs" && m.GetProperty("line").GetInt32() == FindLineNumber(appRunnerSource, "targetTypedFrame = new(2)"));
            Assert.Contains(editFrameRefMatches, m => Path.GetFileName(m.GetProperty("file_path").GetString()!) == "TargetTypedOnly.cs" && m.GetProperty("line").GetInt32() == FindLineNumber(consumerTargetTypedSource, "AcceptFrame(new(3))"));

            JsonElement editFrameInvocations = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = editFrameLine, column = editFrameCol, require_workspace = true,
            });
            JsonElement editFrameInvData = DataOf(editFrameInvocations);
            Assert.Equal(3, editFrameInvData.GetProperty("total_matches").GetInt32());

            // Primary constructor class (both identifier and parameter-list '(' anchor).
            (int primaryLine, int primaryCol) = FindTokenPosition(libTypesSource, "class PrimaryNode(int value)", "PrimaryNode");
            JsonElement primaryInvocations = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = primaryLine, column = primaryCol, require_workspace = true,
            });
            Assert.Equal(3, DataOf(primaryInvocations).GetProperty("total_matches").GetInt32());

            (int primaryParenLine, int primaryParenCol) = FindTokenPosition(libTypesSource, "class PrimaryNode(int value)", "(");
            JsonElement primaryParenInvocations = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = primaryParenLine, column = primaryParenCol, require_workspace = true,
            });
            Assert.Equal(3, DataOf(primaryParenInvocations).GetProperty("total_matches").GetInt32());

            // Implicit parameterless constructor class.
            (int plainLine, int plainCol) = FindTokenPosition(libTypesSource, "class PlainMarker", "PlainMarker");
            JsonElement plainInvocations = await host.CallAsync("nav.find_invocations", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = plainLine, column = plainCol, require_workspace = true,
            });
            Assert.Equal(1, DataOf(plainInvocations).GetProperty("total_matches").GetInt32());

            // 2. nav.call_hierarchy follows lambdas (2 levels up) and method-group registrations in field initializers.
            (int compileLine, int compileCol) = FindTokenPosition(libTypesSource, "void CompileInsert()", "CompileInsert");
            JsonElement compileHierarchy = await host.CallAsync("nav.call_hierarchy", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = compileLine, column = compileCol,
                direction = "incoming", max_depth = 2, require_workspace = true,
            });
            JsonElement compileHierarchyData = DataOf(compileHierarchy);
            string compileNodesRaw = compileHierarchyData.GetProperty("nodes").GetRawText();
            Assert.Contains("EditFrame.Update", compileNodesRaw);
            Assert.Contains("AppHost.InsertOneRow", compileNodesRaw);
            int lambdaLine = FindLineNumber(appRunnerSource, "() => explicitFrame.Update(writer)");
            Assert.Contains(
                compileHierarchyData.GetProperty("edges").EnumerateArray(),
                e => e.GetProperty("via_lambda").GetBoolean() && e.GetProperty("line").GetInt32() == lambdaLine);

            (int createLine, int createCol) = FindTokenPosition(libTypesSource, "RecordsetObject Create()", "Create");
            JsonElement createHierarchy = await host.CallAsync("nav.call_hierarchy", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = createLine, column = createCol,
                direction = "incoming", max_depth = 1, require_workspace = true,
            });
            JsonElement createHierarchyData = DataOf(createHierarchy);
            string createNodesRaw = createHierarchyData.GetProperty("nodes").GetRawText();
            Assert.Contains("Activation.OwnRecordset", createNodesRaw);
            Assert.Contains("Activation.CompatibilityRecordset", createNodesRaw);
            JsonElement[] methodGroupEdges = createHierarchyData.GetProperty("edges").EnumerateArray()
                .Where(e => e.GetProperty("call_kind").GetString() == "method_group")
                .ToArray();
            Assert.Equal(2, methodGroupEdges.Length);

            // 3. nav.go_to_definition resolves source (single & partial) and metadata symbols cleanly.
            (int computeUseLine, int computeUseCol) = FindTokenPosition(appRunnerSource, "Lib.LibApi.Compute(", "Compute");
            (int computeDeclLine, int computeDeclCol) = FindTokenPosition(libTypesSource, "int Compute(int value)", "Compute");
            JsonElement sourceDef = await host.CallAsync("nav.go_to_definition", new
            {
                workspace_handle = handle, file_path = appRunnerPath, line = computeUseLine, column = computeUseCol, require_workspace = true,
            });
            JsonElement sourceDefData = DataOf(sourceDef);
            Assert.Equal(1, sourceDefData.GetProperty("source_definition_count").GetInt32());
            Assert.Equal(0, sourceDefData.GetProperty("metadata_definition_count").GetInt32());
            JsonElement firstSourceDef = sourceDefData.GetProperty("definitions")[0];
            Assert.Equal("source", firstSourceDef.GetProperty("location_kind").GetString());
            Assert.Equal("LibTypes.cs", Path.GetFileName(firstSourceDef.GetProperty("file_path").GetString()!));
            Assert.Equal(computeDeclLine, firstSourceDef.GetProperty("line").GetInt32());
            Assert.Equal(computeDeclCol, firstSourceDef.GetProperty("column").GetInt32());
            Assert.Equal("Lib", firstSourceDef.GetProperty("assembly_name").GetString());

            // Metadata definition (System.Text.NormalizationForm).
            (int metaLine, int metaCol) = FindTokenPosition(appRunnerSource, "NormalizationForm.FormC", "NormalizationForm");
            JsonElement metaDef = await host.CallAsync("nav.go_to_definition", new
            {
                workspace_handle = handle, file_path = appRunnerPath, line = metaLine, column = metaCol, require_workspace = true,
            });
            JsonElement metaDefData = DataOf(metaDef);
            Assert.Equal(0, metaDefData.GetProperty("source_definition_count").GetInt32());
            Assert.True(metaDefData.GetProperty("metadata_definition_count").GetInt32() >= 1);
            JsonElement firstMetaDef = metaDefData.GetProperty("definitions")[0];
            Assert.Equal("metadata", firstMetaDef.GetProperty("location_kind").GetString());
            Assert.Equal("System.Text.NormalizationForm", firstMetaDef.GetProperty("metadata_name").GetString());
            Assert.False(string.IsNullOrWhiteSpace(firstMetaDef.GetProperty("assembly_name").GetString()));

            // Partial type definition returns both source declarations.
            (int splitUseLine, int splitUseCol) = FindTokenPosition(appRunnerSource, "Lib.SplitModel model", "SplitModel");
            JsonElement splitDef = await host.CallAsync("nav.go_to_definition", new
            {
                workspace_handle = handle, file_path = appRunnerPath, line = splitUseLine, column = splitUseCol, require_workspace = true,
            });
            Assert.Equal(2, DataOf(splitDef).GetProperty("source_definition_count").GetInt32());

            // 5. nav.find_implementations returns identifier column (not column 1) and chains straight into nav.* commands.
            (int ifaceLine, int ifaceCol) = FindTokenPosition(libTypesSource, "interface IPageSource", "IPageSource");
            JsonElement impls = await host.CallAsync("nav.find_implementations", new
            {
                workspace_handle = handle, file_path = libTypesPath, line = ifaceLine, column = ifaceCol, require_workspace = true,
            });
            JsonElement[] implMatches = DataOf(impls).GetProperty("matches").EnumerateArray().ToArray();
            Assert.Equal(3, implMatches.Length);
            foreach (JsonElement impl in implMatches)
            {
                int col = impl.GetProperty("column").GetInt32();
                Assert.True(col > 1, $"Expected identifier column > 1 for {impl.GetRawText()}");

                JsonElement chainedInvocations = await host.CallAsync("nav.find_invocations", new
                {
                    workspace_handle = handle,
                    file_path = impl.GetProperty("file_path").GetString()!,
                    line = impl.GetProperty("line").GetInt32(),
                    column = col,
                    require_workspace = true,
                });
                Assert.Equal(1, DataOf(chainedInvocations).GetProperty("total_matches").GetInt32());
            }

            // 4. diag.get_workspace_snapshot covers every project under directory/solution (per project, summed)
            //    and keeps single-project arguments single-project.
            JsonElement cleanSnapshot = await host.CallAsync("diag.get_workspace_snapshot", new
            {
                workspace_handle = handle, directory_path = srcDir, require_workspace = true,
            });
            JsonElement cleanData = DataOf(cleanSnapshot);
            Assert.Equal(3, cleanData.GetProperty("analyzed_projects").GetInt32());
            Assert.Equal(0, cleanData.GetProperty("errors").GetInt32());

            // Introduce a deliberate signature break in Lib that breaks both App and Consumer.
            await File.WriteAllTextAsync(
                libTypesPath,
                libTypesSource.Replace("public static int Compute(int value) => value + 1;", "public static int Compute(int value, int extra) => value + extra;"));
            File.SetLastWriteTimeUtc(libTypesPath, DateTime.UtcNow.AddMinutes(1));

            JsonElement srcBroken = await host.CallAsync("diag.get_workspace_snapshot", new
            {
                workspace_handle = handle, directory_path = srcDir, require_workspace = true,
            });
            JsonElement srcBrokenData = DataOf(srcBroken);
            Assert.Equal(3, srcBrokenData.GetProperty("analyzed_projects").GetInt32());
            Assert.Equal(2, srcBrokenData.GetProperty("projects_with_errors").GetInt32());
            Assert.Equal(2, srcBrokenData.GetProperty("errors").GetInt32());
            Dictionary<string, int> perProjectErrors = srcBrokenData.GetProperty("projects").EnumerateArray()
                .ToDictionary(p => p.GetProperty("project_name").GetString()!, p => p.GetProperty("errors").GetInt32(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, perProjectErrors["Lib"]);
            Assert.Equal(1, perProjectErrors["App"]);
            Assert.Equal(1, perProjectErrors["Consumer"]);
            Assert.Equal(srcBrokenData.GetProperty("errors").GetInt32(), perProjectErrors.Values.Sum());

            // Solution file covers all 3 projects.
            JsonElement slnBroken = await host.CallAsync("diag.get_workspace_snapshot", new
            {
                workspace_handle = handle, workspace_path = solutionPath, require_workspace = true,
            });
            Assert.Equal(3, DataOf(slnBroken).GetProperty("analyzed_projects").GetInt32());
            Assert.Equal(2, DataOf(slnBroken).GetProperty("errors").GetInt32());

            // Single-project argument (.csproj or project dir) stays single-project.
            JsonElement appOnlyProj = await host.CallAsync("diag.get_workspace_snapshot", new
            {
                workspace_handle = handle, workspace_path = appProjPath, require_workspace = true,
            });
            Assert.Equal(1, DataOf(appOnlyProj).GetProperty("analyzed_projects").GetInt32());
            Assert.Equal(1, DataOf(appOnlyProj).GetProperty("errors").GetInt32());

            JsonElement appOnlyDir = await host.CallAsync("diag.get_workspace_snapshot", new
            {
                workspace_handle = handle, directory_path = appDir, require_workspace = true,
            });
            Assert.Equal(1, DataOf(appOnlyDir).GetProperty("analyzed_projects").GetInt32());
            Assert.Equal(1, DataOf(appOnlyDir).GetProperty("errors").GetInt32());

            await process.StandardInput.WriteLineAsync("{\"id\":\"shutdown\",\"method\":\"shutdown\"}");
            _ = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after shutdown.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

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

    private static int FindLineNumber(string source, string lineContains)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(lineContains, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"Line containing '{lineContains}' was not found.");
    }

    private static IEnumerable<IAgentCommand> HotCommands()
    {
        ICommandRegistry registry = DefaultRegistryFactory.Create();
        foreach (CommandDescriptor descriptor in registry.ListCommands().Where(d => d.RunsOnHotWorkspace))
        {
            if (registry.TryGet(descriptor.Id, out IAgentCommand? command) && command is not null)
            {
                yield return command;
            }
        }
    }

    private static JsonElement DataOf(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("envelope").GetProperty("Data");
    }

    private static Process StartHostProcess()
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(WorkspaceHostAssembly).Assembly.Location);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start workspace host process.");
    }

    private sealed class HostSession
    {
        private readonly Process _process;
        private int _next;

        public HostSession(Process process) => _process = process;

        public Task<JsonElement> CallAsync(string commandId, object input, string? refreshPolicy = null)
            => SendAsync(new Dictionary<string, object?>
            {
                ["id"] = $"call{_next++}",
                ["method"] = WorkspaceHostProtocol.Method.ToolCall,
                ["command_id"] = commandId,
                ["refresh_policy"] = refreshPolicy,
                ["input"] = input,
            }, timeoutSeconds: 120);

        public async Task<JsonElement> SendAsync(object request, int timeoutSeconds)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            }
            catch (TimeoutException)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                throw new TimeoutException($"Workspace host did not answer. Stderr: {await _process.StandardError.ReadToEndAsync()}");
            }

            Assert.False(string.IsNullOrWhiteSpace(line), "Workspace host closed its output.");
            return JsonDocument.Parse(line!).RootElement.Clone();
        }
    }

    private static async Task<(string Solution, string Service, string App, string Consumer, string Store)> CreateSolutionAsync(string root)
    {
        const string projectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              {0}
            </Project>
            """;
        const string libReference = "<ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup>";

        string[] projects = { "Lib", "App", "Consumer" };
        foreach (string project in projects)
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            await File.WriteAllTextAsync(
                Path.Combine(root, project, $"{project}.csproj"),
                string.Format(projectFile, project == "Lib" ? string.Empty : libReference));
        }

        string servicePath = Path.Combine(root, "Lib", "Service.cs");
        string appPath = Path.Combine(root, "App", "AppRunner.cs");
        string consumerPath = Path.Combine(root, "Consumer", "Caller.cs");
        string storePath = Path.Combine(root, "Lib", "Store.cs");
        await File.WriteAllTextAsync(storePath, StoreSource);
        await File.WriteAllTextAsync(servicePath, ServiceSource);
        await File.WriteAllTextAsync(appPath, AppSource);
        await File.WriteAllTextAsync(consumerPath, ConsumerSource);

        string solutionPath = Path.Combine(root, "Hot.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="App/App.csproj" />
              <Project Path="Consumer/Consumer.csproj" />
            </Solution>
            """);
        return (solutionPath, servicePath, appPath, consumerPath, storePath);
    }
}
