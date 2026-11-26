using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.Core.Tests;

public sealed class Pass5CommandsTests
{
    [Fact]
    public void DescriptorInputSchemaCatalog_CoversEveryRegisteredCommand()
    {
        ICommandRegistry registry = DefaultRegistryFactory.Create();
        IReadOnlyList<CommandDescriptor> descriptors = registry.ListCommands();
        Assert.True(descriptors.Count >= 61, $"Expected >= 61 registered commands, got {descriptors.Count}");

        HashSet<string> zeroArgCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "system.ping",
            "workspace.list",
        };

        foreach (CommandDescriptor descriptor in descriptors)
        {
            Assert.NotNull(descriptor.InputSchema);
            if (!zeroArgCommands.Contains(descriptor.Id))
            {
                Assert.True(
                    descriptor.InputSchema!.Parameters.Count > 0,
                    $"Parameterized command '{descriptor.Id}' must declare descriptor input schema parameters.");
            }
        }
    }

    [Fact]
    public async Task WorkspaceOverlayIndex_WarmupWait_TrigramPruning_AndIncrementalSync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-overlay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            (string slnxPath, string alphaPath, string betaPath) = await CreateTwoFileSolutionAsync(root);

            WorkspacePreloadCommand preload = new();
            CommandExecutionResult preloadResult = await preload.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_path = slnxPath,
                    require_solution = true,
                    wait_warmup = true,
                    wait_warmup_timeout_ms = 60_000,
                }),
                CancellationToken.None);
            Assert.True(preloadResult.Ok, JsonSerializer.Serialize(preloadResult.Errors));

            JsonElement preloadData = JsonSerializer.SerializeToElement(preloadResult.Data);
            workspaceHandle = preloadData.GetProperty("workspace_handle").GetString()!;
            Assert.Equal("complete", preloadData.GetProperty("warmup_state").GetString());
            Assert.False(preloadData.GetProperty("warmup_timed_out").GetBoolean());
            Assert.True(preloadData.GetProperty("projects_warmed").GetInt32() >= 1);

            JsonElement overlayStats = preloadData.GetProperty("overlay_index");
            Assert.True(overlayStats.GetProperty("indexed_documents").GetInt32() >= 2);
            int fullBuildsBefore = overlayStats.GetProperty("full_rebuilds").GetInt32();
            Assert.True(fullBuildsBefore >= 1);

            // Search for a literal present only in Beta.cs: overlay index prunes Alpha.cs!
            SearchTextCommand search = new();
            CommandExecutionResult searchBefore = await search.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    workspace_path = slnxPath,
                    pattern = "UniqueBetaSentinel",
                    brief = false,
                }),
                CancellationToken.None);
            Assert.True(searchBefore.Ok, JsonSerializer.Serialize(searchBefore.Errors));
            JsonElement beforeData = JsonSerializer.SerializeToElement(searchBefore.Data);
            Assert.Equal("workspace_overlay_index", beforeData.GetProperty("analysis_scope").GetProperty("text_source").GetString());
            Assert.Equal("overlay_index", beforeData.GetProperty("analysis_scope").GetProperty("analysis_mode").GetString());
            Assert.Equal(1, beforeData.GetProperty("files_scanned").GetInt32());
            Assert.True(beforeData.GetProperty("analysis_scope").GetProperty("index_pruned_files").GetInt32() >= 1);
            Assert.Equal(1, beforeData.GetProperty("total_matches").GetInt32());

            // Edit Beta.cs via edit.transaction -> incremental overlay update (no full rebuild).
            EditTransactionCommand tx = new();
            CommandExecutionResult editRes = await tx.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    apply = true,
                    operations = new object[]
                    {
                        new
                        {
                            kind = "replace_text",
                            file_path = betaPath,
                            old_text = "UniqueBetaSentinel",
                            new_text = "ReplacedGammaSentinel",
                        },
                    },
                }),
                CancellationToken.None);
            Assert.True(editRes.Ok, JsonSerializer.Serialize(editRes.Errors));

            WorkspaceStatusCommand statusCmd = new();
            CommandExecutionResult statusRes = await statusCmd.ExecuteAsync(
                JsonSerializer.SerializeToElement(new { workspace_handle = workspaceHandle }),
                CancellationToken.None);
            Assert.True(statusRes.Ok);
            JsonElement statusData = JsonSerializer.SerializeToElement(statusRes.Data);
            JsonElement afterOverlay = statusData.GetProperty("overlay_index");
            Assert.Equal(fullBuildsBefore, afterOverlay.GetProperty("full_rebuilds").GetInt32());
            Assert.True(afterOverlay.GetProperty("incremental_updates").GetInt32() >= 1);

            // Old sentinel is gone from postings; new sentinel is immediately indexed.
            CommandExecutionResult oldSearch = await search.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    workspace_path = slnxPath,
                    pattern = "UniqueBetaSentinel",
                }),
                CancellationToken.None);
            Assert.True(oldSearch.Ok);
            Assert.Equal(0, JsonSerializer.SerializeToElement(oldSearch.Data).GetProperty("total_matches").GetInt32());

            CommandExecutionResult newSearch = await search.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    workspace_path = slnxPath,
                    pattern = "ReplacedGammaSentinel",
                }),
                CancellationToken.None);
            Assert.True(newSearch.Ok);
            JsonElement newData = JsonSerializer.SerializeToElement(newSearch.Data);
            Assert.Equal("workspace_overlay_index", newData.GetProperty("analysis_scope").GetProperty("text_source").GetString());
            Assert.Equal(1, newData.GetProperty("total_matches").GetInt32());
        }
        finally
        {
            await CloseWorkspaceQuietlyAsync(workspaceHandle);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SearchTextCommand_SyntaxFilters_WholeWord_EnclosingSymbol_AndBatchedQueries()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-syntaxsearch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            string projPath = Path.Combine(root, "Lib", "Lib.csproj");
            string filePath = Path.Combine(root, "Lib", "SyntaxSample.cs");
            string slnxPath = Path.Combine(root, "Syntax.slnx");
            await File.WriteAllTextAsync(projPath, SampleCsproj);
            await File.WriteAllTextAsync(
                filePath,
                """
                namespace Lib;

                // Execute comment mention should be excluded by syntax_filter=code
                public sealed class SyntaxSample
                {
                    private const string Label = "Execute string literal mention";

                    public int Execute(int x)
                    {
                        return x + 1;
                    }

                    public int ExecuteEx(int x) => Execute(x) + 2;
                }
                """);
            await File.WriteAllTextAsync(slnxPath, SampleSlnx);

            workspaceHandle = await PreloadWorkspaceAsync(slnxPath);
            SearchTextCommand search = new();

            // 1. syntax_filter=any + whole_word=true matches comment, string, declaration, and invocation (excludes ExecuteEx).
            JsonElement anyData = await ExecuteSearchAsync(search, new
            {
                workspace_handle = workspaceHandle,
                workspace_path = slnxPath,
                pattern = "Execute",
                whole_word = true,
                syntax_filter = "any",
                include_syntax_context = true,
                brief = true,
            });
            Assert.Equal("workspace_overlay_index", anyData.GetProperty("analysis_scope").GetProperty("text_source").GetString());
            Assert.Equal(4, anyData.GetProperty("total_matches").GetInt32());

            // 2. syntax_filter=code excludes comment and string literal (only decl + call = 2 matches).
            JsonElement codeData = await ExecuteSearchAsync(search, new
            {
                workspace_handle = workspaceHandle,
                workspace_path = slnxPath,
                pattern = "Execute",
                whole_word = true,
                syntax_filter = "code",
                brief = true,
            });
            Assert.Equal(2, codeData.GetProperty("total_matches").GetInt32());

            // 3. syntax_filter=comments finds only the comment.
            JsonElement commentData = await ExecuteSearchAsync(search, new
            {
                workspace_handle = workspaceHandle,
                workspace_path = slnxPath,
                pattern = "Execute",
                whole_word = true,
                syntax_filter = "comments",
                brief = true,
            });
            Assert.Equal(1, commentData.GetProperty("total_matches").GetInt32());
            Assert.Equal("comment", commentData.GetProperty("matches")[0].GetProperty("syntax_context").GetString());

            // 4. syntax_filter=strings finds only the string literal.
            JsonElement stringData = await ExecuteSearchAsync(search, new
            {
                workspace_handle = workspaceHandle,
                workspace_path = slnxPath,
                pattern = "Execute",
                whole_word = true,
                syntax_filter = "strings",
                brief = true,
            });
            Assert.Equal(1, stringData.GetProperty("total_matches").GetInt32());
            Assert.Equal("string", stringData.GetProperty("matches")[0].GetProperty("syntax_context").GetString());

            // 5. syntax_filter=declarations finds only the Execute method declaration.
            JsonElement declData = await ExecuteSearchAsync(search, new
            {
                workspace_handle = workspaceHandle,
                workspace_path = slnxPath,
                pattern = "Execute",
                whole_word = true,
                syntax_filter = "declarations",
                brief = true,
            });
            Assert.Equal(1, declData.GetProperty("total_matches").GetInt32());
            Assert.Equal("declaration", declData.GetProperty("matches")[0].GetProperty("syntax_context").GetString());
            Assert.Contains("Execute", declData.GetProperty("matches")[0].GetProperty("enclosing_symbol").GetString());

            // 6. syntax_filter=invocations finds only the Execute(x) call inside ExecuteEx.
            JsonElement invData = await ExecuteSearchAsync(search, new
            {
                workspace_handle = workspaceHandle,
                workspace_path = slnxPath,
                pattern = "Execute",
                whole_word = true,
                syntax_filter = "invocations",
                brief = true,
            });
            Assert.Equal(1, invData.GetProperty("total_matches").GetInt32());
            Assert.Equal("invocation", invData.GetProperty("matches")[0].GetProperty("syntax_context").GetString());
            Assert.Contains("ExecuteEx", invData.GetProperty("matches")[0].GetProperty("enclosing_symbol").GetString());

            // 7. Batched queries in a single call.
            JsonElement batchData = await ExecuteSearchAsync(search, new
            {
                workspace_handle = workspaceHandle,
                workspace_path = slnxPath,
                whole_word = true,
                queries = new object[]
                {
                    new { label = "decls", pattern = "Execute", syntax_filter = "declarations" },
                    new { label = "calls", pattern = "Execute", syntax_filter = "invocations" },
                    new { label = "strs", pattern = "Execute", syntax_filter = "strings" },
                },
            });
            Assert.True(batchData.GetProperty("batched").GetBoolean());
            Assert.Equal(3, batchData.GetProperty("query_count").GetInt32());
            Assert.Equal(3, batchData.GetProperty("total_matches").GetInt32());
            Assert.Equal("workspace_overlay_index", batchData.GetProperty("text_source").GetString());
        }
        finally
        {
            await CloseWorkspaceQuietlyAsync(workspaceHandle);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadWindowCommand_ZeroDiskOverlay_SingleAndBatched_MatchesDiskSha256_BomAndNoBom()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-readwin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            string projPath = Path.Combine(root, "Lib", "Lib.csproj");
            string noBomPath = Path.Combine(root, "Lib", "NoBom.cs");
            string withBomPath = Path.Combine(root, "Lib", "WithBom.cs");
            string slnxPath = Path.Combine(root, "ReadWin.slnx");

            await File.WriteAllTextAsync(projPath, SampleCsproj);
            await File.WriteAllTextAsync(slnxPath, SampleSlnx);

            string noBomContent = """
                namespace Lib;

                public sealed class NoBom
                {
                    public int Compute(int v)
                    {
                        int doubled = v * 2;
                        return doubled + 1;
                    }
                }
                """;
            string withBomContent = """
                namespace Lib;

                public sealed class WithBom
                {
                    public string Greet(string name) => $"Hi {name}";
                }
                """;
            await File.WriteAllTextAsync(noBomPath, noBomContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllTextAsync(withBomPath, withBomContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            string expectedNoBomSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(noBomPath))).ToLowerInvariant();
            string expectedWithBomSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(withBomPath))).ToLowerInvariant();

            workspaceHandle = await PreloadWorkspaceAsync(slnxPath);
            ReadWindowCommand readWindow = new();

            // Single window by anchor_line
            CommandExecutionResult singleRes = await readWindow.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    file_path = noBomPath,
                    anchor_line = 7,
                    context_before = 2,
                    context_after = 2,
                }),
                CancellationToken.None);
            Assert.True(singleRes.Ok, JsonSerializer.Serialize(singleRes.Errors));
            JsonElement singleData = JsonSerializer.SerializeToElement(singleRes.Data);
            Assert.Equal("workspace_overlay_index", singleData.GetProperty("text_source").GetString());
            Assert.Equal(expectedNoBomSha, singleData.GetProperty("file_sha256").GetString());
            Assert.Equal(5, singleData.GetProperty("start_line").GetInt32());
            Assert.Equal(9, singleData.GetProperty("end_line").GetInt32());
            Assert.Contains("Compute", singleData.GetProperty("enclosing_symbol").GetString());
            Assert.Contains("int doubled = v * 2;", singleData.GetProperty("content").GetString());

            // Batched multi-file windows (both no-BOM and UTF-8 BOM match raw disk SHA-256 with zero disk reads!)
            CommandExecutionResult batchRes = await readWindow.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    windows = new object[]
                    {
                        new { label = "nobom", file_path = noBomPath, start_line = 1, end_line = 4 },
                        new { label = "bom", file_path = withBomPath, start_line = 1, end_line = 6 },
                    },
                }),
                CancellationToken.None);
            Assert.True(batchRes.Ok, JsonSerializer.Serialize(batchRes.Errors));
            JsonElement batchData = JsonSerializer.SerializeToElement(batchRes.Data);
            Assert.Equal("workspace_overlay_index", batchData.GetProperty("text_source").GetString());
            Assert.Equal(2, batchData.GetProperty("overlay_hits").GetInt32());
            Assert.Equal(0, batchData.GetProperty("disk_reads").GetInt32());

            JsonElement windows = batchData.GetProperty("windows");
            Assert.Equal(expectedNoBomSha, windows[0].GetProperty("file_sha256").GetString());
            Assert.Equal(expectedWithBomSha, windows[1].GetProperty("file_sha256").GetString());
        }
        finally
        {
            await CloseWorkspaceQuietlyAsync(workspaceHandle);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SymbolContextCommand_Mindspace_ReturnsDefRefsInterfaceCallersCalleesImplsAndDiag()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-mindspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            string projPath = Path.Combine(root, "Lib", "Lib.csproj");
            string filePath = Path.Combine(root, "Lib", "Processor.cs");
            string callerPath = Path.Combine(root, "Lib", "Runner.cs");
            string slnxPath = Path.Combine(root, "Mindspace.slnx");

            await File.WriteAllTextAsync(projPath, SampleCsproj);
            await File.WriteAllTextAsync(slnxPath, SampleSlnx);
            await File.WriteAllTextAsync(
                filePath,
                """
                namespace Lib;

                public interface IProcessor
                {
                    int Run(int value);
                }

                public sealed class FastProcessor : IProcessor
                {
                    /// <summary>Processes an integer using the fast path.</summary>
                    public int Run(int value)
                    {
                        int unusedWarn = 0;
                        return Normalize(value) + 1;
                    }

                    private int Normalize(int value) => value < 0 ? 0 : value;
                }
                """);
            await File.WriteAllTextAsync(
                callerPath,
                """
                namespace Lib;

                public sealed class Runner
                {
                    public int Dispatch(IProcessor processor, int v)
                    {
                        return processor.Run(v);
                    }
                }
                """);

            workspaceHandle = await PreloadWorkspaceAsync(slnxPath);
            SymbolContextCommand cmd = new();

            // 1. Method mindspace: definition + XML doc + SHA-256 + interface-aware caller + callee + file warning diagnostic
            CommandExecutionResult methodRes = await cmd.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    symbol = "Lib.FastProcessor.Run",
                }),
                CancellationToken.None);
            Assert.True(methodRes.Ok, JsonSerializer.Serialize(methodRes.Errors));
            JsonElement data = JsonSerializer.SerializeToElement(methodRes.Data);
            Assert.Equal("workspace_overlay_index", data.GetProperty("text_source").GetString());

            JsonElement sym = data.GetProperty("symbol");
            Assert.Contains("Processes an integer using the fast path.", sym.GetProperty("xml_summary").GetString());
            Assert.Equal(64, sym.GetProperty("file_sha256").GetString()!.Length);
            Assert.Contains("Normalize(value)", data.GetProperty("definition_source").GetProperty("content").GetString());

            // Callers include Runner.Dispatch calling through IProcessor.Run (interface-aware!)
            string callersRaw = data.GetProperty("callers").GetRawText();
            Assert.Contains("Dispatch", callersRaw, StringComparison.Ordinal);
            Assert.Contains("IProcessor.Run", callersRaw, StringComparison.Ordinal);

            // Callees include Normalize
            string calleesRaw = data.GetProperty("callees").GetRawText();
            Assert.Contains("Normalize", calleesRaw, StringComparison.Ordinal);

            // Declaring file diagnostics capture CS0219 unused local warning
            Assert.True(data.GetProperty("diagnostics").GetProperty("warnings").GetInt32() >= 1);

            // 2. Interface type mindspace returns implementations (FastProcessor)
            CommandExecutionResult ifaceRes = await cmd.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    symbol = "Lib.IProcessor",
                }),
                CancellationToken.None);
            Assert.True(ifaceRes.Ok, JsonSerializer.Serialize(ifaceRes.Errors));
            JsonElement ifaceData = JsonSerializer.SerializeToElement(ifaceRes.Data);
            Assert.True(ifaceData.GetProperty("implementations").GetProperty("total_count").GetInt32() >= 1);
            Assert.Contains("FastProcessor", ifaceData.GetProperty("implementations").GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await CloseWorkspaceQuietlyAsync(workspaceHandle);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task P29_SymbolResolution_WorksOnRenameChangeSignatureCallPathAndImpactSlice()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-p29-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            string projPath = Path.Combine(root, "Lib", "Lib.csproj");
            string filePath = Path.Combine(root, "Lib", "Calc.cs");
            string slnxPath = Path.Combine(root, "Calc.slnx");
            await File.WriteAllTextAsync(projPath, SampleCsproj);
            await File.WriteAllTextAsync(slnxPath, SampleSlnx);
            await File.WriteAllTextAsync(
                filePath,
                """
                namespace Lib;

                public sealed class Calc
                {
                    /// <summary>Calls <see cref="OldCompute(int)"/>.</summary>
                    public int Entry(int x) => Middle(x);

                    public int Middle(int x) => OldCompute(x);

                    public int OldCompute(int x) => Add(x, 1);

                    public int Add(int left, int right) => left + right;
                }
                """);

            workspaceHandle = await PreloadWorkspaceAsync(slnxPath);

            // 1. nav.call_path via --source-symbol and --target-symbol
            CallPathCommand callPath = new();
            CommandExecutionResult pathRes = await callPath.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    source_symbol = "Lib.Calc.Entry",
                    target_symbol = "Lib.Calc.Add",
                }),
                CancellationToken.None);
            Assert.True(pathRes.Ok, JsonSerializer.Serialize(pathRes.Errors));
            JsonElement pathData = JsonSerializer.SerializeToElement(pathRes.Data);
            Assert.True(pathData.GetProperty("path_found").GetBoolean());

            // 2. analyze.impact_slice via --symbol
            ImpactSliceCommand impact = new();
            CommandExecutionResult impactRes = await impact.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    symbol = "Lib.Calc.OldCompute",
                }),
                CancellationToken.None);
            Assert.True(impactRes.Ok, JsonSerializer.Serialize(impactRes.Errors));
            JsonElement impactData = JsonSerializer.SerializeToElement(impactRes.Data);
            Assert.Contains("OldCompute", impactData.GetProperty("anchor_symbol").GetProperty("symbol_display").GetString());
            Assert.True(impactData.GetProperty("impact_counts").GetProperty("callers").GetInt32() >= 1);

            // 3. edit.change_signature via --symbol (dry-run)
            ChangeSignatureCommand changeSig = new();
            CommandExecutionResult sigRes = await changeSig.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    symbol = "Lib.Calc.Add",
                    apply = false,
                    parameters = "int right, int left",
                }),
                CancellationToken.None);
            Assert.True(sigRes.Ok, JsonSerializer.Serialize(sigRes.Errors));
            JsonElement sigData = JsonSerializer.SerializeToElement(sigRes.Data);
            Assert.True(sigData.GetProperty("updated_call_site_count").GetInt32() >= 1);

            // 4. edit.rename_symbol via --symbol (apply=true, updates call site and XML doc cref)
            RenameSymbolCommand rename = new();
            CommandExecutionResult renameRes = await rename.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    symbol = "Lib.Calc.OldCompute",
                    new_name = "NewCompute",
                    apply = true,
                }),
                CancellationToken.None);
            Assert.True(renameRes.Ok, JsonSerializer.Serialize(renameRes.Errors));
            string updatedText = await File.ReadAllTextAsync(filePath);
            Assert.Contains("public int NewCompute(int x)", updatedText, StringComparison.Ordinal);
            Assert.Contains("Middle(int x) => NewCompute(x)", updatedText, StringComparison.Ordinal);
            Assert.Contains("cref=\"NewCompute(int)\"", updatedText, StringComparison.Ordinal);
            Assert.DoesNotContain("OldCompute", updatedText, StringComparison.Ordinal);
        }
        finally
        {
            await CloseWorkspaceQuietlyAsync(workspaceHandle);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task P210_Sha256Preconditions_AtomicMultiFileCommit_AndMultiSymbolNavBatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p5-p210-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            (string slnxPath, string alphaPath, string betaPath) = await CreateTwoFileSolutionAsync(root);
            workspaceHandle = await PreloadWorkspaceAsync(slnxPath);

            ReadWindowCommand readWin = new();
            CommandExecutionResult hashesRes = await readWin.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    windows = new object[]
                    {
                        new { file_path = alphaPath, start_line = 1, end_line = 2 },
                        new { file_path = betaPath, start_line = 1, end_line = 2 },
                    },
                }),
                CancellationToken.None);
            Assert.True(hashesRes.Ok);
            JsonElement winArr = JsonSerializer.SerializeToElement(hashesRes.Data).GetProperty("windows");
            string alphaSha = winArr[0].GetProperty("file_sha256").GetString()!;
            string betaSha = winArr[1].GetProperty("file_sha256").GetString()!;
            string wrongSha = new string('0', 64);

            // 1. Mismatched SHA-256 on file 2 must fail closed atomically: ZERO files written (Alpha untouched).
            EditTransactionCommand tx = new();
            CommandExecutionResult failRes = await tx.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    apply = true,
                    atomic = true,
                    expected_file_hashes = new Dictionary<string, string>
                    {
                        [alphaPath] = alphaSha,
                        [betaPath] = wrongSha,
                    },
                    operations = new object[]
                    {
                        new { kind = "replace_text", file_path = alphaPath, old_text = "AlphaHelper", new_text = "AlphaHelperV2" },
                        new { kind = "replace_text", file_path = betaPath, old_text = "UniqueBetaSentinel", new_text = "UpdatedBetaSentinel" },
                    },
                }),
                CancellationToken.None);
            Assert.False(failRes.Ok);
            Assert.Contains(failRes.Errors, e => e.Code == "content_hash_mismatch");
            Assert.Contains("AlphaHelper()", await File.ReadAllTextAsync(alphaPath), StringComparison.Ordinal);

            // 2. Matching SHA-256 preconditions via edit.batch_exact commit both files atomically and return new hashes.
            BatchExactEditCommand batchExact = new();
            CommandExecutionResult okRes = await batchExact.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    apply = true,
                    atomic = true,
                    expected_file_hashes = new Dictionary<string, string>
                    {
                        [alphaPath] = alphaSha,
                        [betaPath] = betaSha,
                    },
                    operations = new object[]
                    {
                        new { kind = "replace_text", file_path = alphaPath, old_text = "return 10;", new_text = "return 20;" },
                        new { kind = "replace_text", file_path = betaPath, old_text = "UniqueBetaSentinel", new_text = "UpdatedBetaSentinel" },
                    },
                }),
                CancellationToken.None);
            Assert.True(okRes.Ok, JsonSerializer.Serialize(okRes.Errors));
            JsonElement okData = JsonSerializer.SerializeToElement(okRes.Data);
            Assert.Equal(2, okData.GetProperty("wrote_file_count").GetInt32());

            JsonElement files = okData.GetProperty("files");
            Assert.Equal(2, files.GetArrayLength());
            foreach (JsonElement f in files.EnumerateArray())
            {
                string orig = f.GetProperty("original_sha256").GetString()!;
                string curr = f.GetProperty("current_sha256").GetString()!;
                Assert.NotEqual(orig, curr);
                string actualDisk = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(f.GetProperty("file_path").GetString()!))).ToLowerInvariant();
                Assert.Equal(actualDisk, curr);
            }

            // Ensure atomic write cleaned up all sibling temp files.
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "Lib"), "*.tmp"));

            // 3. Multi-symbol batching on nav.find_references and nav.find_invocations.
            FindReferencesCommand findRefs = new();
            CommandExecutionResult batchRefs = await findRefs.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    symbols = new[] { "Lib.Alpha.AlphaHelper", "Lib.Beta.BetaWork" },
                }),
                CancellationToken.None);
            Assert.True(batchRefs.Ok, JsonSerializer.Serialize(batchRefs.Errors));
            JsonElement batchRefsData = JsonSerializer.SerializeToElement(batchRefs.Data);
            Assert.True(batchRefsData.GetProperty("batched").GetBoolean());
            Assert.Equal(2, batchRefsData.GetProperty("symbol_count").GetInt32());
            Assert.Equal(2, batchRefsData.GetProperty("succeeded").GetInt32());
            Assert.True(batchRefsData.GetProperty("total_matches").GetInt32() >= 2);

            FindInvocationsCommand findInvs = new();
            CommandExecutionResult batchInvs = await findInvs.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = workspaceHandle,
                    symbols = new[] { "Lib.Alpha.AlphaHelper", "Lib.Beta.BetaWork" },
                    brief = true,
                }),
                CancellationToken.None);
            Assert.True(batchInvs.Ok, JsonSerializer.Serialize(batchInvs.Errors));
            JsonElement batchInvsData = JsonSerializer.SerializeToElement(batchInvs.Data);
            Assert.True(batchInvsData.GetProperty("batched").GetBoolean());
            Assert.Equal(2, batchInvsData.GetProperty("symbol_count").GetInt32());
            Assert.Equal(2, batchInvsData.GetProperty("succeeded").GetInt32());
            Assert.True(batchInvsData.GetProperty("total_matches").GetInt32() >= 2);
        }
        finally
        {
            await CloseWorkspaceQuietlyAsync(workspaceHandle);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private const string SampleCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string SampleSlnx = """
        <Solution>
          <Project Path="Lib/Lib.csproj" />
        </Solution>
        """;

    private static async Task<(string SlnxPath, string AlphaPath, string BetaPath)> CreateTwoFileSolutionAsync(string root)
    {
        string projPath = Path.Combine(root, "Lib", "Lib.csproj");
        string alphaPath = Path.Combine(root, "Lib", "Alpha.cs");
        string betaPath = Path.Combine(root, "Lib", "Beta.cs");
        string slnxPath = Path.Combine(root, "TwoFile.slnx");

        await File.WriteAllTextAsync(projPath, SampleCsproj);
        await File.WriteAllTextAsync(slnxPath, SampleSlnx);
        await File.WriteAllTextAsync(
            alphaPath,
            """
            namespace Lib;

            public sealed class Alpha
            {
                public int AlphaHelper()
                {
                    return 10;
                }

                public int CallBoth(Beta beta) => AlphaHelper() + beta.BetaWork();
            }
            """);
        await File.WriteAllTextAsync(
            betaPath,
            """
            namespace Lib;

            public sealed class Beta
            {
                private const string Tag = "UniqueBetaSentinel";

                public int BetaWork() => Tag.Length;
            }
            """);
        return (slnxPath, alphaPath, betaPath);
    }

    private static async Task<string> PreloadWorkspaceAsync(string slnxPath)
    {
        WorkspacePreloadCommand preload = new();
        CommandExecutionResult res = await preload.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                workspace_path = slnxPath,
                require_solution = true,
                wait_warmup = true,
                wait_warmup_timeout_ms = 60_000,
            }),
            CancellationToken.None);
        Assert.True(res.Ok, JsonSerializer.Serialize(res.Errors));
        return JsonSerializer.SerializeToElement(res.Data).GetProperty("workspace_handle").GetString()!;
    }

    private static async Task<JsonElement> ExecuteSearchAsync(SearchTextCommand search, object payload)
    {
        CommandExecutionResult res = await search.ExecuteAsync(JsonSerializer.SerializeToElement(payload), CancellationToken.None);
        Assert.True(res.Ok, JsonSerializer.Serialize(res.Errors));
        return JsonSerializer.SerializeToElement(res.Data);
    }

    private static async Task CloseWorkspaceQuietlyAsync(string? workspaceHandle)
    {
        if (string.IsNullOrWhiteSpace(workspaceHandle))
        {
            return;
        }

        WorkspaceCloseCommand close = new();
        _ = await close.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { workspace_handle = workspaceHandle }),
            CancellationToken.None);
    }
}
