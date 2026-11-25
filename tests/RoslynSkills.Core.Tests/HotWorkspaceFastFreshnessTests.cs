using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;
using RoslynSkills.WorkspaceHost;

namespace RoslynSkills.Core.Tests;

/// <summary>
/// The zero-stat freshness fast path and the in-memory hot text index, exercised through the real WorkspaceHost
/// process: an edit made right before a call must still be seen (the watcher barrier), the stat sweep must be skipped
/// when nothing changed, and <c>ctx.search_text</c> must read the hot index yet agree with the disk.
/// </summary>
public sealed class HotWorkspaceFastFreshnessTests
{
    [Fact]
    public async Task HotHost_FastPath_SeesExternalEditsImmediately_AndSearchTextReadsHotIndex()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-fast-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using Process process = StartHostProcess();
        try
        {
            string solutionPath = await CreateFixtureAsync(root);
            HostSession host = new(process);
            JsonElement preload = await host.SendAsync(new
            {
                id = "preload",
                method = WorkspaceHostProtocol.Method.WorkspacePreload,
                workspace_alias = "default",
                input = new { workspace_path = solutionPath, require_solution = true, max_files = 100 },
            }, timeoutSeconds: 180);
            Assert.True(preload.GetProperty("ok").GetBoolean(), preload.GetRawText());
            string handle = preload.GetProperty("data").GetProperty("workspace_handle").GetString()!;

            // 1. Nothing changed: the call skips the stat sweep.
            JsonElement firstSearch = await SearchAsync(host, handle, root, "HotMarker");
            Assert.Equal("hot_text_index", TextSource(firstSearch));
            Assert.Equal("watcher_fast_path", firstSearch.GetProperty("freshness_mode").GetString());
            Assert.Equal(2, MatchCount(firstSearch));
            JsonElement quietFind = await FindSymbolAsync(host, handle, Path.Combine(root, "Lib", "Service.cs"), "Run");
            Assert.Equal("watcher_fast_path", quietFind.GetProperty("freshness_mode").GetString());

            // 2. An external create and an external edit, each followed at once by a call: both are visible.
            string externalPath = Path.Combine(root, "Lib", "External.cs");
            await File.WriteAllTextAsync(externalPath, "namespace Lib;\npublic sealed class External { public int HotMarker => 1; }\n");
            string servicePath = Path.Combine(root, "Lib", "Service.cs");
            string service = await File.ReadAllTextAsync(servicePath);
            await File.WriteAllTextAsync(servicePath, service.Replace("HotMarker", "RenamedMarker"));

            JsonElement afterEdit = await SearchAsync(host, handle, root, "HotMarker");
            Assert.Equal("watcher_fast_path", afterEdit.GetProperty("freshness_mode").GetString());
            Assert.Equal("hot_text_index", TextSource(afterEdit));
            string[] hotMarkerFiles = MatchFiles(afterEdit);
            Assert.Contains(hotMarkerFiles, file => file.EndsWith("External.cs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(hotMarkerFiles, file => file.EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, MatchCount(await SearchAsync(host, handle, root, "RenamedMarker")));

            // The semantic workspace caught up with the edit too (incremental refresh, no reload, no stat sweep).
            JsonElement semantic = await FindSymbolAsync(host, handle, externalPath, "External");
            Assert.True(semantic.GetProperty("ok").GetBoolean(), semantic.GetRawText());
            Assert.False(semantic.GetProperty("workspace").GetProperty("requires_reload").GetBoolean());

            // 3. A deletion and a directory move (no per-file events) are reflected as well.
            File.Delete(externalPath);
            string subDirectory = Path.Combine(root, "Lib", "Sub");
            Directory.CreateDirectory(subDirectory);
            await File.WriteAllTextAsync(Path.Combine(subDirectory, "Moved.cs"), "namespace Lib.Sub;\npublic sealed class Moved { public int MovedMarker => 2; }\n");
            Assert.Equal(1, MatchCount(await SearchAsync(host, handle, root, "MovedMarker")));
            string movedDirectory = Path.Combine(root, "Lib", "Relocated");
            Directory.Move(subDirectory, movedDirectory);
            JsonElement afterMove = await SearchAsync(host, handle, root, "MovedMarker");
            string[] movedFiles = MatchFiles(afterMove);
            Assert.Single(movedFiles);
            Assert.Contains("Relocated", movedFiles[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, MatchCount(await SearchAsync(host, handle, root, "HotMarker", scope: Path.Combine(root, "Lib", "Relocated"))));

            // 4. strict policy and includes outside the index contract keep the original behavior.
            JsonElement strictFind = await host.CallAsync(
                "nav.find_symbol",
                new { workspace_handle = handle, file_path = servicePath, symbol_name = "Run", brief = true },
                refreshPolicy: "strict");
            Assert.Equal("full_scan", strictFind.GetProperty("freshness_mode").GetString());

            JsonElement markdownSearch = await host.CallAsync("ctx.search_text", new
            {
                workspace_handle = handle,
                patterns = new[] { "HotMarker" },
                roots = new[] { root },
                include_globs = new[] { "**/*.md" },
            });
            Assert.Equal("disk", TextSource(markdownSearch));
        }
        finally
        {
            TryKillProcessTree(process);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HotHost_SearchText_MatchesDiskScanResults()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-fast-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using Process process = StartHostProcess();
        try
        {
            string solutionPath = await CreateFixtureAsync(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Lib", "Generated.g.cs"), "// HotMarker generated\n");
            Directory.CreateDirectory(Path.Combine(root, "Lib", "Nested", "Deeper"));
            await File.WriteAllTextAsync(Path.Combine(root, "Lib", "Nested", "B.cs"), "// HotMarker b\r\n// HotMarker again\r\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Lib", "Nested", "Deeper", "A.cs"), "// hotmarker a\n");
            HostSession host = new(process);
            JsonElement preload = await host.SendAsync(new
            {
                id = "preload",
                method = WorkspaceHostProtocol.Method.WorkspacePreload,
                workspace_alias = "default",
                input = new { workspace_path = solutionPath, require_solution = true, max_files = 100 },
            }, timeoutSeconds: 180);
            string handle = preload.GetProperty("data").GetProperty("workspace_handle").GetString()!;

            JsonElement hot = await SearchAsync(host, handle, root, "HotMarker", caseSensitive: false);
            Assert.Equal("hot_text_index", TextSource(hot));

            // The same query without a workspace handle walks the disk in-process.
            ICommandRegistry registry = DefaultRegistryFactory.Create();
            Assert.True(registry.TryGet("ctx.search_text", out IAgentCommand? command));
            using JsonDocument diskInput = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                patterns = new[] { "HotMarker" },
                roots = new[] { root },
                case_sensitive = false,
                brief = false,
                max_results = 200,
            }));
            CommandExecutionResult disk = await command!.ExecuteAsync(diskInput.RootElement, CancellationToken.None);
            Assert.True(disk.Ok);
            JsonElement diskData = JsonSerializer.SerializeToElement(disk.Data);
            Assert.Equal("disk", diskData.GetProperty("analysis_scope").GetProperty("text_source").GetString());

            string[] Describe(JsonElement data) => data.GetProperty("matches").EnumerateArray()
                .Select(match => $"{Path.GetRelativePath(root, match.GetProperty("file_path").GetString()!)}:{match.GetProperty("line").GetInt32()}:{match.GetProperty("column").GetInt32()}")
                .ToArray();
            Assert.Equal(Describe(diskData), Describe(hot.GetProperty("data")));
            Assert.Equal(
                diskData.GetProperty("files_scanned").GetInt32(),
                hot.GetProperty("data").GetProperty("files_scanned").GetInt32());
        }
        finally
        {
            TryKillProcessTree(process);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HotTextIndex_EnumeratesLikeDirectoryEnumerationAndDropsDeletedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-text-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "b"));
        Directory.CreateDirectory(Path.Combine(root, "a", "inner"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "proj", "obj"));
        try
        {
            File.WriteAllText(Path.Combine(root, "z.cs"), "// z\n");
            File.WriteAllText(Path.Combine(root, "m.csx"), "// m\n");
            File.WriteAllText(Path.Combine(root, "readme.md"), "# nope\n");
            File.WriteAllText(Path.Combine(root, "b", "b1.cs"), "// b1\n");
            File.WriteAllText(Path.Combine(root, "a", "a1.cs"), "// a1\n");
            File.WriteAllText(Path.Combine(root, "a", "inner", "deep.cs"), "// deep\n");
            File.WriteAllText(Path.Combine(root, "obj", "Skipped.cs"), "// obj\n");
            File.WriteAllText(Path.Combine(root, "bin", "Skipped.cs"), "// bin\n");
            File.WriteAllText(Path.Combine(root, "Thing.g.cs"), "// generated\n");

            using WorkspaceChangeTracker tracker = WorkspaceChangeTracker.Start(
                root,
                Array.Empty<string>(),
                new[] { Path.Combine(root, "proj", "proj.csproj") },
                Path.Combine(root, "x.slnx"),
                Array.Empty<ProjectSourceRules>(),
                new HashSet<string>(),
                includeGenerated: false);
            HotTextIndex index = new(root);

            string[] scopes = { root };
            IReadOnlyDictionary<string, IndexedTextFile> snapshot = index.Snapshot(tracker, scopes, out IReadOnlySet<string> unindexed);
            Assert.Empty(unindexed);
            string[] relative = HotTextIndex.EnumerateBeneath(snapshot, root)
                .Select(file => Path.GetRelativePath(root, file.Path).Replace('\\', '/'))
                .ToArray();
            Assert.Equal(new[] { "m.csx", "z.cs", "a/a1.cs", "b/b1.cs", "a/inner/deep.cs" }, relative);
            Assert.Equal(0, index.FullSyncCount);

            // A second snapshot without changes does no work.
            index.Snapshot(tracker, scopes, out _);
            Assert.Equal(0, index.FullSyncCount);
            Assert.Equal(0, index.IncrementalRefreshCount);

            // A watcher-reported edit is applied incrementally.
            File.WriteAllText(Path.Combine(root, "z.cs"), "// z changed\n");
            Assert.True(tracker.TryWatcherBarrier(TimeSpan.FromSeconds(5)));
            IReadOnlyDictionary<string, IndexedTextFile> changed = index.Snapshot(tracker, scopes, out _);
            Assert.Equal("// z changed", changed[Path.Combine(root, "z.cs")].Lines[0]);
            Assert.Equal(0, index.FullSyncCount);
            Assert.Equal(1, index.IncrementalRefreshCount);

            File.Delete(Path.Combine(root, "b", "b1.cs"));
            Assert.True(tracker.TryWatcherBarrier(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain(Path.Combine(root, "b", "b1.cs"), index.Snapshot(tracker, scopes, out _).Keys);

            // A directory move gives no per-file events; the watcher marks the tracker uncertain and the index rescans.
            long generationBefore = tracker.UncertaintyGeneration;
            Directory.Move(Path.Combine(root, "a"), Path.Combine(root, "moved"));
            Assert.True(tracker.TryWatcherBarrier(TimeSpan.FromSeconds(5)));
            Assert.True(tracker.UncertaintyGeneration > generationBefore);
            IReadOnlyDictionary<string, IndexedTextFile> afterMove = index.Snapshot(tracker, scopes, out _);
            Assert.Contains(Path.Combine(root, "moved", "a1.cs"), afterMove.Keys);
            Assert.DoesNotContain(Path.Combine(root, "a", "a1.cs"), afterMove.Keys);
            Assert.Equal(1, index.FullSyncCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IdentifierIndex_FiltersByIdentifierAndKeepsEscapedTreesAsCandidates()
    {
        SyntaxTree plain = CSharpSyntaxTree.ParseText("namespace N; public class Widget { public int Compute(int x) => x; }");
        Assert.True(IdentifierIndex.MightMention(plain, "Compute"));
        Assert.True(IdentifierIndex.MightMention(plain, "Widget"));
        Assert.False(IdentifierIndex.MightMention(plain, "Comput"));
        Assert.False(IdentifierIndex.MightMention(plain, "Missing"));

        SyntaxTree verbatim = CSharpSyntaxTree.ParseText("class C { int @class; int Use() => @class; }");
        Assert.True(IdentifierIndex.MightMention(verbatim, "class"));

        SyntaxTree escaped = CSharpSyntaxTree.ParseText("class C { int \\u0041bc; }");
        Assert.True(IdentifierIndex.MightMention(escaped, "Abc"));
        Assert.True(IdentifierIndex.MightMention(escaped, "Anything"));
    }

    [Fact]
    public void FreshnessSettings_ParseSweepModeAndSafetyInterval()
    {
        Assert.True(HotWorkspaceFreshnessSettings.IsSweepRequested("sweep"));
        Assert.True(HotWorkspaceFreshnessSettings.IsSweepRequested(" FULL_SCAN "));
        Assert.False(HotWorkspaceFreshnessSettings.IsSweepRequested(null));
        Assert.False(HotWorkspaceFreshnessSettings.IsSweepRequested("fast"));
        Assert.Equal(TimeSpan.FromSeconds(7), HotWorkspaceFreshnessSettings.ResolveSafetyInterval("7"));
        Assert.Equal(TimeSpan.FromSeconds(15), HotWorkspaceFreshnessSettings.ResolveSafetyInterval("nonsense"));
        Assert.Equal(TimeSpan.Zero, HotWorkspaceFreshnessSettings.ResolveSafetyInterval("0"));
    }

    private static async Task<JsonElement> SearchAsync(
        HostSession host,
        string handle,
        string root,
        string pattern,
        bool caseSensitive = true,
        string? scope = null)
    {
        return await host.CallAsync("ctx.search_text", new
        {
            workspace_handle = handle,
            patterns = new[] { pattern },
            roots = new[] { scope ?? root },
            case_sensitive = caseSensitive,
            brief = false,
            max_results = 200,
        });
    }

    private static Task<JsonElement> FindSymbolAsync(HostSession host, string handle, string filePath, string symbol)
        => host.CallAsync("nav.find_symbol", new
        {
            workspace_handle = handle,
            file_path = filePath,
            symbol_name = symbol,
            brief = true,
        });

    private static string TextSource(JsonElement response)
        => response.GetProperty("data").GetProperty("analysis_scope").GetProperty("text_source").GetString()!;

    private static int MatchCount(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("data").GetProperty("total_matches").GetInt32();
    }

    private static string[] MatchFiles(JsonElement response)
        => response.GetProperty("data").GetProperty("matches").EnumerateArray()
            .Select(match => match.GetProperty("file_path").GetString()!)
            .ToArray();

    private static async Task<string> CreateFixtureAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
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

            public sealed class Service
            {
                public int HotMarker => 1;
                public int Run(int x) => x + 1;
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Lib", "Other.cs"),
            """
            namespace Lib;

            public sealed class Other
            {
                public int Use() => new Service().HotMarker;
            }
            """);
        string solutionPath = Path.Combine(root, "FastFixture.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """);
        return solutionPath;
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

    private sealed class HostSession(Process process)
    {
        private int _nextId;

        public Task<JsonElement> CallAsync(string commandId, object input, string? refreshPolicy = null, int timeoutSeconds = 60)
            => SendAsync(
                new
                {
                    id = $"req-{Interlocked.Increment(ref _nextId)}",
                    method = WorkspaceHostProtocol.Method.ToolCall,
                    command_id = commandId,
                    refresh_policy = refreshPolicy,
                    input,
                },
                timeoutSeconds);

        public async Task<JsonElement> SendAsync(object request, int timeoutSeconds = 60)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            Assert.False(string.IsNullOrWhiteSpace(line), "Workspace host closed stdout without responding.");
            using JsonDocument document = JsonDocument.Parse(line!);
            return document.RootElement.Clone();
        }
    }
}
