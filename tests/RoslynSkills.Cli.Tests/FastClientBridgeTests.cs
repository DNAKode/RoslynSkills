using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynSkills.Core.Commands;
using Xunit;

namespace RoslynSkills.Cli.Tests;

/// <summary>
/// The thin native client (<c>roscli-fast</c>) and the host-side <c>cli/run</c> bridge: with a daemon bound, the client must
/// print the same envelope the full CLI prints without ever starting the full CLI.
/// </summary>
public sealed class FastClientBridgeTests
{
    [Fact]
    public async Task FastClient_PrintsTheSameAnswerAsTheFullCli_WithoutStartingIt()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-fast-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        WorkspaceHostDaemonManager manager = new();
        try
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
                "namespace Lib;\n\npublic sealed class Service\n{\n    public int Run(int x) => x + 1;\n}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Fix.slnx"), "<Solution>\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");

            string cliDll = typeof(CliApplication).Assembly.Location;
            string fastDll = ResolveFastClientDll();

            (int useExit, string useOut, string useErr) = await RunAsync(cliDll, root, new[] { "workspace.use", "Fix.slnx", "--require-solution", "true" }, daemonMode: "auto");
            Assert.True(useExit == 0, $"workspace.use failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");

            string[] query = { "nav.find_symbol", "Lib/Service.cs", "Run", "--brief", "true" };
            (int fullExit, string fullOut, string fullErr) = await RunAsync(cliDll, root, query, daemonMode: "required");
            Assert.True(fullExit == 0, $"full CLI failed.\nSTDOUT:\n{fullOut}\nSTDERR:\n{fullErr}");

            // An empty PATH leaves the client no full CLI to fall back to, so success proves the daemon bridge answered.
            string emptyBin = Path.Combine(root, "empty-bin");
            Directory.CreateDirectory(emptyBin);
            (int fastExit, string fastOut, string fastErr) = await RunAsync(
                fastDll,
                root,
                query,
                daemonMode: "required",
                environmentOverrides: new Dictionary<string, string> { ["PATH"] = emptyBin, ["ROSCLI_FULL_PATH"] = Path.Combine(emptyBin, "missing") });
            Assert.True(fastExit == 0, $"roscli-fast failed.\nSTDOUT:\n{fastOut}\nSTDERR:\n{fastErr}");

            using JsonDocument full = JsonDocument.Parse(fullOut);
            using JsonDocument fast = JsonDocument.Parse(fastOut);
            Assert.Equal(full.RootElement.GetProperty("Ok").GetBoolean(), fast.RootElement.GetProperty("Ok").GetBoolean());
            Assert.Equal(full.RootElement.GetProperty("CommandId").GetString(), fast.RootElement.GetProperty("CommandId").GetString());
            Assert.Equal(full.RootElement.GetProperty("Summary").GetString(), fast.RootElement.GetProperty("Summary").GetString());
            Assert.Equal(full.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32(), fast.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32());
            Assert.Equal(full.RootElement.GetProperty("Data").GetProperty("matches").GetRawText(), fast.RootElement.GetProperty("Data").GetProperty("matches").GetRawText());
            Assert.Equal("daemon", fast.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
            Assert.Equal(
                full.RootElement.GetProperty("Transport").GetProperty("workspace_handle").GetString(),
                fast.RootElement.GetProperty("Transport").GetProperty("workspace_handle").GetString());

            // Relative paths in the client's working directory resolve on the host exactly as in the full CLI, and a
            // search the host serves from its text index agrees with the full CLI's.
            (int searchFastExit, string searchFastOut, _) = await RunAsync(
                fastDll, root, new[] { "ctx.search_text", "Run", "Lib" }, daemonMode: "required",
                environmentOverrides: new Dictionary<string, string> { ["PATH"] = emptyBin, ["ROSCLI_FULL_PATH"] = Path.Combine(emptyBin, "missing") });
            (int searchFullExit, string searchFullOut, _) = await RunAsync(cliDll, root, new[] { "ctx.search_text", "Run", "Lib" }, daemonMode: "required");
            Assert.Equal(0, searchFastExit);
            Assert.Equal(0, searchFullExit);
            using JsonDocument searchFast = JsonDocument.Parse(searchFastOut);
            using JsonDocument searchFull = JsonDocument.Parse(searchFullOut);
            Assert.Equal(
                searchFull.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32(),
                searchFast.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32());
            Assert.Equal("workspace_overlay_index", searchFast.RootElement.GetProperty("Data").GetProperty("analysis_scope").GetProperty("text_source").GetString());

            // The MCP server takes the same daemon route for hot-workspace tools instead of loading the solution itself.
            string mcpDll = ResolveSiblingDll("RoslynSkills.McpServer");
            ProcessStartInfo mcpStart = new("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            mcpStart.ArgumentList.Add(mcpDll);
            mcpStart.Environment["ROSCLI_DAEMON"] = "auto";
            mcpStart.Environment.Remove("ROSCLI_WORKSPACE_ALIAS");
            mcpStart.Environment.Remove("ROSLYNSKILLS_MCP_ROUTING");
            using (Process mcp = Process.Start(mcpStart)!)
            {
                try
                {
                    await mcp.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
                    await mcp.StandardInput.FlushAsync();
                    Assert.False(string.IsNullOrWhiteSpace(await mcp.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60))));
                    await mcp.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = 2,
                        method = "tools/call",
                        @params = new { name = "roslyn_nav_find_symbol", arguments = new { file_path = "Lib/Service.cs", symbol_name = "Run", brief = true } },
                    }));
                    await mcp.StandardInput.FlushAsync();
                    string? toolLine = await mcp.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120));
                    Assert.False(string.IsNullOrWhiteSpace(toolLine));
                    using JsonDocument tool = JsonDocument.Parse(toolLine!);
                    JsonElement structured = tool.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), toolLine);
                    JsonElement transport = structured.GetProperty("transport");
                    Assert.Equal("mcp", transport.GetProperty("mode").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(transport.GetProperty("daemon_endpoint").GetString()), toolLine);
                    Assert.Equal(
                        full.RootElement.GetProperty("Transport").GetProperty("workspace_handle").GetString(),
                        transport.GetProperty("workspace_handle").GetString());
                }
                finally
                {
                    try
                    {
                        if (!mcp.HasExited)
                        {
                            mcp.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            // Lifecycle verbs are not served by the bridge: the client hands them to roscli-slow.
            (int statusExit, _, string statusErr) = await RunAsync(fastDll, root, new[] { "daemon.status" }, daemonMode: "auto",
                environmentOverrides: new Dictionary<string, string> { ["PATH"] = emptyBin, ["ROSCLI_FULL_PATH"] = Path.Combine(emptyBin, "missing") });
            Assert.Equal(127, statusExit);
            Assert.Contains("roscli-slow was not found", statusErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                _ = await RunAsync(typeof(CliApplication).Assembly.Location, root, new[] { "daemon.stop" }, daemonMode: "auto");
            }
            catch
            {
            }

            try { Directory.Delete(root, recursive: true); } catch { }
            _ = manager;
        }
    }

    private static string ResolveSiblingDll(string projectName)
    {
        string sibling = Path.Combine(AppContext.BaseDirectory, $"{projectName}.dll");
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
                    string candidate = Path.Combine(current.FullName, "src", projectName, "bin", config, "net10.0", $"{projectName}.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"{projectName}.dll was not built; build RoslynSkills.slnx first.");
    }

    private static string ResolveFastClientDll()
    {
        string sibling = Path.Combine(AppContext.BaseDirectory, "roscli.dll");
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
                    string candidate = Path.Combine(current.FullName, "src", "RoslynSkills.FastClient", "bin", config, "net10.0", "roscli.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("roscli.dll was not built; build RoslynSkills.slnx first.");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
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

        // `dotnet` itself must stay resolvable when a test empties PATH.
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
