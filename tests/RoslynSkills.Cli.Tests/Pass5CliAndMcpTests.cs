using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynSkills.Core;
using Xunit;

namespace RoslynSkills.Cli.Tests;

public sealed class Pass5CliAndMcpTests
{
    [Fact]
    public async Task CliApplication_DescribeCommandEmitsInputSchema_AndPreservesStringCoercion()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());

        // 1. describe-command emits full descriptor-driven JSON Schema
        StringWriter descOut = new();
        int descExit = await app.RunAsync(
            new[] { "describe-command", "ctx.search_text", "--compact-json" },
            descOut,
            new StringWriter(),
            CancellationToken.None);
        Assert.Equal(0, descExit);

        using (JsonDocument doc = JsonDocument.Parse(descOut.ToString()))
        {
            Assert.True(doc.RootElement.GetProperty("Ok").GetBoolean());
            JsonElement schema = doc.RootElement.GetProperty("Data").GetProperty("input_schema");
            JsonElement props = schema.GetProperty("properties");
            Assert.True(props.TryGetProperty("syntax_filter", out JsonElement filterProp));
            Assert.Contains("declarations", filterProp.GetProperty("enum").GetRawText(), StringComparison.Ordinal);
            Assert.True(props.TryGetProperty("queries", out _));
        }

        // 2. Schema-driven coercion keeps numeric-looking --pattern "3191" as a JSON string!
        string tempFile = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-coerce-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(tempFile, "public class Code { public const int Id = 3191; }");
            StringWriter searchOut = new();
            int searchExit = await app.RunAsync(
                new[] { "ctx.search_text", "--pattern", "3191", "--file-path", tempFile, "--compact-json" },
                searchOut,
                new StringWriter(),
                CancellationToken.None);
            Assert.Equal(0, searchExit);
            using JsonDocument searchDoc = JsonDocument.Parse(searchOut.ToString());
            Assert.True(searchDoc.RootElement.GetProperty("Ok").GetBoolean(), searchOut.ToString());
            Assert.Equal(1, searchDoc.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32());
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task CliAndFastClient_WaitWarmup_OverlaySearch_ReadWindow_AndSymbolContext()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-fast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string cliDll = typeof(CliApplication).Assembly.Location;
        string fastDll = ResolveSiblingDll("RoslynSkills.FastClient", "roscli-fast.dll");

        try
        {
            await CreateSampleSolutionAsync(root);

            // workspace.use --wait-warmup blocks until background warmup & overlay index are complete.
            (int useExit, string useOut, string useErr) = await RunProcessAsync(
                cliDll,
                root,
                new[] { "workspace.use", "Demo.slnx", "--require-solution", "true", "--wait-warmup", "true" },
                daemonMode: "auto");
            Assert.True(useExit == 0, $"workspace.use failed.\nOUT:\n{useOut}\nERR:\n{useErr}");
            using (JsonDocument useDoc = JsonDocument.Parse(useOut))
            {
                Assert.True(useDoc.RootElement.GetProperty("Ok").GetBoolean(), useOut);
                JsonElement data = useDoc.RootElement.GetProperty("Data");
                Assert.Equal("complete", data.GetProperty("warmup_state").GetString());
                Assert.True(data.GetProperty("overlay_index").GetProperty("indexed_documents").GetInt32() >= 1);
            }

            // Empty PATH guarantees roscli-fast serves these commands directly on the hot daemon without CLI fallback!
            string emptyBin = Path.Combine(root, "empty-bin");
            Directory.CreateDirectory(emptyBin);
            Dictionary<string, string> noFallbackEnv = new()
            {
                ["PATH"] = emptyBin,
                ["ROSCLI_FULL_PATH"] = Path.Combine(emptyBin, "missing"),
            };

            // 1. roscli-fast ctx.search_text --syntax-filter code
            (int searchExit, string searchOut, string searchErr) = await RunProcessAsync(
                fastDll,
                root,
                new[] { "ctx.search_text", "--pattern", "Leaf", "--whole-word", "true", "--syntax-filter", "code" },
                daemonMode: "required",
                environmentOverrides: noFallbackEnv);
            Assert.True(searchExit == 0, $"roscli-fast ctx.search_text failed.\nOUT:\n{searchOut}\nERR:\n{searchErr}");
            using (JsonDocument searchDoc = JsonDocument.Parse(searchOut))
            {
                Assert.True(searchDoc.RootElement.GetProperty("Ok").GetBoolean(), searchOut);
                JsonElement data = searchDoc.RootElement.GetProperty("Data");
                Assert.Equal("workspace_overlay_index", data.GetProperty("analysis_scope").GetProperty("text_source").GetString());
                // Excludes the comment mentioning Leaf
                Assert.DoesNotContain("comment", data.GetProperty("matches").GetRawText(), StringComparison.OrdinalIgnoreCase);
                Assert.True(data.GetProperty("total_matches").GetInt32() >= 2);
            }

            // 2. roscli-fast ctx.read_window Lib/Service.cs 1 10
            (int readExit, string readOut, string readErr) = await RunProcessAsync(
                fastDll,
                root,
                new[] { "ctx.read_window", "Lib/Service.cs", "1", "10" },
                daemonMode: "required",
                environmentOverrides: noFallbackEnv);
            Assert.True(readExit == 0, $"roscli-fast ctx.read_window failed.\nOUT:\n{readOut}\nERR:\n{readErr}");
            using (JsonDocument readDoc = JsonDocument.Parse(readOut))
            {
                Assert.True(readDoc.RootElement.GetProperty("Ok").GetBoolean(), readOut);
                JsonElement data = readDoc.RootElement.GetProperty("Data");
                Assert.Equal("workspace_overlay_index", data.GetProperty("text_source").GetString());
                Assert.Equal(64, data.GetProperty("file_sha256").GetString()!.Length);
                Assert.Contains("interface IService", data.GetProperty("content").GetString());
            }

            // 3. roscli-fast ctx.symbol_context --symbol Lib.Service.Leaf
            (int ctxExit, string ctxOut, string ctxErr) = await RunProcessAsync(
                fastDll,
                root,
                new[] { "ctx.symbol_context", "--symbol", "Lib.Service.Leaf" },
                daemonMode: "required",
                environmentOverrides: noFallbackEnv);
            Assert.True(ctxExit == 0, $"roscli-fast ctx.symbol_context failed.\nOUT:\n{ctxOut}\nERR:\n{ctxErr}");
            using (JsonDocument ctxDoc = JsonDocument.Parse(ctxOut))
            {
                Assert.True(ctxDoc.RootElement.GetProperty("Ok").GetBoolean(), ctxOut);
                JsonElement data = ctxDoc.RootElement.GetProperty("Data");
                Assert.Equal("workspace_overlay_index", data.GetProperty("text_source").GetString());
                Assert.Contains("return 1;", data.GetProperty("definition_source").GetProperty("content").GetString());
                Assert.Contains("Middle", data.GetProperty("callers").GetRawText(), StringComparison.Ordinal);
            }

            // 4. roscli-fast nav.find_invocations --symbols Lib.Service.Leaf,Lib.Service.Middle
            (int batchExit, string batchOut, string batchErr) = await RunProcessAsync(
                fastDll,
                root,
                new[] { "nav.find_invocations", "--symbols", "Lib.Service.Leaf,Lib.Service.Middle", "--brief", "true" },
                daemonMode: "required",
                environmentOverrides: noFallbackEnv);
            Assert.True(batchExit == 0, $"roscli-fast nav.find_invocations --symbols failed.\nOUT:\n{batchOut}\nERR:\n{batchErr}");
            using (JsonDocument batchDoc = JsonDocument.Parse(batchOut))
            {
                Assert.True(batchDoc.RootElement.GetProperty("Ok").GetBoolean(), batchOut);
                JsonElement data = batchDoc.RootElement.GetProperty("Data");
                Assert.True(data.GetProperty("batched").GetBoolean());
                Assert.Equal(2, data.GetProperty("succeeded").GetInt32());
            }

            // 5. roscli-fast nav.call_path --source-symbol Lib.Service.Top --target-symbol Lib.Service.Leaf
            (int pathExit, string pathOut, string pathErr) = await RunProcessAsync(
                fastDll,
                root,
                new[] { "nav.call_path", "--source-symbol", "Lib.Service.Top", "--target-symbol", "Lib.Service.Leaf" },
                daemonMode: "required",
                environmentOverrides: noFallbackEnv);
            Assert.True(pathExit == 0, $"roscli-fast nav.call_path failed.\nOUT:\n{pathOut}\nERR:\n{pathErr}");
            using (JsonDocument pathDoc = JsonDocument.Parse(pathOut))
            {
                Assert.True(pathDoc.RootElement.GetProperty("Ok").GetBoolean(), pathOut);
                Assert.True(pathDoc.RootElement.GetProperty("Data").GetProperty("path_found").GetBoolean());
            }
        }
        finally
        {
            try { _ = await RunProcessAsync(cliDll, root, new[] { "daemon.stop" }, daemonMode: "auto"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task McpServer_Pass5_DescriptorFullSchemas_Slim12Tools_SearchContextAndSha256Tx()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string cliDll = typeof(CliApplication).Assembly.Location;
        string mcpDll = ResolveSiblingDll("RoslynSkills.McpServer", "RoslynSkills.McpServer.dll");

        try
        {
            await CreateSampleSolutionAsync(root);

            // 1. Full mode exposes all 61 tools from CommandInputSchemaCatalog (0 parameterized schema-less tools).
            using (Process fullMcp = StartMcpProcess(mcpDll, root, "--mode", "full"))
            {
                try
                {
                    using (await SendMcpAsync(fullMcp, 1, "initialize", new { protocolVersion = "2025-06-18" })) { }
                    (JsonDocument fullList, _) = await SendMcpWithSizeAsync(fullMcp, 2, "tools/list", new { });
                    using (fullList)
                    {
                        JsonElement tools = fullList.RootElement.GetProperty("result").GetProperty("tools");
                        Assert.True(tools.GetArrayLength() >= 61, $"Expected >= 61 tools in full mode, got {tools.GetArrayLength()}");

                        HashSet<string> zeroArgTools = new(StringComparer.OrdinalIgnoreCase)
                        {
                            "roslyn_system_ping",
                            "roslyn_workspace_list",
                        };

                        foreach (JsonElement tool in tools.EnumerateArray())
                        {
                            string name = tool.GetProperty("name").GetString()!;
                            if (zeroArgTools.Contains(name))
                            {
                                continue;
                            }

                            JsonElement schema = tool.GetProperty("inputSchema");
                            Assert.True(
                                schema.TryGetProperty("properties", out JsonElement props) &&
                                props.ValueKind == JsonValueKind.Object &&
                                props.EnumerateObject().Any(),
                                $"Full-mode tool '{name}' must expose descriptor-driven inputSchema.properties.");
                        }
                    }
                }
                finally
                {
                    TryKill(fullMcp);
                }
            }

            // 2. Slim mode: 12 tools within <= 6 KB, auto-binds on roslyn_context, serves roslyn_search & SHA-256 tx.
            using Process slimMcp = StartMcpProcess(mcpDll, root, "--mode", "slim");
            try
            {
                using (await SendMcpAsync(slimMcp, 1, "initialize", new { protocolVersion = "2025-06-18" })) { }
                (JsonDocument listResp, int listBytes) = await SendMcpWithSizeAsync(slimMcp, 2, "tools/list", new { });
                using (listResp)
                {
                    JsonElement tools = listResp.RootElement.GetProperty("result").GetProperty("tools");
                    Assert.Equal(12, tools.GetArrayLength());
                    Assert.True(listBytes <= 6144, $"Slim tools/list must be <= 6144 bytes, got {listBytes} bytes.");
                }

                // First call: roslyn_context auto-binds Demo.slnx and returns symbol mindspace + SHA-256!
                string serviceSha;
                using (JsonDocument ctxResp = await SendMcpAsync(slimMcp, 3, "tools/call", new
                {
                    name = "roslyn_context",
                    arguments = new { symbol = "Lib.Service.Leaf" },
                }, timeoutSeconds: 180))
                {
                    JsonElement structured = ctxResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), ctxResp.RootElement.GetRawText());
                    Assert.Equal("ctx.symbol_context", structured.GetProperty("commandId").GetString());
                    JsonElement data = structured.GetProperty("data");
                    Assert.Equal("workspace_overlay_index", data.GetProperty("text_source").GetString());
                    serviceSha = data.GetProperty("symbol").GetProperty("file_sha256").GetString()!;
                    Assert.Equal(64, serviceSha.Length);
                    Assert.Contains("Middle", data.GetProperty("callers").GetRawText(), StringComparison.Ordinal);
                }

                // Second call: roslyn_search uses warm overlay index with syntax_filter=declarations.
                using (JsonDocument searchResp = await SendMcpAsync(slimMcp, 4, "tools/call", new
                {
                    name = "roslyn_search",
                    arguments = new { pattern = "Leaf", whole_word = true, syntax_filter = "declarations" },
                }))
                {
                    JsonElement structured = searchResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), searchResp.RootElement.GetRawText());
                    JsonElement data = structured.GetProperty("data");
                    Assert.Equal("workspace_overlay_index", data.GetProperty("analysis_scope").GetProperty("text_source").GetString());
                    Assert.True(data.GetProperty("total_matches").GetInt32() >= 1);
                }

                // Third call: roslyn_edit_rename by --symbol (dry-run).
                using (JsonDocument renameResp = await SendMcpAsync(slimMcp, 5, "tools/call", new
                {
                    name = "roslyn_edit_rename",
                    arguments = new { symbol = "Lib.Service.Leaf", new_name = "LeafV2", apply = false },
                }))
                {
                    JsonElement structured = renameResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), renameResp.RootElement.GetRawText());
                }

                // Fourth call: roslyn_edit_transaction with wrong expected_sha256 fails closed.
                using (JsonDocument badTxResp = await SendMcpAsync(slimMcp, 6, "tools/call", new
                {
                    name = "roslyn_edit_transaction",
                    arguments = new
                    {
                        apply = true,
                        file_path = "Lib/Service.cs",
                        expected_sha256 = new string('f', 64),
                        operations = new object[]
                        {
                            new { kind = "replace_text", old_text = "return 1;", new_text = "return 9;" },
                        },
                    },
                }))
                {
                    JsonElement structured = badTxResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.False(structured.GetProperty("ok").GetBoolean());
                    Assert.Contains("content_hash_mismatch", structured.GetProperty("errors").GetRawText(), StringComparison.Ordinal);
                }

                // Fifth call: roslyn_edit_transaction with exact SHA-256 from roslyn_context succeeds!
                using (JsonDocument okTxResp = await SendMcpAsync(slimMcp, 7, "tools/call", new
                {
                    name = "roslyn_edit_transaction",
                    arguments = new
                    {
                        apply = true,
                        file_path = "Lib/Service.cs",
                        expected_sha256 = serviceSha,
                        operations = new object[]
                        {
                            new { kind = "replace_text", old_text = "return 1;", new_text = "return 42;" },
                        },
                    },
                }))
                {
                    JsonElement structured = okTxResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), okTxResp.RootElement.GetRawText());
                    Assert.Equal(1, structured.GetProperty("data").GetProperty("wrote_file_count").GetInt32());
                }

                // Sixth call: roslyn_run ctx.read_window observes updated content from warm overlay index!
                using (JsonDocument readResp = await SendMcpAsync(slimMcp, 8, "tools/call", new
                {
                    name = "roslyn_run",
                    arguments = new
                    {
                        command_id = "ctx.read_window",
                        input = new { file_path = "Lib/Service.cs", start_line = 12, end_line = 20 },
                    },
                }))
                {
                    JsonElement structured = readResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), readResp.RootElement.GetRawText());
                    JsonElement data = structured.GetProperty("data");
                    Assert.Equal("workspace_overlay_index", data.GetProperty("text_source").GetString());
                    Assert.Contains("return 42;", data.GetProperty("content").GetString());
                }
            }
            finally
            {
                TryKill(slimMcp);
            }
        }
        finally
        {
            try { _ = await RunProcessAsync(cliDll, root, new[] { "daemon.stop" }, daemonMode: "auto"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task McpServer_LargeUtf8Request_AndConcurrentPipelinedReadOnlyCalls()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-mcp-perf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string mcpDll = ResolveSiblingDll("RoslynSkills.McpServer", "RoslynSkills.McpServer.dll");

        try
        {
            string testFile = Path.Combine(root, "Utf8Sample.cs");
            await File.WriteAllTextAsync(testFile, "// §1 äöü €100\npublic sealed class Utf8Sample { public int Value => 42; }\n", new UTF8Encoding(false));

            using Process mcp = StartMcpProcess(mcpDll, root, "--mode", "slim");
            try
            {
                using (await SendMcpAsync(mcp, 1, "initialize", new { protocolVersion = "2025-06-18" })) { }

                // 1. > 10 KB newline-delimited UTF-8 request with non-ASCII characters (previously broke 8 KB cap & ASCII decoding).
                string largeNonAsciiPadding = new string('ä', 4500) + "§€" + new string('x', 4500);
                using (JsonDocument largeResp = await SendMcpAsync(mcp, 2, "tools/call", new
                {
                    name = "roslyn_edit_transaction",
                    arguments = new
                    {
                        apply = false,
                        file_path = testFile,
                        operations = new object[]
                        {
                            new
                            {
                                kind = "replace_text",
                                old_text = "// §1 äöü €100",
                                new_text = $"// §2 {largeNonAsciiPadding}",
                            },
                        },
                    },
                }))
                {
                    JsonElement structured = largeResp.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), largeResp.RootElement.GetRawText());
                    Assert.Equal(1, structured.GetProperty("data").GetProperty("succeeded_operations").GetInt32());
                }

                // 2. Pipeline 4 read-only calls back-to-back without waiting between writes.
                int[] ids = [10, 11, 12, 13];
                foreach (int id in ids)
                {
                    string req = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id,
                        method = "tools/call",
                        @params = new
                        {
                            name = "roslyn_run",
                            arguments = new
                            {
                                command_id = "ctx.read_window",
                                input = new { file_path = testFile, start_line = 1, end_line = 2 },
                            },
                        },
                    });
                    await mcp.StandardInput.WriteLineAsync(req);
                }

                await mcp.StandardInput.FlushAsync();

                HashSet<int> receivedIds = new();
                for (int i = 0; i < ids.Length; i++)
                {
                    string? line = await mcp.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.False(string.IsNullOrWhiteSpace(line));
                    using JsonDocument doc = JsonDocument.Parse(line!);
                    int respId = doc.RootElement.GetProperty("id").GetInt32();
                    receivedIds.Add(respId);
                    JsonElement structured = doc.RootElement.GetProperty("result").GetProperty("structuredContent");
                    Assert.True(structured.GetProperty("ok").GetBoolean(), line);
                    Assert.Contains("§1 äöü €100", structured.GetProperty("data").GetProperty("content").GetString());
                }

                Assert.Equal(ids.ToHashSet(), receivedIds);
            }
            finally
            {
                TryKill(mcp);
            }
        }
        finally
        {
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

            // Comment mentioning Leaf for syntax_filter verification
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
