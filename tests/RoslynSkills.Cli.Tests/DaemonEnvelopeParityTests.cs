using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.Core.Commands;
using Xunit;

namespace RoslynSkills.Cli.Tests;

public sealed class DaemonEnvelopeParityTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] ExpectedTopLevelKeys =
    {
        "Ok",
        "CommandId",
        "Version",
        "Preview",
        "Data",
        "Telemetry",
        "Transport",
        "Errors",
        "TraceId",
        "Summary",
    };

    private static readonly HashSet<string> DynamicDataKeys = new(StringComparer.Ordinal)
    {
        "workspace_handle",
        "workspace_cache_mode",
        "workspace_cache_hit",
        "resolution_source",
        "hot_workspace_refresh",
        "loaded_at_utc",
        "last_refresh_utc",
        "workspace_load_duration_ms",
        "msbuild_registration_duration_ms",
        "store_workspace_count",
        "tracked_paths",
        "incremental_updates",
        "full_rebuilds",
        "last_sync_utc",
        "warmup",
        "warmup_state",
        "projects_warmed",
        "warmup_timed_out",
    };

    [Fact]
    public async Task DaemonAndInProcess_ProduceIdenticalEnvelopeAndDataShape_AcrossCoreCommands()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-qvcn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        WorkspaceHostDaemonManager manager = new();
        int? trackedDaemonPid = null;

        try
        {
            (string solutionPath, string calculatorPath, string runnerPath, int addLine, int addCol, string originalRunnerSource) =
                await CreateTwoProjectFixtureAsync(tempRoot);

            // Start daemon and bind default alias.
            (int useExit, string useOut, string useErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.use", "ParityDemo.slnx", "--require-solution", "true" },
                daemonMode: "auto");
            Assert.True(useExit == 0, $"workspace.use failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");
            trackedDaemonPid = manager.TryGetDaemonProcessId(tempRoot);

            string daemonHandle;
            using (JsonDocument useDoc = JsonDocument.Parse(useOut))
            {
                AssertTopLevelEnvelopeShape(useDoc.RootElement, expectedCommandId: "workspace.use", expectedTransportMode: "daemon");
                Assert.True(useDoc.RootElement.GetProperty("Ok").GetBoolean(), useOut);
                daemonHandle = useDoc.RootElement.GetProperty("Data").GetProperty("workspace_handle").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(daemonHandle));
            }

            // 1. nav.find_symbol
            string[] findArgs =
            {
                "nav.find_symbol", "src/Core/Calculator.cs", "Add",
                "--workspace-path", solutionPath,
                "--require-workspace", "true",
                "--brief", "true",
            };
            (int findOffExit, string findOffOut, string findOffErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: findArgs,
                daemonMode: "off");
            Assert.True(findOffExit == 0, $"nav.find_symbol (off) failed.\nSTDOUT:\n{findOffOut}\nSTDERR:\n{findOffErr}");

            (int findReqExit, string findReqOut, string findReqErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: findArgs,
                daemonMode: "required");
            Assert.True(findReqExit == 0, $"nav.find_symbol (required) failed.\nSTDOUT:\n{findReqOut}\nSTDERR:\n{findReqErr}");

            AssertParity(findOffOut, findReqOut, "nav.find_symbol", expectedDaemonHandle: daemonHandle);

            // 2. nav.find_references
            string[] refsArgs =
            {
                "nav.find_references", "src/Core/Calculator.cs",
                addLine.ToString(CultureInfo.InvariantCulture),
                addCol.ToString(CultureInfo.InvariantCulture),
                "--workspace-path", solutionPath,
                "--require-workspace", "true",
            };
            (int refsOffExit, string refsOffOut, string refsOffErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: refsArgs,
                daemonMode: "off");
            Assert.True(refsOffExit == 0, $"nav.find_references (off) failed.\nSTDOUT:\n{refsOffOut}\nSTDERR:\n{refsOffErr}");

            (int refsReqExit, string refsReqOut, string refsReqErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: refsArgs,
                daemonMode: "required");
            Assert.True(refsReqExit == 0, $"nav.find_references (required) failed.\nSTDOUT:\n{refsReqOut}\nSTDERR:\n{refsReqErr}");

            AssertParity(refsOffOut, refsReqOut, "nav.find_references", expectedDaemonHandle: daemonHandle);

            // 3. diag.get_file_diagnostics
            string[] diagArgs =
            {
                "diag.get_file_diagnostics", "src/App/Runner.cs",
                "--workspace-path", solutionPath,
                "--require-workspace", "true",
            };
            (int diagOffExit, string diagOffOut, string diagOffErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: diagArgs,
                daemonMode: "off");
            Assert.True(diagOffExit == 0, $"diag.get_file_diagnostics (off) failed.\nSTDOUT:\n{diagOffOut}\nSTDERR:\n{diagOffErr}");

            (int diagReqExit, string diagReqOut, string diagReqErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: diagArgs,
                daemonMode: "required");
            Assert.True(diagReqExit == 0, $"diag.get_file_diagnostics (required) failed.\nSTDOUT:\n{diagReqOut}\nSTDERR:\n{diagReqErr}");

            AssertParity(diagOffOut, diagReqOut, "diag.get_file_diagnostics", expectedDaemonHandle: daemonHandle);

            // 4. edit.replace_in_member (run on identical starting source in both modes)
            string[] editArgs =
            {
                "edit.replace_in_member", "src/App/Runner.cs",
                "--member-name", "Compute",
                "--old-text", "return new Calculator().Add(a, b);",
                "--new-text", "return new Calculator().Add(a, b) + 1;",
                "--workspace-path", solutionPath,
                "--apply", "true",
            };
            (int editOffExit, string editOffOut, string editOffErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: editArgs,
                daemonMode: "off");
            Assert.True(editOffExit == 0, $"edit.replace_in_member (off) failed.\nSTDOUT:\n{editOffOut}\nSTDERR:\n{editOffErr}");

            // Restore identical starting file contents before daemon edit.
            await File.WriteAllTextAsync(runnerPath, originalRunnerSource, Utf8NoBom);

            (int editReqExit, string editReqOut, string editReqErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: editArgs,
                daemonMode: "required");
            Assert.True(editReqExit == 0, $"edit.replace_in_member (required) failed.\nSTDOUT:\n{editReqOut}\nSTDERR:\n{editReqErr}");

            AssertParity(editOffOut, editReqOut, "edit.replace_in_member", expectedDaemonHandle: daemonHandle);

            // 5. workspace.status
            // 5a. Daemon path via subprocess under ROSCLI_DAEMON=required.
            (int statusReqExit, string statusReqOut, string statusReqErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.status", "--repo-root", tempRoot },
                daemonMode: "required");
            Assert.True(statusReqExit == 0, $"workspace.status (required) failed.\nSTDOUT:\n{statusReqOut}\nSTDERR:\n{statusReqErr}");

            // 5b. In-process path with a preloaded workspace so Ok=true Data parity is verified key-for-key.
            string statusOffOkOut;
            CliApplication inProcessApp = new(DefaultRegistryFactory.Create());
            string inProcessHandle;
            {
                StringWriter preloadWriter = new(CultureInfo.InvariantCulture);
                StringWriter preloadErr = new(CultureInfo.InvariantCulture);
                int preloadCode = await inProcessApp.RunAsync(
                    new[] { "--no-daemon", "workspace.preload", solutionPath, "--require-solution", "true" },
                    preloadWriter,
                    preloadErr,
                    CancellationToken.None);
                Assert.True(preloadCode == 0, $"In-process workspace.preload failed: {preloadWriter}");
                using JsonDocument preloadDoc = JsonDocument.Parse(preloadWriter.ToString());
                AssertTopLevelEnvelopeShape(preloadDoc.RootElement, "workspace.preload", "in_process");
                inProcessHandle = preloadDoc.RootElement.GetProperty("Data").GetProperty("workspace_handle").GetString()!;
            }

            try
            {
                StringWriter statusWriter = new(CultureInfo.InvariantCulture);
                StringWriter statusErr = new(CultureInfo.InvariantCulture);
                int statusCode = await inProcessApp.RunAsync(
                    new[] { "--no-daemon", "workspace.status", inProcessHandle },
                    statusWriter,
                    statusErr,
                    CancellationToken.None);
                Assert.True(statusCode == 0, $"In-process workspace.status failed: {statusWriter}");
                statusOffOkOut = statusWriter.ToString();
            }
            finally
            {
                StringWriter closeWriter = new(CultureInfo.InvariantCulture);
                StringWriter closeErr = new(CultureInfo.InvariantCulture);
                _ = await inProcessApp.RunAsync(
                    new[] { "--no-daemon", "workspace.close", inProcessHandle },
                    closeWriter,
                    closeErr,
                    CancellationToken.None);
            }

            AssertParity(statusOffOkOut, statusReqOut, "workspace.status", expectedDaemonHandle: daemonHandle);

            // 5c. Subprocess with ROSCLI_DAEMON=off must NOT contact the daemon (stays in_process).
            (int statusOffSubExit, string statusOffSubOut, string statusOffSubErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.status", daemonHandle },
                daemonMode: "off");
            Assert.NotEqual(0, statusOffSubExit);
            using (JsonDocument statusOffSubDoc = JsonDocument.Parse(statusOffSubOut))
            {
                AssertTopLevelEnvelopeShape(statusOffSubDoc.RootElement, "workspace.status", "in_process");
                Assert.False(statusOffSubDoc.RootElement.GetProperty("Ok").GetBoolean());
                Assert.Equal("workspace_not_found", statusOffSubDoc.RootElement.GetProperty("Errors")[0].GetProperty("Code").GetString());
            }

            _ = calculatorPath;
            _ = statusOffSubErr;
        }
        finally
        {
            await CleanupTempDaemonAsync(tempRoot, manager, trackedDaemonPid);
        }
    }

    [Fact]
    public async Task TransportServerAndMcpServer_EmitFlatCommandEnvelopeWithTransportMetadata()
    {
        // 1. TransportServer stdio JSONL
        string transportDll = ResolveProjectDllPath("RoslynSkills.TransportServer");
        using (Process transportProcess = StartStdioProcess(transportDll))
        {
            try
            {
                Task<string> stderrTask = transportProcess.StandardError.ReadToEndAsync();
                await transportProcess.StandardInput.WriteLineAsync(
                    "{\"id\":\"t1\",\"method\":\"tool/call\",\"command_id\":\"system.ping\",\"input\":{}}");
                await transportProcess.StandardInput.FlushAsync();

                string? pingLine = await ReadLineWithTimeoutAsync(transportProcess.StandardOutput, "transport tool/call", 20);
                Assert.False(string.IsNullOrWhiteSpace(pingLine));
                using (JsonDocument doc = JsonDocument.Parse(pingLine!))
                {
                    JsonElement root = doc.RootElement;
                    Assert.True(root.GetProperty("Ok").GetBoolean(), pingLine);
                    Assert.Equal("system.ping", root.GetProperty("CommandId").GetString());
                    Assert.False(root.TryGetProperty("envelope", out _), pingLine);
                    Assert.False(root.GetProperty("Data").TryGetProperty("envelope", out _), pingLine);
                    Assert.Equal("transport_server", root.GetProperty("Transport").GetProperty("mode").GetString());
                }

                await transportProcess.StandardInput.WriteLineAsync("{\"id\":\"t2\",\"method\":\"shutdown\"}");
                await transportProcess.StandardInput.FlushAsync();
                _ = await ReadLineWithTimeoutAsync(transportProcess.StandardOutput, "transport shutdown", 10);
                Assert.True(await WaitForProcessExitAsync(transportProcess, TimeSpan.FromSeconds(10)));
                _ = await DrainWithTimeoutAsync(stderrTask, TimeSpan.FromSeconds(5));
            }
            finally
            {
                TryKillProcessTree(transportProcess);
            }
        }

        // 2. McpServer stdio newline-delimited JSON-RPC
        string mcpDll = ResolveProjectDllPath("RoslynSkills.McpServer");
        using (Process mcpProcess = StartStdioProcess(mcpDll))
        {
            try
            {
                Task<string> stderrTask = mcpProcess.StandardError.ReadToEndAsync();

                await mcpProcess.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new { protocolVersion = "2025-06-18" },
                }));
                await mcpProcess.StandardInput.FlushAsync();

                string? initLine = await ReadLineWithTimeoutAsync(mcpProcess.StandardOutput, "mcp initialize", 20);
                Assert.False(string.IsNullOrWhiteSpace(initLine));
                using (JsonDocument initResp = JsonDocument.Parse(initLine!))
                {
                    Assert.True(initResp.RootElement.TryGetProperty("result", out _), initLine);
                }

                // 2a. tools/call
                await mcpProcess.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new
                    {
                        name = "roslyn_system_ping",
                        arguments = new { },
                    },
                }));
                await mcpProcess.StandardInput.FlushAsync();

                string? toolLine = await ReadLineWithTimeoutAsync(mcpProcess.StandardOutput, "mcp tools/call", 20);
                Assert.False(string.IsNullOrWhiteSpace(toolLine));
                using (JsonDocument toolResp = JsonDocument.Parse(toolLine!))
                {
                    JsonElement structured = toolResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), structured.GetRawText());
                    Assert.Equal("system.ping", structured.GetProperty("commandId").GetString());
                    Assert.False(structured.TryGetProperty("envelope", out _), structured.GetRawText());
                    Assert.Equal("mcp", structured.GetProperty("transport").GetProperty("mode").GetString());
                }

                // 2b. resources/read roslyn://command/system.ping
                await mcpProcess.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "resources/read",
                    @params = new
                    {
                        uri = "roslyn://command/system.ping",
                    },
                }));
                await mcpProcess.StandardInput.FlushAsync();

                string? resLine = await ReadLineWithTimeoutAsync(mcpProcess.StandardOutput, "mcp resources/read", 20);
                Assert.False(string.IsNullOrWhiteSpace(resLine));
                using (JsonDocument resResp = JsonDocument.Parse(resLine!))
                {
                    string payloadText = resResp.RootElement
                        .GetProperty("result")
                        .GetProperty("contents")[0]
                        .GetProperty("text")
                        .GetString()!;
                    using JsonDocument payloadDoc = JsonDocument.Parse(payloadText);
                    JsonElement payload = payloadDoc.RootElement;
                    Assert.True(payload.GetProperty("ok").GetBoolean(), payloadText);
                    Assert.Equal("system.ping", payload.GetProperty("commandId").GetString());
                    Assert.False(payload.TryGetProperty("envelope", out _), payloadText);
                    Assert.True(payload.TryGetProperty("data", out _), payloadText);
                    Assert.Equal("mcp", payload.GetProperty("transport").GetProperty("mode").GetString());
                }

                mcpProcess.StandardInput.Close();
                Assert.True(await WaitForProcessExitAsync(mcpProcess, TimeSpan.FromSeconds(10)));
                _ = await DrainWithTimeoutAsync(stderrTask, TimeSpan.FromSeconds(5));
            }
            finally
            {
                TryKillProcessTree(mcpProcess);
            }
        }
    }

    private static void AssertParity(
        string inProcessJson,
        string daemonJson,
        string expectedCommandId,
        string expectedDaemonHandle)
    {
        using JsonDocument offDoc = JsonDocument.Parse(inProcessJson);
        using JsonDocument reqDoc = JsonDocument.Parse(daemonJson);

        JsonElement offRoot = offDoc.RootElement;
        JsonElement reqRoot = reqDoc.RootElement;

        AssertTopLevelEnvelopeShape(offRoot, expectedCommandId, expectedTransportMode: "in_process");
        AssertTopLevelEnvelopeShape(reqRoot, expectedCommandId, expectedTransportMode: "daemon");

        Assert.True(offRoot.GetProperty("Ok").GetBoolean(), $"in_process {expectedCommandId} not Ok:\n{inProcessJson}");
        Assert.True(reqRoot.GetProperty("Ok").GetBoolean(), $"daemon {expectedCommandId} not Ok:\n{daemonJson}");

        // Exact same top-level key set in identical order.
        string[] offTopKeys = offRoot.EnumerateObject().Select(p => p.Name).ToArray();
        string[] reqTopKeys = reqRoot.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(offTopKeys, reqTopKeys);

        // Neither path may wrap payload inside Data.envelope.
        JsonElement offData = offRoot.GetProperty("Data");
        JsonElement reqData = reqRoot.GetProperty("Data");
        Assert.Equal(JsonValueKind.Object, offData.ValueKind);
        Assert.Equal(JsonValueKind.Object, reqData.ValueKind);
        Assert.False(offData.TryGetProperty("envelope", out _), $"in_process {expectedCommandId} had Data.envelope");
        Assert.False(reqData.TryGetProperty("envelope", out _), $"daemon {expectedCommandId} had Data.envelope");

        // Exact same Data top-level property keys.
        string[] offDataKeys = offData.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        string[] reqDataKeys = reqData.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(offDataKeys, reqDataKeys);

        // Normalized semantic Data payload must be identical across in_process and daemon.
        string normalizedOff = NormalizeDynamicFields(JsonNode.Parse(offData.GetRawText())!).ToJsonString();
        string normalizedReq = NormalizeDynamicFields(JsonNode.Parse(reqData.GetRawText())!).ToJsonString();
        Assert.Equal(normalizedOff, normalizedReq);

        // Verify Transport metadata details on both paths.
        JsonElement offTransport = offRoot.GetProperty("Transport");
        Assert.Equal("in_process", offTransport.GetProperty("mode").GetString());
        Assert.True(offTransport.GetProperty("timings").GetProperty("total_ms").GetDouble() >= 0);

        JsonElement reqTransport = reqRoot.GetProperty("Transport");
        Assert.Equal("daemon", reqTransport.GetProperty("mode").GetString());
        Assert.Equal(expectedDaemonHandle, reqTransport.GetProperty("workspace_handle").GetString());
        Assert.False(string.IsNullOrWhiteSpace(reqTransport.GetProperty("daemon_endpoint").GetString()));
        JsonElement reqTimings = reqTransport.GetProperty("timings");
        Assert.True(reqTimings.GetProperty("host_elapsed_ms").GetDouble() >= 0);
        Assert.True(reqTimings.GetProperty("roundtrip_ms").GetDouble() >= 0);
        Assert.True(reqTimings.GetProperty("total_ms").GetDouble() >= 0);
    }

    private static void AssertTopLevelEnvelopeShape(
        JsonElement root,
        string expectedCommandId,
        string expectedTransportMode)
    {
        string[] actualKeys = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(ExpectedTopLevelKeys, actualKeys);
        Assert.Equal(expectedCommandId, root.GetProperty("CommandId").GetString());
        Assert.Equal(expectedTransportMode, root.GetProperty("Transport").GetProperty("mode").GetString());
    }

    private static JsonNode NormalizeDynamicFields(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            JsonObject normalized = new();
            foreach (KeyValuePair<string, JsonNode?> kvp in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (DynamicDataKeys.Contains(kvp.Key))
                {
                    continue;
                }

                normalized[kvp.Key] = kvp.Value is null ? null : NormalizeDynamicFields(kvp.Value.DeepClone());
            }

            return normalized;
        }

        if (node is JsonArray arr)
        {
            JsonArray normalized = new();
            foreach (JsonNode? item in arr)
            {
                normalized.Add(item is null ? null : NormalizeDynamicFields(item.DeepClone()));
            }

            return normalized;
        }

        return node;
    }

    private static async Task<(string SolutionPath, string CalculatorPath, string RunnerPath, int AddLine, int AddCol, string RunnerSource)>
        CreateTwoProjectFixtureAsync(string root)
    {
        string coreDir = Path.Combine(root, "src", "Core");
        string appDir = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(appDir);

        await File.WriteAllTextAsync(
            Path.Combine(coreDir, "Core.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """,
            Utf8NoBom);

        string calculatorSource =
            """
            namespace Core;

            public sealed class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """;
        string calculatorPath = Path.Combine(coreDir, "Calculator.cs");
        await File.WriteAllTextAsync(calculatorPath, calculatorSource, Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(appDir, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """,
            Utf8NoBom);

        string runnerSource =
            """
            using Core;

            namespace App;

            public sealed class Runner
            {
                public int Compute(int a, int b)
                {
                    return new Calculator().Add(a, b);
                }
            }
            """;
        string runnerPath = Path.Combine(appDir, "Runner.cs");
        await File.WriteAllTextAsync(runnerPath, runnerSource, Utf8NoBom);

        string solutionPath = Path.Combine(root, "ParityDemo.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="src/Core/Core.csproj" />
              <Project Path="src/App/App.csproj" />
            </Solution>
            """,
            Utf8NoBom);

        string[] calcLines = calculatorSource.Split('\n');
        int addLine = -1;
        int addCol = -1;
        for (int i = 0; i < calcLines.Length; i++)
        {
            int idx = calcLines[i].IndexOf("Add(", StringComparison.Ordinal);
            if (idx >= 0)
            {
                addLine = i + 1;
                addCol = idx + 1;
                break;
            }
        }

        Assert.True(addLine > 0 && addCol > 0);
        return (solutionPath, calculatorPath, runnerPath, addLine, addCol, runnerSource);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliProcessAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string daemonMode,
        int timeoutMilliseconds = 120_000)
    {
        string cliDllPath = typeof(CliApplication).Assembly.Location;
        ProcessStartInfo startInfo = new(ResolveDotnetExecutable())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliDllPath);
        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ROSCLI_DAEMON"] = daemonMode;
        startInfo.Environment.Remove("ROSCLI_WORKSPACE_ALIAS");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roscli child process.");
        try
        {
            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource waitCts = new(timeoutMilliseconds);
            try
            {
                await process.WaitForExitAsync(waitCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw new TimeoutException($"roscli process timed out after {timeoutMilliseconds}ms for args: {string.Join(' ', arguments)}");
            }

            Task allDrains = Task.WhenAll(stdOutTask, stdErrTask);
            if (await Task.WhenAny(allDrains, Task.Delay(TimeSpan.FromSeconds(10))) != allDrains)
            {
                TryKillProcessTree(process);
                throw new TimeoutException($"roscli process exited (code={process.ExitCode}) but stdout/stderr did not close within 10s.");
            }

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        finally
        {
            TryKillProcessTree(process);
        }
    }

    private static Process StartStdioProcess(string assemblyPath)
    {
        ProcessStartInfo startInfo = new(ResolveDotnetExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {assemblyPath}.");
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, string label, int timeoutSeconds)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for line ({label}).");
        }
    }

    private static string ResolveProjectDllPath(string projectName)
    {
        string sibling = Path.Combine(AppContext.BaseDirectory, $"{projectName}.dll");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        string[] configs = configuration == "Release" ? new[] { "Release", "Debug" } : new[] { "Debug", "Release" };

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

        throw new FileNotFoundException($"Could not locate {projectName}.dll for parity test.");
    }

    private static string ResolveDotnetExecutable()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
        {
            return processPath;
        }

        return "dotnet";
    }

    private static async Task CleanupTempDaemonAsync(
        string tempRoot,
        WorkspaceHostDaemonManager manager,
        int? trackedPid)
    {
        int? manifestPid = null;
        try
        {
            manifestPid = manager.TryGetDaemonProcessId(tempRoot);
            using CancellationTokenSource stopCts = new(TimeSpan.FromSeconds(15));
            await manager.StopAsync(tempRoot, stopCts.Token);
        }
        catch
        {
        }
        finally
        {
            TryKillProcessById(manifestPid);
            TryKillProcessById(trackedPid);
            try { new WorkspaceAliasStore(tempRoot).Clear(); } catch { }
            try
            {
                string manifestPath = RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(tempRoot);
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { return process.HasExited; } catch { return true; }
        }
    }

    private static async Task<string> DrainWithTimeoutAsync(Task<string> drainTask, TimeSpan timeout)
    {
        Task finished = await Task.WhenAny(drainTask, Task.Delay(timeout));
        return finished == drainTask ? await drainTask : string.Empty;
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

    private static void TryKillProcessById(int? processId)
    {
        if (processId is null or <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
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
