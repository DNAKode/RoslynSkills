using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.WorkspaceHost;

namespace RoslynSkills.Core.Tests;

/// <summary>
/// End-to-end verification of Roslyn-backed code fixes (<c>edit.apply_code_fix</c>) and hot-workspace repair
/// (<c>repair.propose_from_diagnostics</c>, <c>repair.apply_plan</c>) on a 3-project solution driven through
/// the real <c>WorkspaceHost</c> process:
/// - missing <c>using</c> across projects (CS0103, CS0246, CS1061 extension method)
/// - ambiguous <c>AddImport</c> reporting offered titles and applying the explicitly requested fix
/// - unimplemented interface members (CS0535) via <c>repair.propose_from_diagnostics</c> + <c>repair.apply_plan</c>
///   and via explicit <c>fix_title</c>
/// - diagnostics with no safe Roslyn fix (CS7036 missing required argument, CS0029 type mismatch) returning
///   <c>no_fix_available</c> and never proposing TODO stubs
/// - multi-step repair plan on one file with line shifts
/// - CRLF/LF and UTF-8 BOM preservation plus updated-solution diagnostics.
/// </summary>
public sealed class HotWorkspaceCodeFixRepairTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private const string CoreLibSourceLf =
        "namespace CoreLib.Formatting;\n" +
        "\n" +
        "public sealed record BannerSpec(string Prefix, string Suffix);\n" +
        "\n" +
        "public interface IGreeter\n" +
        "{\n" +
        "    string Greet(string name);\n" +
        "    int Count { get; }\n" +
        "}\n" +
        "\n" +
        "public static class BannerFormatter\n" +
        "{\n" +
        "    public static string Format(BannerSpec spec, string value) => $\"{spec.Prefix}{value}{spec.Suffix}\";\n" +
        "}\n" +
        "\n" +
        "public static class StringBannerExtensions\n" +
        "{\n" +
        "    public static string ToBanner(this string value) => $\"[{value}]\";\n" +
        "}\n";

    private const string CoreLibAmbiguousSourceLf =
        "namespace CoreLib.Alpha\n" +
        "{\n" +
        "    public sealed class SharedToken { public static string Origin => \"alpha\"; }\n" +
        "}\n" +
        "\n" +
        "namespace CoreLib.Beta\n" +
        "{\n" +
        "    public sealed class SharedToken { public static string Origin => \"beta\"; }\n" +
        "}\n";

    // CRLF + UTF-8 BOM: missing `using CoreLib.Formatting;` causes CS0246 (BannerSpec), CS0103 (BannerFormatter),
    // and CS1061 (ToBanner extension method) across project boundaries.
    private const string AppMissingUsingCrLf =
        "namespace App;\r\n" +
        "\r\n" +
        "public sealed class AppRunner\r\n" +
        "{\r\n" +
        "    public string Run(BannerSpec spec)\r\n" +
        "    {\r\n" +
        "        string formatted = BannerFormatter.Format(spec, \"hello\");\r\n" +
        "        return formatted.ToBanner();\r\n" +
        "    }\r\n" +
        "}\r\n";

    // LF + no BOM: missing `using System.Diagnostics;` causes 2x CS0103 (Stopwatch + Debug).
    private const string AppMissingDiagnosticsLf =
        "namespace App;\n" +
        "\n" +
        "public sealed class TimingProbe\n" +
        "{\n" +
        "    public long Measure()\n" +
        "    {\n" +
        "        Stopwatch sw = Stopwatch.StartNew();\n" +
        "        Debug.Assert(sw != null);\n" +
        "        return sw.ElapsedMilliseconds;\n" +
        "    }\n" +
        "}\n";

    // LF + no BOM: ambiguous AddImport between CoreLib.Alpha and CoreLib.Beta.
    private const string AppAmbiguousUsingLf =
        "namespace App;\n" +
        "\n" +
        "public sealed class AmbiguousConsumer\n" +
        "{\n" +
        "    public string Read() => SharedToken.Origin;\n" +
        "}\n";

    // LF + no BOM: unimplemented cross-project interface IGreeter (CS0535 x2) AND missing using CoreLib.Formatting;
    // Wait: keep one file with explicit using (pure CS0535) and another file with BOTH missing using + CS0535.
    private const string ConsumerUnimplementedLf =
        "using CoreLib.Formatting;\n" +
        "\n" +
        "namespace Consumer;\n" +
        "\n" +
        "public sealed class DefaultGreeter : IGreeter\n" +
        "{\n" +
        "}\n" +
        "\n" +
        "public sealed class ExplicitGreeter : IGreeter\n" +
        "{\n" +
        "}\n";

    // LF + no BOM: diagnostics with NO safe Roslyn fix (CS7036 missing required argument + CS0029 type mismatch).
    private const string ConsumerUnfixableLf =
        "namespace Consumer;\n" +
        "\n" +
        "public sealed class BrokenCalls\n" +
        "{\n" +
        "    public void Submit(int id, string label) { }\n" +
        "\n" +
        "    public int Execute()\n" +
        "    {\n" +
        "        Submit(42);\n" +
        "        int wrong = \"not-an-int\";\n" +
        "        return wrong;\n" +
        "    }\n" +
        "}\n";

    // CRLF + UTF-8 BOM: multi-step repair file combining missing using (CS1061) and unimplemented interface (CS0535)
    // to verify line-shift handling across sequential steps in repair.apply_plan.
    private const string ConsumerMultiStepCrLf =
        "namespace Consumer;\r\n" +
        "\r\n" +
        "public sealed class ComboGreeter : CoreLib.Formatting.IGreeter\r\n" +
        "{\r\n" +
        "    public string Tag(string s) => s.ToBanner();\r\n" +
        "}\r\n";

    [Fact]
    public void CodeFixAndRepairCommands_DeclareHotWorkspaceRouting()
    {
        IReadOnlyList<CommandDescriptor> commands = DefaultRegistryFactory.Create().ListCommands();
        HashSet<string> hot = commands
            .Where(c => c.RunsOnHotWorkspace)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("edit.apply_code_fix", hot);
        Assert.Contains("repair.propose_from_diagnostics", hot);
        Assert.Contains("repair.apply_plan", hot);
    }

    [Fact]
    public async Task HotHost_ThreeProjectCodeFixesAndRepairPlan()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-hot-codefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using Process process = StartHostProcess();

        try
        {
            FixturePaths paths = await CreateThreeProjectFixtureAsync(root);
            HostSession host = new(process);

            JsonElement preload = await host.SendAsync(new
            {
                id = "preload",
                method = WorkspaceHostProtocol.Method.WorkspacePreload,
                workspace_alias = "default",
                input = new { workspace_path = paths.Solution, require_solution = true, max_files = 100 },
            }, timeoutSeconds: 120);
            string handle = DataOf(preload).GetProperty("workspace_handle").GetString()!;

            // -----------------------------------------------------------------
            // 1. Missing using across projects (CS0246 + CS0103 + CS1061):
            //    propose deduplicates to 1 real AddImport fix (never TODO stubs);
            //    edit.apply_code_fix on CS1061 fixes all 3 errors in one hot call
            //    and preserves CRLF + UTF-8 BOM.
            // -----------------------------------------------------------------
            JsonElement proposeAppResp = await host.CallAsync("repair.propose_from_diagnostics", new
            {
                workspace_handle = handle,
                file_path = paths.AppMissingUsing,
                require_workspace = true,
            });
            JsonElement proposeAppData = DataOf(proposeAppResp);
            Assert.Equal("process_hot", proposeAppData.GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());
            Assert.DoesNotContain("TODO_ResolveMissingSymbol", proposeAppData.GetRawText());
            Assert.DoesNotContain("edit.add_member", proposeAppData.GetRawText());
            Assert.Equal(1, proposeAppData.GetProperty("proposal_count").GetInt32());
            JsonElement appProposal = proposeAppData.GetProperty("proposals")[0];
            Assert.Equal("using CoreLib.Formatting;", appProposal.GetProperty("fix_title").GetString());
            Assert.Equal("AddImport", appProposal.GetProperty("provider").GetString());
            Assert.True(appProposal.GetProperty("covered_diagnostic_count").GetInt32() >= 3);

            JsonElement fixExtResp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.AppMissingUsing,
                diagnostic_id = "CS1061",
                line = 8,
                apply = true,
                require_workspace = true,
            });
            JsonElement fixExtData = DataOf(fixExtResp);
            Assert.Equal("process_hot", fixExtData.GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());
            Assert.Equal("using CoreLib.Formatting;", fixExtData.GetProperty("selected_fix").GetProperty("title").GetString());
            Assert.Equal("AddImport", fixExtData.GetProperty("selected_fix").GetProperty("provider").GetString());
            Assert.True(fixExtData.GetProperty("wrote_file").GetBoolean());
            Assert.Equal(0, fixExtData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());
            Assert.Contains("using CoreLib.Formatting;", await File.ReadAllTextAsync(paths.AppMissingUsing));
            AssertPureCrLfWithBom(paths.AppMissingUsing);

            // -----------------------------------------------------------------
            // 2. Missing `using System.Diagnostics;` (2x CS0103: Stopwatch + Debug):
            //    one edit.apply_code_fix call without line/fix_title resolves both
            //    and preserves LF + no BOM.
            // -----------------------------------------------------------------
            JsonElement fixDiagResp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.AppMissingDiagnostics,
                diagnostic_id = "CS0103",
                apply = true,
                require_workspace = true,
            });
            JsonElement fixDiagData = DataOf(fixDiagResp);
            Assert.Equal("using System.Diagnostics;", fixDiagData.GetProperty("selected_fix").GetProperty("title").GetString());
            Assert.True(fixDiagData.GetProperty("wrote_file").GetBoolean());
            Assert.Equal(0, fixDiagData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());
            Assert.Contains("using System.Diagnostics;", await File.ReadAllTextAsync(paths.AppMissingDiagnostics));
            AssertPureLfNoBom(paths.AppMissingDiagnostics);

            // -----------------------------------------------------------------
            // 3. Ambiguous AddImport (CoreLib.Alpha vs CoreLib.Beta):
            //    refuses to guess when fix_title is omitted, lists both titles,
            //    then applies the explicitly requested fix_title.
            // -----------------------------------------------------------------
            JsonElement ambigFailResp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.AppAmbiguousUsing,
                diagnostic_id = "CS0103",
                line = 5,
                apply = true,
                require_workspace = true,
            });
            Assert.False(ambigFailResp.GetProperty("ok").GetBoolean(), ambigFailResp.GetRawText());
            Assert.False(ambigFailResp.TryGetProperty("envelope", out _), ambigFailResp.GetRawText());
            string ambigErrorCode = ambigFailResp.GetProperty("errors")[0].GetProperty("Code").GetString()!;
            Assert.Equal("ambiguous_code_fix", ambigErrorCode);
            string[] ambigTitles = ambigFailResp.GetProperty("data").GetProperty("offered_fixes")
                .EnumerateArray()
                .Select(f => f.GetProperty("title").GetString()!)
                .ToArray();
            Assert.Contains("using CoreLib.Alpha;", ambigTitles);
            Assert.Contains("using CoreLib.Beta;", ambigTitles);

            JsonElement ambigApplyResp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.AppAmbiguousUsing,
                diagnostic_id = "CS0103",
                line = 5,
                fix_title = "using CoreLib.Beta;",
                apply = true,
                require_workspace = true,
            });
            JsonElement ambigApplyData = DataOf(ambigApplyResp);
            Assert.Equal("using CoreLib.Beta;", ambigApplyData.GetProperty("selected_fix").GetProperty("title").GetString());
            Assert.Equal(0, ambigApplyData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());
            Assert.Contains("using CoreLib.Beta;", await File.ReadAllTextAsync(paths.AppAmbiguousUsing));
            AssertPureLfNoBom(paths.AppAmbiguousUsing);

            // -----------------------------------------------------------------
            // 4. Unimplemented interface members (CS0535):
            //    - preview lists "Implement interface" and "Implement all members explicitly"
            //    - explicit fix_title applies explicit interface implementation on ExplicitGreeter
            //    - repair.propose_from_diagnostics + repair.apply_plan fixes DefaultGreeter on hot host
            //    - preserves LF + no BOM and produces 0 errors.
            // -----------------------------------------------------------------
            JsonElement ifacePreviewResp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.ConsumerUnimplemented,
                diagnostic_id = "CS0535",
                line = 5,
                apply = false,
                require_workspace = true,
            });
            JsonElement ifacePreviewData = DataOf(ifacePreviewResp);
            string[] ifaceOffered = ifacePreviewData.GetProperty("offered_fixes")
                .EnumerateArray()
                .Select(f => f.GetProperty("title").GetString()!)
                .ToArray();
            Assert.Contains("Implement interface", ifaceOffered);
            Assert.Contains("Implement all members explicitly", ifaceOffered);

            JsonElement explicitImplResp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.ConsumerUnimplemented,
                diagnostic_id = "CS0535",
                line = 9,
                fix_title = "Implement all members explicitly",
                apply = true,
                require_workspace = true,
            });
            JsonElement explicitImplData = DataOf(explicitImplResp);
            Assert.Equal("Implement all members explicitly", explicitImplData.GetProperty("selected_fix").GetProperty("title").GetString());
            Assert.True(explicitImplData.GetProperty("wrote_file").GetBoolean());
            Assert.Contains("string IGreeter.Greet(string name)", await File.ReadAllTextAsync(paths.ConsumerUnimplemented));
            AssertPureLfNoBom(paths.ConsumerUnimplemented);

            JsonElement proposeIfaceResp = await host.CallAsync("repair.propose_from_diagnostics", new
            {
                workspace_handle = handle,
                file_path = paths.ConsumerUnimplemented,
                require_workspace = true,
            });
            JsonElement proposeIfaceData = DataOf(proposeIfaceResp);
            Assert.Equal(1, proposeIfaceData.GetProperty("proposal_count").GetInt32());
            Assert.Equal("Implement interface", proposeIfaceData.GetProperty("proposals")[0].GetProperty("fix_title").GetString());
            JsonElement ifacePlan = proposeIfaceData.GetProperty("plan");

            JsonElement applyIfacePlanResp = await host.CallAsync("repair.apply_plan", new
            {
                workspace_handle = handle,
                plan = ifacePlan,
                require_workspace = true,
            });
            JsonElement applyIfacePlanData = DataOf(applyIfacePlanResp);
            Assert.Equal(1, applyIfacePlanData.GetProperty("succeeded_steps").GetInt32());
            Assert.Equal(0, applyIfacePlanData.GetProperty("failed_steps").GetInt32());
            Assert.Equal(0, applyIfacePlanData.GetProperty("diagnostics_after_plan").GetProperty("errors").GetInt32());
            string unimplAfter = await File.ReadAllTextAsync(paths.ConsumerUnimplemented);
            Assert.Contains("public string Greet(string name)", unimplAfter);
            Assert.Contains("public int Count", unimplAfter);
            AssertPureLfNoBom(paths.ConsumerUnimplemented);

            // -----------------------------------------------------------------
            // 5. Diagnostics with NO safe Roslyn fix (CS7036 and CS0029):
            //    - CS7036 is NOT auto-fixed -> no_fix_available (never invents an overload)
            //    - CS0029 -> no_fix_available
            //    - repair.propose_from_diagnostics reports proposal_count=0, no_fix_available=true,
            //      and lists both under unfixable_diagnostics (never TODO stubs).
            // -----------------------------------------------------------------
            string unfixableBefore = await File.ReadAllTextAsync(paths.ConsumerUnfixable);

            JsonElement cs7036Resp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.ConsumerUnfixable,
                diagnostic_id = "CS7036",
                line = 9,
                apply = true,
                require_workspace = true,
            });
            Assert.False(cs7036Resp.GetProperty("ok").GetBoolean(), cs7036Resp.GetRawText());
            Assert.False(cs7036Resp.TryGetProperty("envelope", out _), cs7036Resp.GetRawText());
            Assert.Equal("no_fix_available", cs7036Resp.GetProperty("errors")[0].GetProperty("Code").GetString());
            Assert.Empty(cs7036Resp.GetProperty("data").GetProperty("offered_fixes").EnumerateArray());

            JsonElement cs0029Resp = await host.CallAsync("edit.apply_code_fix", new
            {
                workspace_handle = handle,
                file_path = paths.ConsumerUnfixable,
                diagnostic_id = "CS0029",
                line = 10,
                apply = true,
                require_workspace = true,
            });
            Assert.False(cs0029Resp.GetProperty("ok").GetBoolean(), cs0029Resp.GetRawText());
            Assert.False(cs0029Resp.TryGetProperty("envelope", out _), cs0029Resp.GetRawText());
            Assert.Equal("no_fix_available", cs0029Resp.GetProperty("errors")[0].GetProperty("Code").GetString());
            Assert.Equal(unfixableBefore, await File.ReadAllTextAsync(paths.ConsumerUnfixable));

            JsonElement proposeUnfixableResp = await host.CallAsync("repair.propose_from_diagnostics", new
            {
                workspace_handle = handle,
                file_path = paths.ConsumerUnfixable,
                require_workspace = true,
            });
            JsonElement proposeUnfixableData = DataOf(proposeUnfixableResp);
            Assert.Equal(0, proposeUnfixableData.GetProperty("proposal_count").GetInt32());
            Assert.True(proposeUnfixableData.GetProperty("no_fix_available").GetBoolean());
            Assert.Equal(2, proposeUnfixableData.GetProperty("unfixable_count").GetInt32());
            Assert.Empty(proposeUnfixableData.GetProperty("plan").GetProperty("steps").EnumerateArray());
            Assert.DoesNotContain("TODO_ResolveMissingSymbol", proposeUnfixableData.GetRawText());

            // -----------------------------------------------------------------
            // 6. Multi-step repair plan on one CRLF+BOM file (missing using + CS0535):
            //    step 1 shifts line numbers by inserting `using CoreLib.Formatting;`,
            //    step 2 still resolves CS0535 cleanly on the shifted line.
            // -----------------------------------------------------------------
            JsonElement proposeMultiResp = await host.CallAsync("repair.propose_from_diagnostics", new
            {
                workspace_handle = handle,
                file_path = paths.ConsumerMultiStep,
                require_workspace = true,
            });
            JsonElement proposeMultiData = DataOf(proposeMultiResp);
            Assert.Equal(2, proposeMultiData.GetProperty("proposal_count").GetInt32());
            JsonElement multiPlan = proposeMultiData.GetProperty("plan");

            JsonElement applyMultiResp = await host.CallAsync("repair.apply_plan", new
            {
                workspace_handle = handle,
                plan = multiPlan,
                require_workspace = true,
            });
            JsonElement applyMultiData = DataOf(applyMultiResp);
            Assert.Equal(2, applyMultiData.GetProperty("succeeded_steps").GetInt32());
            Assert.Equal(0, applyMultiData.GetProperty("failed_steps").GetInt32());
            Assert.Equal(0, applyMultiData.GetProperty("diagnostics_after_plan").GetProperty("errors").GetInt32());
            string multiAfter = await File.ReadAllTextAsync(paths.ConsumerMultiStep);
            Assert.Contains("using CoreLib.Formatting;", multiAfter);
            Assert.Contains("public string Greet(string name)", multiAfter);
            AssertPureCrLfWithBom(paths.ConsumerMultiStep);

            _ = await host.SendAsync(new { id = "bye", method = WorkspaceHostProtocol.Method.Shutdown }, timeoutSeconds: 15);
            Assert.True(process.WaitForExit(10_000));
        }
        finally
        {
            TryKillProcessTree(process);
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private static void AssertPureLfNoBom(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"Expected no UTF-8 BOM in {filePath}.");
        string text = Utf8NoBom.GetString(bytes);
        Assert.DoesNotContain("\r\n", text);
        Assert.Contains("\n", text);
    }

    private static void AssertPureCrLfWithBom(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"Expected UTF-8 BOM in {filePath}.");
        string text = Utf8WithBom.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    private static async Task WriteUtf8BomAsync(string path, string content)
    {
        byte[] bom = Utf8WithBom.GetPreamble();
        byte[] body = Utf8WithBom.GetBytes(content);
        byte[] bytes = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
        Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task<FixturePaths> CreateThreeProjectFixtureAsync(string root)
    {
        const string projectTemplate = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              {0}
            </Project>
            """;
        const string coreRef = "<ItemGroup><ProjectReference Include=\"../CoreLib/CoreLib.csproj\" /></ItemGroup>";

        foreach (string project in new[] { "CoreLib", "App", "Consumer" })
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            await File.WriteAllTextAsync(
                Path.Combine(root, project, $"{project}.csproj"),
                string.Format(projectTemplate, project == "CoreLib" ? string.Empty : coreRef),
                Utf8NoBom);
        }

        string coreFormatting = Path.Combine(root, "CoreLib", "Formatting.cs");
        string coreAmbiguous = Path.Combine(root, "CoreLib", "Ambiguous.cs");
        string appMissingUsing = Path.Combine(root, "App", "AppRunner.cs");
        string appMissingDiag = Path.Combine(root, "App", "TimingProbe.cs");
        string appAmbiguous = Path.Combine(root, "App", "AmbiguousConsumer.cs");
        string consumerUnimpl = Path.Combine(root, "Consumer", "Greeters.cs");
        string consumerUnfixable = Path.Combine(root, "Consumer", "BrokenCalls.cs");
        string consumerMultiStep = Path.Combine(root, "Consumer", "ComboGreeter.cs");

        await File.WriteAllBytesAsync(coreFormatting, Utf8NoBom.GetBytes(CoreLibSourceLf));
        await File.WriteAllBytesAsync(coreAmbiguous, Utf8NoBom.GetBytes(CoreLibAmbiguousSourceLf));
        await WriteUtf8BomAsync(appMissingUsing, AppMissingUsingCrLf);
        await File.WriteAllBytesAsync(appMissingDiag, Utf8NoBom.GetBytes(AppMissingDiagnosticsLf));
        await File.WriteAllBytesAsync(appAmbiguous, Utf8NoBom.GetBytes(AppAmbiguousUsingLf));
        await File.WriteAllBytesAsync(consumerUnimpl, Utf8NoBom.GetBytes(ConsumerUnimplementedLf));
        await File.WriteAllBytesAsync(consumerUnfixable, Utf8NoBom.GetBytes(ConsumerUnfixableLf));
        await WriteUtf8BomAsync(consumerMultiStep, ConsumerMultiStepCrLf);

        string solutionPath = Path.Combine(root, "ThreeProjCodeFix.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="CoreLib/CoreLib.csproj" />
              <Project Path="App/App.csproj" />
              <Project Path="Consumer/Consumer.csproj" />
            </Solution>
            """,
            Utf8NoBom);

        return new FixturePaths(
            solutionPath,
            appMissingUsing,
            appMissingDiag,
            appAmbiguous,
            consumerUnimpl,
            consumerUnfixable,
            consumerMultiStep);
    }

    private sealed record FixturePaths(
        string Solution,
        string AppMissingUsing,
        string AppMissingDiagnostics,
        string AppAmbiguousUsing,
        string ConsumerUnimplemented,
        string ConsumerUnfixable,
        string ConsumerMultiStep);

    private static JsonElement DataOf(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        Assert.False(response.TryGetProperty("envelope", out _), response.GetRawText());
        return response.GetProperty("data");
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
                TryKillProcessTree(_process);
                string stderr;
                try
                {
                    stderr = await _process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    stderr = string.Empty;
                }

                throw new TimeoutException($"Workspace host did not answer. Stderr: {stderr}");
            }

            Assert.False(string.IsNullOrWhiteSpace(line), "Workspace host closed its output.");
            return JsonDocument.Parse(line!).RootElement.Clone();
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
