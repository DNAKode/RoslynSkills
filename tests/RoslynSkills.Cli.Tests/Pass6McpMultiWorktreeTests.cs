using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynSkills.Core.Commands;
using Xunit;

namespace RoslynSkills.Cli.Tests;

public sealed class Pass6McpMultiWorktreeTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task McpServer_SlimMode_All11ToolsDeclareWorkspacePath_AndSchemaUnder8KB()
    {
        string neutralCwd = Path.Combine(Path.GetTempPath(), $"roslynskills-p6-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(neutralCwd);
        string mcpDll = ResolveSiblingDll("RoslynSkills.McpServer", "RoslynSkills.McpServer.dll");

        try
        {
            using Process slimMcp = StartMcpProcess(mcpDll, neutralCwd, "--mode", "slim");
            try
            {
                using (JsonDocument initResp = await SendMcpAsync(slimMcp, 1, "initialize", new { protocolVersion = "2025-06-18" }))
                {
                    string instructions = initResp.RootElement.GetProperty("result").GetProperty("instructions").GetString() ?? string.Empty;
                    Assert.Contains("workspace_path", instructions, StringComparison.Ordinal);
                }

                (JsonDocument listResp, int listBytes) = await SendMcpWithSizeAsync(slimMcp, 2, "tools/list", new { });
                using (listResp)
                {
                    JsonElement tools = listResp.RootElement.GetProperty("result").GetProperty("tools");
                    Assert.Equal(12, tools.GetArrayLength());
                    Assert.True(listBytes < 8192, $"Slim tools/list must stay < 8192 bytes, got {listBytes} bytes.");

                    int workspacePathToolCount = 0;
                    foreach (JsonElement tool in tools.EnumerateArray())
                    {
                        string name = tool.GetProperty("name").GetString()!;
                        if (string.Equals(name, "roslyn_run", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        JsonElement props = tool.GetProperty("inputSchema").GetProperty("properties");
                        Assert.True(
                            props.TryGetProperty("workspace_path", out JsonElement wpProp),
                            $"Slim tool '{name}' must expose 'workspace_path' in inputSchema.properties.");
                        Assert.Equal("string", wpProp.GetProperty("type").GetString());
                        workspacePathToolCount++;
                    }

                    Assert.Equal(11, workspacePathToolCount);
                }
            }
            finally
            {
                TryKill(slimMcp);
            }
        }
        finally
        {
            try { Directory.Delete(neutralCwd, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CliApplication_InferDaemonRepoRoot_ExtractsFromWorkspacePath_Roots_FilePaths_AndOperations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-p6-infer-{Guid.NewGuid():N}");
        string repoA = Path.Combine(tempRoot, "repo-a");
        string wtB = Path.Combine(tempRoot, "wt-b");
        Directory.CreateDirectory(repoA);

        try
        {
            await CreateAndCommitSampleRepoAsync(repoA, "MainMarker", 101);
            RunGit(repoA, $"worktree add \"{wtB}\" -b feature/wt-b");

            string expectedA = Path.GetFullPath(repoA);
            string expectedB = Path.GetFullPath(wtB);

            using (JsonDocument d1 = JsonDocument.Parse(JsonSerializer.Serialize(new { workspace_path = wtB })))
            {
                Assert.Equal(expectedB, CliApplication.InferDaemonRepoRoot(d1.RootElement));
            }

            using (JsonDocument d2 = JsonDocument.Parse(JsonSerializer.Serialize(new { roots = new[] { Path.Combine(repoA, "Lib") } })))
            {
                Assert.Equal(expectedA, CliApplication.InferDaemonRepoRoot(d2.RootElement));
            }

            using (JsonDocument d3 = JsonDocument.Parse(JsonSerializer.Serialize(new { roots = new[] { Path.Combine(wtB, "Lib") } })))
            {
                Assert.Equal(expectedB, CliApplication.InferDaemonRepoRoot(d3.RootElement));
            }

            using (JsonDocument d4 = JsonDocument.Parse(JsonSerializer.Serialize(new { file_paths = new[] { Path.Combine(wtB, "Lib", "Service.cs") } })))
            {
                Assert.Equal(expectedB, CliApplication.InferDaemonRepoRoot(d4.RootElement));
            }

            using (JsonDocument d5 = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                operations = new[]
                {
                    new { kind = "replace_text", file_path = Path.Combine(wtB, "Lib", "Service.cs"), old_text = "1", new_text = "2" },
                },
            })))
            {
                Assert.Equal(expectedB, CliApplication.InferDaemonRepoRoot(d5.RootElement));
            }

            // Relative paths resolve against explicit baseDirectory (worktree root), not process CWD.
            using JsonDocument relInput = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                workspace_path = wtB,
                file_path = "Lib/Service.cs",
                roots = new[] { "Lib" },
            }));
            JsonElement resolved = CliApplication.ResolveRelativePathsForDaemon(relInput.RootElement, wtB);
            Assert.Equal(Path.GetFullPath(Path.Combine(wtB, "Lib", "Service.cs")), resolved.GetProperty("file_path").GetString());
            Assert.Equal(Path.GetFullPath(Path.Combine(wtB, "Lib")), resolved.GetProperty("roots")[0].GetString());
        }
        finally
        {
            TryCleanupTempGitRoot(repoA, wtB, tempRoot);
        }
    }

    [Fact]
    public async Task McpServer_SlimMode_RoutesTwoDistinctGitWorktreesIndependently_FromNeutralCwd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-p6-multiwt-{Guid.NewGuid():N}");
        string repoA = Path.Combine(tempRoot, "repo-a");
        string wtB = Path.Combine(tempRoot, "wt-b");
        string neutralCwd = Path.Combine(tempRoot, "neutral-cwd");
        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(neutralCwd);
        string mcpDll = ResolveSiblingDll("RoslynSkills.McpServer", "RoslynSkills.McpServer.dll");

        try
        {
            await CreateAndCommitSampleRepoAsync(repoA, "MainWorktreeOnly", 101);
            RunGit(repoA, $"worktree add \"{wtB}\" -b feature/wt-b");

            // Give wt-b distinct content and marker so we can prove routing isolation.
            string serviceB = Path.Combine(wtB, "Lib", "Service.cs");
            string serviceA = Path.Combine(repoA, "Lib", "Service.cs");
            await File.WriteAllTextAsync(
                serviceB,
                (await File.ReadAllTextAsync(serviceB))
                    .Replace("MainWorktreeOnly", "SecondWorktreeOnly", StringComparison.Ordinal)
                    .Replace("return 101;", "return 202;", StringComparison.Ordinal),
                Utf8NoBom);

            // Single slim MCP server started in neutralCwd (NOT in repoA or wtB!).
            using Process slimMcp = StartMcpProcess(mcpDll, neutralCwd, "--mode", "slim");
            try
            {
                using (await SendMcpAsync(slimMcp, 1, "initialize", new { protocolVersion = "2025-06-18" })) { }

                // 1. roslyn_context with workspace_path=repoA auto-binds repoA's daemon.
                string endpointA;
                using (JsonDocument ctxA = await SendMcpAsync(slimMcp, 2, "tools/call", new
                {
                    name = "roslyn_context",
                    arguments = new { symbol = "Lib.Service.Leaf", workspace_path = repoA },
                }, timeoutSeconds: 180))
                {
                    JsonElement structured = ctxA.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), ctxA.RootElement.GetRawText());
                    endpointA = structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString()!;
                    Assert.Contains("return 101;", structured.GetProperty("data").GetProperty("definition_source").GetProperty("content").GetString());
                }

                // 2. roslyn_context with workspace_path=wtB auto-binds wtB's independent daemon.
                string endpointB;
                using (JsonDocument ctxB = await SendMcpAsync(slimMcp, 3, "tools/call", new
                {
                    name = "roslyn_context",
                    arguments = new { symbol = "Lib.Service.Leaf", workspace_path = wtB },
                }, timeoutSeconds: 180))
                {
                    JsonElement structured = ctxB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), ctxB.RootElement.GetRawText());
                    endpointB = structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString()!;
                    Assert.NotEqual(endpointA, endpointB);
                    Assert.Contains("return 202;", structured.GetProperty("data").GetProperty("definition_source").GetProperty("content").GetString());
                }

                // 3. roslyn_search routed solely via roots[0] -> repoA vs wtB (no workspace_path!).
                using (JsonDocument searchA = await SendMcpAsync(slimMcp, 4, "tools/call", new
                {
                    name = "roslyn_search",
                    arguments = new { pattern = "MainWorktreeOnly", roots = new[] { Path.Combine(repoA, "Lib") } },
                }))
                {
                    JsonElement structured = searchA.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), searchA.RootElement.GetRawText());
                    Assert.Equal(endpointA, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.Equal("workspace_overlay_index", structured.GetProperty("data").GetProperty("analysis_scope").GetProperty("text_source").GetString());
                    Assert.True(structured.GetProperty("data").GetProperty("total_matches").GetInt32() >= 1);
                }

                using (JsonDocument searchB = await SendMcpAsync(slimMcp, 5, "tools/call", new
                {
                    name = "roslyn_search",
                    arguments = new { pattern = "SecondWorktreeOnly", roots = new[] { Path.Combine(wtB, "Lib") } },
                }))
                {
                    JsonElement structured = searchB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), searchB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.Equal("workspace_overlay_index", structured.GetProperty("data").GetProperty("analysis_scope").GetProperty("text_source").GetString());
                    Assert.True(structured.GetProperty("data").GetProperty("total_matches").GetInt32() >= 1);
                }

                // 4. roslyn_search with BOTH workspace_path and file_path routes to wtB without widening scope.
                using (JsonDocument searchScopedB = await SendMcpAsync(slimMcp, 6, "tools/call", new
                {
                    name = "roslyn_search",
                    arguments = new { pattern = "Leaf", workspace_path = wtB, file_path = "Lib/Service.cs", syntax_filter = "declarations" },
                }))
                {
                    JsonElement structured = searchScopedB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), searchScopedB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.Equal(1, structured.GetProperty("data").GetProperty("files_scanned").GetInt32());
                    Assert.Equal(1, structured.GetProperty("data").GetProperty("analysis_scope").GetProperty("resolved_scope_count").GetInt32());
                }

                // 5. roslyn_edit_transaction routed solely via operations[0].file_path -> repoA vs wtB.
                using (JsonDocument txA = await SendMcpAsync(slimMcp, 7, "tools/call", new
                {
                    name = "roslyn_edit_transaction",
                    arguments = new
                    {
                        apply = false,
                        operations = new object[]
                        {
                            new { kind = "replace_text", file_path = serviceA, old_text = "return 101;", new_text = "return 111;" },
                        },
                    },
                }))
                {
                    JsonElement structured = txA.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), txA.RootElement.GetRawText());
                    Assert.Equal(endpointA, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                }

                using (JsonDocument txB = await SendMcpAsync(slimMcp, 8, "tools/call", new
                {
                    name = "roslyn_edit_transaction",
                    arguments = new
                    {
                        apply = false,
                        operations = new object[]
                        {
                            new { kind = "replace_text", file_path = serviceB, old_text = "return 202;", new_text = "return 222;" },
                        },
                    },
                }))
                {
                    JsonElement structured = txB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), txB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                }

                // 6. All nav/diag/edit slim tools accept workspace_path cleanly on wtB.
                using (JsonDocument navSymB = await SendMcpAsync(slimMcp, 9, "tools/call", new
                {
                    name = "roslyn_nav_symbol",
                    arguments = new { symbol = "Lib.Service.Leaf", workspace_path = wtB, brief = true },
                }))
                {
                    JsonElement structured = navSymB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), navSymB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                }

                using (JsonDocument diagB = await SendMcpAsync(slimMcp, 10, "tools/call", new
                {
                    name = "roslyn_diag_snapshot",
                    arguments = new { workspace_path = wtB, brief = true, include_diagnostics = false },
                }))
                {
                    JsonElement structured = diagB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), diagB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                }

                using (JsonDocument renameB = await SendMcpAsync(slimMcp, 11, "tools/call", new
                {
                    name = "roslyn_edit_rename",
                    arguments = new { symbol = "Lib.Service.Leaf", new_name = "LeafRenamed", workspace_path = wtB, apply = false },
                }))
                {
                    JsonElement structured = renameB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), renameB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                }

                // 7. roslyn_run(ctx.read_window) with explicit workspace_path + relative file_path reads wtB from overlay!
                using (JsonDocument readB = await SendMcpAsync(slimMcp, 12, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new
                    {
                        command_id = "ctx.read_window",
                        input = new { workspace_path = wtB, file_path = "Lib/Service.cs", start_line = 1, end_line = 25 },
                    },
                }))
                {
                    JsonElement structured = readB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), readB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.Equal("workspace_overlay_index", structured.GetProperty("data").GetProperty("text_source").GetString());
                    Assert.Contains("return 202;", structured.GetProperty("data").GetProperty("content").GetString());
                }

                // 8. roslyn_run(workspace.status) routes per-worktree via workspace_path.
                using (JsonDocument statusA = await SendMcpAsync(slimMcp, 13, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new { command_id = "workspace.status", input = new { workspace_path = repoA } },
                }))
                {
                    JsonElement structured = statusA.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), statusA.RootElement.GetRawText());
                    Assert.Equal(endpointA, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.StartsWith(Path.GetFullPath(repoA), structured.GetProperty("data").GetProperty("resolved_workspace_path").GetString()!, StringComparison.OrdinalIgnoreCase);
                }

                using (JsonDocument statusB = await SendMcpAsync(slimMcp, 14, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new { command_id = "workspace.status", input = new { workspace_path = wtB } },
                }))
                {
                    JsonElement structured = statusB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), statusB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.StartsWith(Path.GetFullPath(wtB), structured.GetProperty("data").GetProperty("resolved_workspace_path").GetString()!, StringComparison.OrdinalIgnoreCase);
                }

                // 9. roslyn_run(daemon.list) reports active daemons including both endpoints.
                using (JsonDocument listDaemons = await SendMcpAsync(slimMcp, 15, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new { command_id = "daemon.list", input = new { } },
                }))
                {
                    JsonElement structured = listDaemons.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), listDaemons.RootElement.GetRawText());
                    string raw = structured.GetProperty("data").GetRawText();
                    Assert.Contains(endpointA, raw, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(endpointB, raw, StringComparison.OrdinalIgnoreCase);
                }

                // 10. roslyn_run(workspace.close) closes wtB's handle cleanly, and roslyn_run(daemon.stop) stops ONLY wtB's daemon.
                using (JsonDocument closeB = await SendMcpAsync(slimMcp, 16, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new { command_id = "workspace.close", input = new { workspace_path = wtB } },
                }))
                {
                    JsonElement structured = closeB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), closeB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.True(structured.GetProperty("data").GetProperty("closed").GetBoolean());
                }

                using (JsonDocument stopB = await SendMcpAsync(slimMcp, 17, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new { command_id = "daemon.stop", input = new { workspace_path = wtB } },
                }))
                {
                    JsonElement structured = stopB.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), stopB.RootElement.GetRawText());
                    Assert.Equal(endpointB, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                    Assert.True(structured.GetProperty("data").GetProperty("stopped").GetBoolean());
                }

                // repoA's daemon must still be alive and responsive!
                using (JsonDocument stillAliveA = await SendMcpAsync(slimMcp, 18, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new { command_id = "workspace.status", input = new { workspace_path = repoA } },
                }))
                {
                    JsonElement structured = stillAliveA.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), stillAliveA.RootElement.GetRawText());
                    Assert.Equal(endpointA, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                }

                // Stop repoA's daemon via roslyn_run(daemon.stop).
                using (JsonDocument stopA = await SendMcpAsync(slimMcp, 19, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new { command_id = "daemon.stop", input = new { workspace_path = repoA } },
                }))
                {
                    JsonElement structured = stopA.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), stopA.RootElement.GetRawText());
                    Assert.Equal(endpointA, structured.GetProperty("transport").GetProperty("daemon_endpoint").GetString());
                }
            }
            finally
            {
                TryKill(slimMcp);
            }
        }
        finally
        {
            await StopDaemonSafeAsync(wtB);
            await StopDaemonSafeAsync(repoA);
            TryCleanupTempGitRoot(repoA, wtB, tempRoot);
        }
    }

    private static async Task StopDaemonSafeAsync(string repoRoot)
    {
        try
        {
            WorkspaceHostDaemonManager manager = new();
            int? pid = manager.TryGetDaemonProcessId(repoRoot);
            _ = await manager.StopAsync(repoRoot, CancellationToken.None);
            if (pid is > 0)
            {
                try
                {
                    using Process p = Process.GetProcessById(pid.Value);
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                }
                catch { }
            }

            try { new WorkspaceAliasStore(repoRoot).Clear(); } catch { }
        }
        catch
        {
        }
    }

    private static async Task CreateAndCommitSampleRepoAsync(string repoRoot, string marker, int returnValue)
    {
        RunGit(repoRoot, "init -b main");
        RunGit(repoRoot, "config user.name \"RoslynSkills Test\"");
        RunGit(repoRoot, "config user.email \"test@example.com\"");
        RunGit(repoRoot, "config commit.gpgsign false");

        string libDir = Path.Combine(repoRoot, "Lib");
        Directory.CreateDirectory(libDir);

        await File.WriteAllTextAsync(
            Path.Combine(libDir, "Lib.csproj"),
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

        await File.WriteAllTextAsync(
            Path.Combine(libDir, "Service.cs"),
            $$"""
            namespace Lib;

            public interface IService
            {
                int Leaf();
            }

            public sealed class Service : IService
            {
                public const string Marker = "{{marker}}";
                public int Top() => Middle();
                public int Middle() => Leaf();
                public int Leaf()
                {
                    return {{returnValue}};
                }
            }
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(libDir, "Extra.cs"),
            """
            namespace Lib;

            public sealed class Extra
            {
                public int Leaf() => 0;
            }
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "Demo.slnx"),
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """,
            Utf8NoBom);

        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -m \"Initial sample repo\"");
    }

    private static Process StartMcpProcess(string mcpDll, string workingDirectory, params string[] extraArgs)
    {
        ProcessStartInfo psi = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(mcpDll);
        foreach (string arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["ROSCLI_DAEMON"] = "auto";
        psi.Environment.Remove("ROSLYNSKILLS_MCP_MODE");
        psi.Environment.Remove("ROSLYNSKILLS_MCP_ROUTING");
        psi.Environment.Remove("ROSLYNSKILLS_MCP_AUTOBIND");
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start RoslynSkills.McpServer.");
    }

    private static async Task<JsonDocument> SendMcpAsync(Process mcp, int id, string method, object @params, int timeoutSeconds = 60)
    {
        (JsonDocument doc, _) = await SendMcpWithSizeAsync(mcp, id, method, @params, timeoutSeconds);
        return doc;
    }

    private static async Task<(JsonDocument Doc, int ByteCount)> SendMcpWithSizeAsync(
        Process mcp,
        int id,
        string method,
        object @params,
        int timeoutSeconds = 60)
    {
        string requestJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params,
        });
        await mcp.StandardInput.WriteLineAsync(requestJson);
        await mcp.StandardInput.FlushAsync();

        string? line = await mcp.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        Assert.False(string.IsNullOrWhiteSpace(line), $"No JSON-RPC response received for method '{method}' (id={id}).");
        int bytes = Encoding.UTF8.GetByteCount(line!);
        return (JsonDocument.Parse(line!), bytes);
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        ProcessStartInfo startInfo = new("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}.");
        try
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                TryKill(process);
                throw new TimeoutException($"git {arguments} timed out.");
            }

            string stdout = stdoutTask.Wait(TimeSpan.FromSeconds(5)) ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.Wait(TimeSpan.FromSeconds(5)) ? stderrTask.Result : string.Empty;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {arguments} failed ({process.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }
        }
        finally
        {
            TryKill(process);
        }
    }

    private static string ResolveSiblingDll(string projectFolderName, string dllFileName)
    {
        string testBinDir = Path.GetDirectoryName(typeof(Pass6McpMultiWorktreeTests).Assembly.Location)!;
        string tfm = Path.GetFileName(testBinDir);
        string config = Path.GetFileName(Path.GetDirectoryName(testBinDir)!);
        string repoRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));
        string candidate = Path.Combine(repoRoot, "src", projectFolderName, "bin", config, tfm, dllFileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        string fallback = Path.Combine(testBinDir, dllFileName);
        if (File.Exists(fallback))
        {
            return fallback;
        }

        throw new FileNotFoundException($"Could not locate {dllFileName} at '{candidate}' or '{fallback}'.");
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

    private static void TryCleanupTempGitRoot(string mainRepo, string worktreeRoot, string tempRoot)
    {
        try
        {
            if (Directory.Exists(mainRepo) && Directory.Exists(worktreeRoot))
            {
                RunGit(mainRepo, $"worktree remove --force \"{worktreeRoot}\"");
            }
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(tempRoot))
            {
                foreach (string file in Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
