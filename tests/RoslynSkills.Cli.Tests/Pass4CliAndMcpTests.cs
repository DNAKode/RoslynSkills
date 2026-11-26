using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynSkills.Core;
using Xunit;

namespace RoslynSkills.Cli.Tests;

public sealed class Pass4CliAndMcpTests
{
    [Fact]
    public async Task CliApplication_CompactAndPrettyJsonFlags_ControlIndentation()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());

        StringWriter compactOut = new();
        int compactExit = await app.RunAsync(
            new[] { "system.ping", "--compact-json" },
            compactOut,
            new StringWriter(),
            CancellationToken.None);
        Assert.Equal(0, compactExit);
        string compactText = compactOut.ToString().TrimEnd('\r', '\n');
        Assert.DoesNotContain("\n", compactText, StringComparison.Ordinal);
        Assert.Contains("\"CommandId\":\"system.ping\"", compactText, StringComparison.Ordinal);

        StringWriter prettyOut = new();
        int prettyExit = await app.RunAsync(
            new[] { "system.ping", "--pretty-json" },
            prettyOut,
            new StringWriter(),
            CancellationToken.None);
        Assert.Equal(0, prettyExit);
        string prettyText = prettyOut.ToString().TrimEnd('\r', '\n');
        Assert.Contains("\n", prettyText, StringComparison.Ordinal);
        Assert.Contains("\"CommandId\": \"system.ping\"", prettyText, StringComparison.Ordinal);
        Assert.True(compactText.Length < prettyText.Length);

        // Last flag wins when both are provided.
        StringWriter overrideOut = new();
        int overrideExit = await app.RunAsync(
            new[] { "--compact-json", "system.ping", "--pretty-json" },
            overrideOut,
            new StringWriter(),
            CancellationToken.None);
        Assert.Equal(0, overrideExit);
        Assert.Contains("\n", overrideOut.ToString().TrimEnd('\r', '\n'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliAndFastClient_RedirectedStdoutCompactJson_AndHotFileOutline()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p4-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string cliDll = typeof(CliApplication).Assembly.Location;
        string fastDll = ResolveSiblingDll("RoslynSkills.FastClient", "roscli-fast.dll");

        try
        {
            await CreateSampleSolutionAsync(root);
            (int useExit, string useOut, string useErr) = await RunProcessAsync(
                cliDll,
                root,
                new[] { "workspace.use", "Demo.slnx", "--require-solution", "true" },
                daemonMode: "auto");
            Assert.True(useExit == 0, $"workspace.use failed.\nOUT:\n{useOut}\nERR:\n{useErr}");

            // Redirected roscli emits compact JSON by default (single line) and pretty JSON with --pretty-json.
            (int cliCompactExit, string cliCompactOut, _) = await RunProcessAsync(
                cliDll,
                root,
                new[] { "ctx.file_outline", "Lib/Service.cs" },
                daemonMode: "required");
            Assert.Equal(0, cliCompactExit);
            string cliCompactTrimmed = cliCompactOut.TrimEnd('\r', '\n');
            Assert.DoesNotContain("\n", cliCompactTrimmed, StringComparison.Ordinal);

            (int cliPrettyExit, string cliPrettyOut, _) = await RunProcessAsync(
                cliDll,
                root,
                new[] { "ctx.file_outline", "Lib/Service.cs", "--pretty-json" },
                daemonMode: "required");
            Assert.Equal(0, cliPrettyExit);
            string cliPrettyTrimmed = cliPrettyOut.TrimEnd('\r', '\n');
            Assert.Contains("\n", cliPrettyTrimmed, StringComparison.Ordinal);
            Assert.True(cliCompactTrimmed.Length < cliPrettyTrimmed.Length);

            // roscli-fast serves ctx.file_outline directly on the hot workspace (empty PATH forbids fallback) in compact JSON.
            string emptyBin = Path.Combine(root, "empty-bin");
            Directory.CreateDirectory(emptyBin);
            Dictionary<string, string> noFallbackEnv = new()
            {
                ["PATH"] = emptyBin,
                ["ROSCLI_FULL_PATH"] = Path.Combine(emptyBin, "missing"),
            };

            (int fastCompactExit, string fastCompactOut, string fastCompactErr) = await RunProcessAsync(
                fastDll,
                root,
                new[] { "ctx.file_outline", "Lib/Service.cs" },
                daemonMode: "required",
                environmentOverrides: noFallbackEnv);
            Assert.True(fastCompactExit == 0, $"roscli-fast ctx.file_outline failed.\nOUT:\n{fastCompactOut}\nERR:\n{fastCompactErr}");
            string fastCompactTrimmed = fastCompactOut.TrimEnd('\r', '\n');
            Assert.DoesNotContain("\n", fastCompactTrimmed, StringComparison.Ordinal);

            using (JsonDocument outlineDoc = JsonDocument.Parse(fastCompactTrimmed))
            {
                Assert.True(outlineDoc.RootElement.GetProperty("Ok").GetBoolean());
                Assert.Equal("daemon", outlineDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
                Assert.Equal("hot_workspace", outlineDoc.RootElement.GetProperty("Data").GetProperty("summary").GetProperty("syntax_source").GetString());
            }

            // roscli-fast also honors --pretty-json even when stdout is redirected.
            (int fastPrettyExit, string fastPrettyOut, string fastPrettyErr) = await RunProcessAsync(
                fastDll,
                root,
                new[] { "--pretty-json", "ctx.file_outline", "Lib/Service.cs" },
                daemonMode: "required",
                environmentOverrides: noFallbackEnv);
            Assert.True(fastPrettyExit == 0, $"roscli-fast --pretty-json failed.\nOUT:\n{fastPrettyOut}\nERR:\n{fastPrettyErr}");
            Assert.Contains("\n", fastPrettyOut.TrimEnd('\r', '\n'), StringComparison.Ordinal);
            Assert.True(fastCompactTrimmed.Length < fastPrettyOut.TrimEnd('\r', '\n').Length);
        }
        finally
        {
            try { _ = await RunProcessAsync(cliDll, root, new[] { "daemon.stop" }, daemonMode: "auto"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task McpServer_SlimMode_Exposes10CompleteTools_AndAutoBindsPersistentPipe()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p4-mcp-slim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string cliDll = typeof(CliApplication).Assembly.Location;
        string mcpDll = ResolveSiblingDll("RoslynSkills.McpServer", "RoslynSkills.McpServer.dll");

        try
        {
            await CreateSampleSolutionAsync(root);

            // Start MCP in --mode slim WITHOUT running workspace.use first to verify auto-bind + persistent pipe reuse.
            using Process mcp = StartMcpProcess(mcpDll, root, "--mode", "slim");
            try
            {
                JsonDocument initResp = await SendMcpAsync(mcp, 1, "initialize", new { protocolVersion = "2025-06-18" });
                using (initResp)
                {
                    string instructions = initResp.RootElement.GetProperty("result").GetProperty("instructions").GetString() ?? string.Empty;
                    Assert.Contains("slim mode", instructions, StringComparison.OrdinalIgnoreCase);
                }

                // Check tools/list: 12 slim tools (Pass 5 adds roslyn_search + roslyn_context), complete schemas, <= 6 KB total.
                (JsonDocument listResp, int listBytes) = await SendMcpWithSizeAsync(mcp, 2, "tools/list", new { });
                using (listResp)
                {
                    JsonElement tools = listResp.RootElement.GetProperty("result").GetProperty("tools");
                    Assert.Equal(12, tools.GetArrayLength());

                    HashSet<string> expectedNames = new(StringComparer.Ordinal)
                    {
                        "roslyn_search",
                        "roslyn_context",
                        "roslyn_nav_symbol",
                        "roslyn_nav_references",
                        "roslyn_nav_invocations",
                        "roslyn_nav_call_hierarchy",
                        "roslyn_nav_implementations",
                        "roslyn_diag_snapshot",
                        "roslyn_edit_rename",
                        "roslyn_edit_transaction",
                        "roslyn_edit_code_fix",
                        "roslyn_run",
                    };
                    HashSet<string> actualNames = new(StringComparer.Ordinal);
                    foreach (JsonElement tool in tools.EnumerateArray())
                    {
                        string name = tool.GetProperty("name").GetString()!;
                        actualNames.Add(name);
                        JsonElement schema = tool.GetProperty("inputSchema");
                        Assert.True(schema.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object && props.EnumerateObject().Any(),
                            $"Slim tool '{name}' must expose a complete properties schema.");
                    }

                    Assert.Equal(expectedNames, actualNames);
                    Assert.True(listBytes <= 6144, $"Slim tools/list should be <= 6 KB, got {listBytes} bytes.");
                }

                // 1. First semantic tool call auto-binds Demo.slnx on the WorkspaceHost daemon!
                string boundHandle;
                using (JsonDocument symResp = await SendMcpAsync(mcp, 3, "tools/call", new
                {
                    name = "roslyn_nav_symbol",
                    arguments = new { symbol = "Lib.Service.Leaf", brief = true },
                }, timeoutSeconds: 180))
                {
                    JsonElement structured = symResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), symResp.RootElement.GetRawText());
                    Assert.Equal("nav.go_to_definition", structured.GetProperty("commandId").GetString());
                    JsonElement transport = structured.GetProperty("transport");
                    Assert.Equal("mcp", transport.GetProperty("mode").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(transport.GetProperty("daemon_endpoint").GetString()));
                    boundHandle = transport.GetProperty("workspace_handle").GetString()!;
                    Assert.False(string.IsNullOrWhiteSpace(boundHandle));
                }

                // 2. Smart file-scoped roslyn_nav_symbol -> nav.find_symbol over same persistent pipe.
                using (JsonDocument findSymResp = await SendMcpAsync(mcp, 4, "tools/call", new
                {
                    name = "roslyn_nav_symbol",
                    arguments = new { file_path = "Lib/Service.cs", symbol_name = "Leaf", brief = true },
                }))
                {
                    JsonElement structured = findSymResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), findSymResp.RootElement.GetRawText());
                    Assert.Equal("nav.find_symbol", structured.GetProperty("commandId").GetString());
                    Assert.Equal(boundHandle, structured.GetProperty("transport").GetProperty("workspace_handle").GetString());
                }

                // 3. roslyn_nav_implementations
                using (JsonDocument implResp = await SendMcpAsync(mcp, 5, "tools/call", new
                {
                    name = "roslyn_nav_implementations",
                    arguments = new { symbol = "Lib.IService" },
                }))
                {
                    JsonElement structured = implResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), implResp.RootElement.GetRawText());
                    Assert.True(structured.GetProperty("data").GetProperty("total_matches").GetInt32() >= 1);
                }

                // 4. roslyn_nav_call_hierarchy defaults to max_depth=1
                using (JsonDocument hierResp = await SendMcpAsync(mcp, 6, "tools/call", new
                {
                    name = "roslyn_nav_call_hierarchy",
                    arguments = new { symbol = "Lib.Service.Leaf", direction = "incoming", brief = true },
                }))
                {
                    JsonElement structured = hierResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), hierResp.RootElement.GetRawText());
                    Assert.Equal(1, structured.GetProperty("data").GetProperty("query").GetProperty("max_depth").GetInt32());
                }

                // 5. roslyn_diag_snapshot
                using (JsonDocument diagResp = await SendMcpAsync(mcp, 7, "tools/call", new
                {
                    name = "roslyn_diag_snapshot",
                    arguments = new { directory_path = "Lib", brief = true, include_diagnostics = true, require_workspace = true },
                }))
                {
                    JsonElement structured = diagResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), diagResp.RootElement.GetRawText());
                    Assert.Equal(0, structured.GetProperty("data").GetProperty("summary").GetProperty("diagnostics").GetProperty("errors").GetInt32());
                }

                // 6. roslyn_run generic dispatcher -> ctx.file_outline (hot workspace) + query.batch
                using (JsonDocument outlineResp = await SendMcpAsync(mcp, 8, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new
                    {
                        command_id = "ctx.file_outline",
                        input = new { file_path = "Lib/Service.cs" },
                    },
                }))
                {
                    JsonElement structured = outlineResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), outlineResp.RootElement.GetRawText());
                    Assert.Equal("hot_workspace", structured.GetProperty("data").GetProperty("summary").GetProperty("syntax_source").GetString());
                }

                // 7. roslyn_edit_transaction dry-run
                using (JsonDocument editResp = await SendMcpAsync(mcp, 9, "tools/call", new
                {
                    name = "roslyn_edit_transaction",
                    arguments = new
                    {
                        apply = false,
                        operations = new object[]
                        {
                            new { kind = "replace_text", file_path = "Lib/Service.cs", old_text = "return 1;", new_text = "return 2;" },
                        },
                    },
                }))
                {
                    JsonElement structured = editResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), editResp.RootElement.GetRawText());
                    Assert.Equal(1, structured.GetProperty("data").GetProperty("succeeded_operations").GetInt32());
                }
            }
            finally
            {
                TryKill(mcp);
            }

            // Full mode remains backward-compatible and exposes all 59 registry tools.
            using Process fullMcp = StartMcpProcess(mcpDll, root, "--mode", "full");
            try
            {
                using (await SendMcpAsync(fullMcp, 1, "initialize", new { protocolVersion = "2025-06-18" })) { }
                (JsonDocument fullList, int fullBytes) = await SendMcpWithSizeAsync(fullMcp, 2, "tools/list", new { });
                using (fullList)
                {
                    int toolCount = fullList.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength();
                    Assert.True(toolCount >= 55, $"Expected full mode tool catalog, got {toolCount}");
                    Assert.True(fullBytes > 30_000, $"Expected full mode tools/list > 30 KB, got {fullBytes}");
                }
            }
            finally
            {
                TryKill(fullMcp);
            }
        }
        finally
        {
            try { _ = await RunProcessAsync(cliDll, root, new[] { "daemon.stop" }, daemonMode: "auto"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task CreateSampleSolutionAsync(string root)
    {
        await File.WriteAllTextAsync(
            Path.Combine(root, "Lib", "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Lib", "Service.cs"),
            """
            namespace Lib;

            public interface IService
            {
                int Leaf();
            }

            public sealed class Service : IService
            {
                public int Top() => Middle();
                public int Middle() => Leaf();
                public int Leaf()
                {
                    return 1;
                }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Demo.slnx"),
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """);
    }

    private static Process StartMcpProcess(string mcpDll, string workingDirectory, params string[] extraArgs)
    {
        ProcessStartInfo psi = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(mcpDll);
        foreach (string arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["ROSCLI_DAEMON"] = "auto";
        psi.Environment.Remove("ROSCLI_WORKSPACE_ALIAS");
        psi.Environment.Remove("ROSLYNSKILLS_MCP_ROUTING");
        psi.Environment.Remove("ROSLYNSKILLS_MCP_MODE");
        psi.Environment.Remove("ROSLYNSKILLS_MCP_AUTOBIND");
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start McpServer.");
    }

    private static async Task<JsonDocument> SendMcpAsync(Process mcp, int id, string method, object @params, int timeoutSeconds = 60)
    {
        (JsonDocument doc, _) = await SendMcpWithSizeAsync(mcp, id, method, @params, timeoutSeconds);
        return doc;
    }

    private static async Task<(JsonDocument Document, int ByteCount)> SendMcpWithSizeAsync(Process mcp, int id, string method, object @params, int timeoutSeconds = 60)
    {
        string requestLine = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        await mcp.StandardInput.WriteLineAsync(requestLine);
        await mcp.StandardInput.FlushAsync();
        string? responseLine = await mcp.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        Assert.False(string.IsNullOrWhiteSpace(responseLine), $"Empty MCP response for {method}");
        int bytes = Encoding.UTF8.GetByteCount(responseLine!);
        return (JsonDocument.Parse(responseLine!), bytes);
    }

    private static void TryKill(Process process)
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

    private static string ResolveSiblingDll(string projectName, string fileName)
    {
        string sibling = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(sibling))
        {
            return sibling;
        }

        string[] configs = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Release", "Debug" }
            : new[] { "Debug", "Release" };
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RoslynSkills.slnx")))
            {
                foreach (string config in configs)
                {
                    string candidate = Path.Combine(current.FullName, "src", projectName, "bin", config, "net10.0", fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"{fileName} was not built; build RoslynSkills.slnx first.");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string assemblyPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string daemonMode,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        int timeoutMilliseconds = 180_000)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["ROSCLI_DAEMON"] = daemonMode;
        startInfo.Environment.Remove("ROSCLI_WORKSPACE_ALIAS");
        startInfo.Environment.Remove("ROSCLI_FAST");
        if (environmentOverrides is not null)
        {
            foreach ((string name, string value) in environmentOverrides)
            {
                startInfo.Environment[name] = value;
            }
        }

        if (environmentOverrides is not null && environmentOverrides.ContainsKey("PATH"))
        {
            startInfo.FileName = Environment.ProcessPath is { } processPath && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? processPath
                : ResolveDotnetExecutable();
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start child process.");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(timeoutMilliseconds);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            TryKill(process);
        }
    }

    private static string ResolveDotnetExecutable()
    {
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            string candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        return File.Exists(programFiles) ? programFiles : "dotnet";
    }
}
