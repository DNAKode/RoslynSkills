using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.WorkspaceHost;

namespace RoslynSkills.Core.Tests;

/// <summary>
/// End-to-end verification of hot-workspace edit commands on a 3-project solution driven through the real
/// WorkspaceHost process: cross-project rename with XML doc crefs, signature changes with default values and
/// call-site rewrites, CRLF/LF + BOM preservation across exact/transaction edits, and created files joining
/// the warm solution immediately.
/// </summary>
public sealed class HotWorkspaceEditTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private const string ServiceSourceLf =
        "using System;\n" +
        "using System.Collections.Generic;\n" +
        "\n" +
        "namespace Lib;\n" +
        "\n" +
        "public sealed class Service\n" +
        "{\n" +
        "    /// <summary>Calls <see cref=\"BeginWork\"/> and <see cref=\"FormatPair(string, int, bool)\"/>.</summary>\n" +
        "    public int BeginWork(int x)\n" +
        "    {\n" +
        "        // BeginWork plain comment must stay untouched\n" +
        "        string label = \"BeginWork\";\n" +
        "        List<int> items = new() { x, label.Length };\n" +
        "        return items[0];\n" +
        "    }\n" +
        "\n" +
        "    public List<string> CompileWork(int count, string? tag = null)\n" +
        "    {\n" +
        "        List<string> result = new();\n" +
        "        for (int i = 0; i < count; i++)\n" +
        "        {\n" +
        "            result.Add(tag ?? i.ToString());\n" +
        "        }\n" +
        "        return result;\n" +
        "    }\n" +
        "\n" +
        "    public string FormatPair(string left, int right, bool unused)\n" +
        "    {\n" +
        "        return $\"{left}:{right}\";\n" +
        "    }\n" +
        "}\n";

    private const string AppSourceCrLf =
        "using System;\r\n" +
        "using System.Collections.Generic;\r\n" +
        "using Lib;\r\n" +
        "\r\n" +
        "namespace App;\r\n" +
        "\r\n" +
        "public sealed class AppRunner : IDisposable\r\n" +
        "{\r\n" +
        "    /// <summary>Delegates to <see cref=\"Service.BeginWork\"/>.</summary>\r\n" +
        "    public int Run()\r\n" +
        "    {\r\n" +
        "        // BeginWork plain comment in App\r\n" +
        "        string tag = \"BeginWork\";\r\n" +
        "        Service service = new();\r\n" +
        "        List<string> compiled = service.CompileWork(2, tag);\r\n" +
        "        string formatted = service.FormatPair(\"app\", compiled.Count, true);\r\n" +
        "        return service.BeginWork(formatted.Length);\r\n" +
        "    }\r\n" +
        "\r\n" +
        "    public void Dispose() { }\r\n" +
        "}\r\n";

    private const string ConsumerSourceLf =
        "using System;\n" +
        "using System.Collections.Generic;\n" +
        "using Lib;\n" +
        "\n" +
        "namespace Consumer;\n" +
        "\n" +
        "public sealed class Caller : IDisposable\n" +
        "{\n" +
        "    public int Invoke()\n" +
        "    {\n" +
        "        Service service = new();\n" +
        "        List<string> items = service.CompileWork(1);\n" +
        "        string text = service.FormatPair(\"consumer\", items.Count, false);\n" +
        "        return service.BeginWork(text.Length);\n" +
        "    }\n" +
        "\n" +
        "    public void Dispose() { }\n" +
        "}\n";

    [Fact]
    public void EditCommands_DeclareHotWorkspaceRouting()
    {
        IReadOnlyList<CommandDescriptor> commands = DefaultRegistryFactory.Create().ListCommands();
        HashSet<string> hot = commands
            .Where(c => c.RunsOnHotWorkspace)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        string[] expectedHotEdits =
        {
            "edit.rename_symbol",
            "edit.change_signature",
            "edit.batch_exact",
            "edit.transaction",
            "edit.create_file",
            "edit.replace_in_member",
        };
        foreach (string id in expectedHotEdits)
        {
            Assert.Contains(id, hot);
        }
    }

    [Fact]
    public async Task HotHost_ThreeProjectEdits_RenameChangeSignatureLineEndingsAndCreateFile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-hot-edits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using Process process = StartHostProcess();

        try
        {
            (string solutionPath, string servicePath, string appPath, string consumerPath) = await CreateThreeProjectSolutionAsync(root);
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

            // -----------------------------------------------------------------
            // 1. edit.rename_symbol across all 3 projects + <see cref>
            // -----------------------------------------------------------------
            JsonElement renameResponse = await host.CallAsync("edit.rename_symbol", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 9,
                column = 16,
                new_name = "BeginWorkScope",
                apply = true,
                require_workspace = true,
            });
            JsonElement renameData = DataOf(renameResponse);
            Assert.Equal("solution", renameData.GetProperty("rename_scope").GetString());
            Assert.Equal(3, renameData.GetProperty("changed_file_count").GetInt32());
            Assert.Equal(3, renameData.GetProperty("wrote_file_count").GetInt32());
            Assert.Equal(5, renameData.GetProperty("replacement_count").GetInt32());
            Assert.Equal(0, renameData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());

            string serviceAfterRename = await File.ReadAllTextAsync(servicePath);
            string appAfterRename = await File.ReadAllTextAsync(appPath);
            string consumerAfterRename = await File.ReadAllTextAsync(consumerPath);

            Assert.Contains("public int BeginWorkScope(int x)", serviceAfterRename);
            Assert.Contains("<see cref=\"BeginWorkScope\"/>", serviceAfterRename);
            Assert.Contains("// BeginWork plain comment must stay untouched", serviceAfterRename);
            Assert.Contains("string label = \"BeginWork\";", serviceAfterRename);

            Assert.Contains("service.BeginWorkScope(", appAfterRename);
            Assert.Contains("<see cref=\"Service.BeginWorkScope\"/>", appAfterRename);
            Assert.Contains("// BeginWork plain comment in App", appAfterRename);
            Assert.Contains("string tag = \"BeginWork\";", appAfterRename);

            Assert.Contains("service.BeginWorkScope(", consumerAfterRename);
            Assert.DoesNotContain("BeginWork(", serviceAfterRename);
            Assert.DoesNotContain("BeginWork(", appAfterRename);
            Assert.DoesNotContain("BeginWork(", consumerAfterRename);

            // Preserve LF / CRLF and UTF-8 BOM.
            AssertPureLfNoBom(servicePath);
            AssertPureCrLfWithBom(appPath);
            AssertPureLfNoBom(consumerPath);

            // Hot workspace immediately reflects the rename across all 3 projects.
            JsonElement refsAfterRename = await host.CallAsync("nav.find_references", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 9,
                column = 16,
                require_workspace = true,
            });
            JsonElement refsData = DataOf(refsAfterRename);
            Assert.Equal("solution", refsData.GetProperty("search_scope").GetString());
            string[] refFileNames = refsData.GetProperty("matches").EnumerateArray()
                .Select(m => Path.GetFileName(m.GetProperty("file_path").GetString()!))
                .ToArray();
            Assert.Contains("Service.cs", refFileNames);
            Assert.Contains("AppRunner.cs", refFileNames);
            Assert.Contains("Caller.cs", refFileNames);

            // -----------------------------------------------------------------
            // 2a. edit.change_signature adding a parameter with a default value
            // -----------------------------------------------------------------
            JsonElement addDefaultResponse = await host.CallAsync("edit.change_signature", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 17,
                column = 25,
                parameters = "int count, string? tag = null, bool trace = false",
                apply = true,
                require_workspace = true,
            });
            JsonElement addDefaultData = DataOf(addDefaultResponse);
            Assert.Equal("solution", addDefaultData.GetProperty("edit_scope").GetString());
            Assert.Equal(0, addDefaultData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());
            Assert.True(addDefaultData.GetProperty("wrote_file").GetBoolean());

            string serviceAfterDefaultParam = await File.ReadAllTextAsync(servicePath);
            Assert.Contains("public List<string> CompileWork(int count, string? tag = null, bool trace = false)\n    {\n", serviceAfterDefaultParam);
            Assert.DoesNotContain("bool trace = false)    {", serviceAfterDefaultParam);
            AssertPureLfNoBom(servicePath);

            // -----------------------------------------------------------------
            // 2b. edit.change_signature renaming, reordering, and removing a parameter across projects
            // -----------------------------------------------------------------
            JsonElement reorderResponse = await host.CallAsync("edit.change_signature", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 27,
                column = 19,
                new_name = "FormatSwapped",
                parameters = "int right, string left",
                apply = true,
                require_workspace = true,
            });
            JsonElement reorderData = DataOf(reorderResponse);
            Assert.Equal("solution", reorderData.GetProperty("edit_scope").GetString());
            Assert.Equal(3, reorderData.GetProperty("changed_file_count").GetInt32());
            Assert.Equal(2, reorderData.GetProperty("updated_call_site_count").GetInt32());
            Assert.Equal(0, reorderData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());

            string serviceAfterReorder = await File.ReadAllTextAsync(servicePath);
            string appAfterReorder = await File.ReadAllTextAsync(appPath);
            string consumerAfterReorder = await File.ReadAllTextAsync(consumerPath);
            Assert.Contains("public string FormatSwapped(int right, string left)\n    {\n", serviceAfterReorder);
            Assert.Contains("<see cref=\"FormatSwapped(int, string)\"/>", serviceAfterReorder);
            Assert.Contains("service.FormatSwapped(compiled.Count, \"app\")", appAfterReorder);
            Assert.Contains("service.FormatSwapped(items.Count, \"consumer\")", consumerAfterReorder);
            AssertPureLfNoBom(servicePath);
            AssertPureCrLfWithBom(appPath);
            AssertPureLfNoBom(consumerPath);

            // -----------------------------------------------------------------
            // 3a. edit.batch_exact (thin alias of edit.transaction) normalizes CRLF -> LF on LF files and LF -> CRLF on CRLF files
            // -----------------------------------------------------------------
            JsonElement batchResponse = await host.CallAsync("edit.batch_exact", new
            {
                workspace_handle = handle,
                apply = true,
                operations = new object[]
                {
                    new
                    {
                        file_path = servicePath,
                        kind = "replace_text",
                        old_text = "        return items[0];",
                        new_text = "        int first = items[0];\r\n        return first;",
                    },
                    new
                    {
                        file_path = appPath,
                        kind = "replace_text",
                        old_text = "        return service.BeginWorkScope(formatted.Length);",
                        new_text = "        int length = formatted.Length;\n        return service.BeginWorkScope(length);",
                    },
                },
            });
            JsonElement batchData = DataOf(batchResponse);
            Assert.Equal(2, batchData.GetProperty("wrote_file_count").GetInt32());
            Assert.Equal("process_hot", batchData.GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());
            Assert.Equal(0, batchData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());

            AssertPureLfNoBom(servicePath);
            AssertPureCrLfWithBom(appPath);

            // -----------------------------------------------------------------
            // 3b. edit.transaction runs on hot workspace with replace_text, set_content, and replace_span,
            //     normalizes line endings, and reports unified solution diagnostics
            // -----------------------------------------------------------------
            string consumerCurrent = await File.ReadAllTextAsync(consumerPath);
            string consumerWithCrLfPayload = consumerCurrent
                .Replace("return service.BeginWorkScope(text.Length);", "int n = text.Length;\r\n        return service.BeginWorkScope(n);")
                .Replace("\n", "\r\n");

            string[] appLines = (await File.ReadAllTextAsync(appPath)).Split("\r\n");
            int disposeLine = Array.FindIndex(appLines, l => l.Contains("public void Dispose()", StringComparison.Ordinal)) + 1;
            Assert.True(disposeLine > 0);

            JsonElement txResponse = await host.CallAsync("edit.transaction", new
            {
                workspace_handle = handle,
                apply = true,
                require_workspace = true,
                operations = new object[]
                {
                    new
                    {
                        file_path = consumerPath,
                        operation = "set_content",
                        new_content = consumerWithCrLfPayload,
                    },
                    new
                    {
                        file_path = appPath,
                        operation = "replace_span",
                        start_line = disposeLine,
                        start_column = 1,
                        end_line = disposeLine,
                        end_column = 30,
                        new_text = "    public void Dispose()\n    {\n    }",
                    },
                    new
                    {
                        file_path = servicePath,
                        operation = "replace_text",
                        old_text = "        int first = items[0];\r\n        return first;",
                        new_text = "        int head = items[0];\r\n        return head;",
                    },
                },
            });
            JsonElement txData = DataOf(txResponse);
            Assert.Equal(3, txData.GetProperty("wrote_file_count").GetInt32());
            Assert.Equal("process_hot", txData.GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());
            Assert.Equal(0, txData.GetProperty("diagnostics_after_edit").GetProperty("errors").GetInt32());
            AssertPureLfNoBom(servicePath);
            AssertPureLfNoBom(consumerPath);
            AssertPureCrLfWithBom(appPath);
            Assert.Contains("int head = items[0];", await File.ReadAllTextAsync(servicePath));

            // 3c. edit.transaction atomic rollback on ambiguous replace_text writes nothing
            JsonElement ambiguousTx = await host.CallAsync("edit.transaction", new
            {
                workspace_handle = handle,
                apply = true,
                atomic = true,
                operations = new object[]
                {
                    new
                    {
                        file_path = servicePath,
                        operation = "replace_text",
                        old_text = "int head = items[0];",
                        new_text = "int neverWritten = items[0];",
                    },
                    new
                    {
                        file_path = appPath,
                        operation = "replace_text",
                        old_text = "    ",
                        new_text = "  ",
                    },
                },
            });
            Assert.False(ambiguousTx.GetProperty("ok").GetBoolean());
            JsonElement ambiguousData = ambiguousTx.GetProperty("envelope").GetProperty("Data");
            Assert.True(ambiguousData.GetProperty("skipped_apply_due_to_errors").GetBoolean());
            Assert.Equal(0, ambiguousData.GetProperty("wrote_file_count").GetInt32());
            Assert.Contains("old_text_ambiguous", ambiguousTx.GetRawText());
            Assert.DoesNotContain("neverWritten", await File.ReadAllTextAsync(servicePath));

            // -----------------------------------------------------------------
            // 4. edit.create_file joins its project in the hot workspace and is seen by nav.find_symbol
            // -----------------------------------------------------------------
            string createdFilePath = Path.Combine(root, "Lib", "Sub", "FreshHelper.cs");
            const string createdContent =
                "using System.Collections.Generic;\n" +
                "\n" +
                "namespace Lib.Sub;\n" +
                "\n" +
                "public sealed class FreshlyCreatedService\n" +
                "{\n" +
                "    public List<string> Run(Service service) => service.CompileWork(1, trace: true);\n" +
                "}\n";

            JsonElement createResponse = await host.CallAsync("edit.create_file", new
            {
                workspace_handle = handle,
                file_path = createdFilePath,
                content = createdContent,
                apply = true,
                require_workspace = true,
            });
            JsonElement createData = DataOf(createResponse);
            Assert.True(createData.GetProperty("wrote_file").GetBoolean());
            Assert.Equal(1, createData.GetProperty("hot_workspace_refresh").GetProperty("refreshed_workspace_count").GetInt32());
            Assert.Equal("workspace_updated_source", createData.GetProperty("diagnostics_after_create").GetProperty("mode").GetString());
            Assert.Equal(0, createData.GetProperty("diagnostics_after_create").GetProperty("errors").GetInt32());

            // Next nav.find_symbol sees the created file directly in the warm workspace.
            JsonElement findCreatedResponse = await host.CallAsync("nav.find_symbol", new
            {
                workspace_handle = handle,
                file_path = createdFilePath,
                symbol_name = "FreshlyCreatedService",
                require_workspace = true,
                brief = true,
            });
            JsonElement findCreatedData = DataOf(findCreatedResponse);
            Assert.True(findCreatedData.GetProperty("total_matches").GetInt32() >= 1);
            Assert.Equal("process_hot", findCreatedData.GetProperty("query").GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());

            // Hot workspace status stays clean (does not require reload after joining the new file).
            JsonElement statusResponse = await host.SendAsync(new
            {
                id = "status-after-create",
                method = WorkspaceHostProtocol.Method.WorkspaceStatus,
                input = new { workspace_handle = handle },
            }, timeoutSeconds: 30);
            JsonElement statusData = DataOf(statusResponse);
            Assert.False(statusData.GetProperty("requires_reload").GetBoolean(), statusData.GetRawText());

            await process.StandardInput.WriteLineAsync("{\"id\":\"shutdown\",\"method\":\"shutdown\"}");
            _ = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after shutdown.");

            // Verify the entire modified 3-project solution actually builds with dotnet build!
            ProcessStartInfo buildStartInfo = new("dotnet", $"build \"{solutionPath}\" --nologo --disable-build-servers -p:UseSharedCompilation=false")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            buildStartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            buildStartInfo.Environment.Remove("MSBUILD_EXE_PATH");
            buildStartInfo.Environment.Remove("MSBuildSDKsPath");
            buildStartInfo.Environment.Remove("MSBuildExtensionsPath");
            using Process buildProcess = Process.Start(buildStartInfo)!;
            try
            {
                Task<string> buildStdOutTask = buildProcess.StandardOutput.ReadToEndAsync();
                Task<string> buildStdErrTask = buildProcess.StandardError.ReadToEndAsync();
                if (!buildProcess.WaitForExit(120_000))
                {
                    TryKillProcessTree(buildProcess);
                    Assert.Fail("dotnet build timed out after 120s.");
                }

                await Task.WhenAll(buildStdOutTask, buildStdErrTask).WaitAsync(TimeSpan.FromSeconds(10));
                string buildStdOut = await buildStdOutTask;
                string buildStdErr = await buildStdErrTask;
                Assert.True(buildProcess.ExitCode == 0, $"Expected 3-project fixture build to pass.\nSTDOUT:\n{buildStdOut}\nSTDERR:\n{buildStdErr}");
            }
            finally
            {
                TryKillProcessTree(buildProcess);
            }
        }
        finally
        {
            TryKillProcessTree(process);
            try { Directory.Delete(root, recursive: true); } catch { }
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

    private static async Task<(string Solution, string Service, string App, string Consumer)> CreateThreeProjectSolutionAsync(string root)
    {
        const string projectTemplate = """
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

        foreach (string project in new[] { "Lib", "App", "Consumer" })
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            await File.WriteAllTextAsync(
                Path.Combine(root, project, $"{project}.csproj"),
                string.Format(projectTemplate, project == "Lib" ? string.Empty : libReference),
                Utf8NoBom);
        }

        string servicePath = Path.Combine(root, "Lib", "Service.cs");
        string appPath = Path.Combine(root, "App", "AppRunner.cs");
        string consumerPath = Path.Combine(root, "Consumer", "Caller.cs");

        await File.WriteAllBytesAsync(servicePath, Utf8NoBom.GetBytes(ServiceSourceLf));
        byte[] appBom = Utf8WithBom.GetPreamble();
        byte[] appBody = Utf8WithBom.GetBytes(AppSourceCrLf);
        byte[] appBytes = new byte[appBom.Length + appBody.Length];
        Buffer.BlockCopy(appBom, 0, appBytes, 0, appBom.Length);
        Buffer.BlockCopy(appBody, 0, appBytes, appBom.Length, appBody.Length);
        await File.WriteAllBytesAsync(appPath, appBytes);
        await File.WriteAllBytesAsync(consumerPath, Utf8NoBom.GetBytes(ConsumerSourceLf));

        string solutionPath = Path.Combine(root, "ThreeProj.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="App/App.csproj" />
              <Project Path="Consumer/Consumer.csproj" />
            </Solution>
            """,
            Utf8NoBom);
        return (solutionPath, servicePath, appPath, consumerPath);
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
