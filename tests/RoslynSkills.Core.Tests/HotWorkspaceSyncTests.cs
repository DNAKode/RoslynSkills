using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;
using RoslynSkills.WorkspaceHost;

namespace RoslynSkills.Core.Tests;

/// <summary>
/// Verifies hot-workspace pre-call filesystem synchronization through the real WorkspaceHost process on a
/// 3-project solution: externally created files join their project immediately, deleted files and
/// <c>git clean</c>-removed files disappear from <c>document_count</c> and nav results, changes under
/// <c>bin/</c>, <c>obj/</c>, non-source folders, or <c>Compile Remove</c> paths neither dirty nor reload,
/// and <c>.csproj</c> edits ask for a reload.
/// </summary>
public sealed class HotWorkspaceSyncTests
{
    [Fact]
    public async Task HotHost_ThreeProjectFixture_ExternalCreateDeleteGitCleanIgnoredAndCsprojReload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-hot-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using Process process = StartHostProcess();

        try
        {
            (string solutionPath, string libProjectPath, string servicePath, string appPath, string consumerPath) =
                await CreateThreeProjectGitRepoAsync(root);
            HostSession host = new(process);

            JsonElement preload = await host.SendAsync(new
            {
                id = "preload",
                method = WorkspaceHostProtocol.Method.WorkspacePreload,
                workspace_alias = "default",
                input = new { workspace_path = solutionPath, require_solution = true, max_files = 100 },
            }, timeoutSeconds: 120);
            Assert.True(preload.GetProperty("ok").GetBoolean(), preload.GetRawText());
            JsonElement preloadData = DataOf(preload);
            string handle = preloadData.GetProperty("workspace_handle").GetString()!;
            int baselineDocuments = preloadData.GetProperty("documents_loaded").GetInt32();
            Assert.Equal(3, preloadData.GetProperty("projects_loaded").GetInt32());

            // -----------------------------------------------------------------
            // 1. File written externally via File.WriteAllText is found by the very next nav.find_symbol
            // -----------------------------------------------------------------
            string externalFilePath = Path.Combine(root, "App", "External", "ExternalConsumer.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(externalFilePath)!);
            await File.WriteAllTextAsync(
                externalFilePath,
                """
                namespace App.External;

                public sealed class ExternalConsumer : Lib.IStore
                {
                    public void Save() => new Lib.Service().Run(42);
                }
                """);

            // Also verify a C# file containing "Analyzer" in its name is added incrementally (not hijacked as analyzer config).
            string analyzerFilePath = Path.Combine(root, "Lib", "CustomAnalyzer.cs");
            await File.WriteAllTextAsync(
                analyzerFilePath,
                """
                namespace Lib;

                public sealed class CustomAnalyzer
                {
                    public int Analyze() => 7;
                }
                """);

            JsonElement findExternal = await host.CallAsync("nav.find_symbol", new
            {
                workspace_handle = handle,
                file_path = externalFilePath,
                symbol_name = "ExternalConsumer",
                require_workspace = true,
                brief = true,
            });
            Assert.True(findExternal.GetProperty("ok").GetBoolean(), findExternal.GetRawText());
            JsonElement findExternalData = DataOf(findExternal);
            Assert.True(findExternalData.GetProperty("total_matches").GetInt32() >= 1);
            Assert.Equal("process_hot", findExternalData.GetProperty("query").GetProperty("workspace_context").GetProperty("workspace_cache_mode").GetString());
            Assert.Equal("incremental_document_update", findExternal.GetProperty("workspace").GetProperty("refresh_action").GetString());
            Assert.False(findExternal.GetProperty("workspace").GetProperty("requires_reload").GetBoolean());
            Assert.Equal(baselineDocuments + 2, findExternal.GetProperty("workspace").GetProperty("document_count").GetInt32());

            // Solution-wide nav.find_implementations sees ExternalConsumer from the newly added file.
            JsonElement implsWithExternal = await host.CallAsync("nav.find_implementations", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 8,
                column = 18,
                require_workspace = true,
            });
            Assert.True(implsWithExternal.GetProperty("ok").GetBoolean(), implsWithExternal.GetRawText());
            string[] implDisplaysWithExternal = DataOf(implsWithExternal).GetProperty("matches").EnumerateArray()
                .Select(m => m.GetProperty("symbol_display").GetString()!)
                .ToArray();
            Assert.Contains("App.External.ExternalConsumer", implDisplaysWithExternal);

            // And nav.find_symbol finds CustomAnalyzer in the hot workspace.
            JsonElement findAnalyzer = await host.CallAsync("nav.find_symbol", new
            {
                workspace_handle = handle,
                file_path = analyzerFilePath,
                symbol_name = "CustomAnalyzer",
                require_workspace = true,
                brief = true,
            });
            Assert.True(findAnalyzer.GetProperty("ok").GetBoolean(), findAnalyzer.GetRawText());
            Assert.True(DataOf(findAnalyzer).GetProperty("total_matches").GetInt32() >= 1);

            // -----------------------------------------------------------------
            // 2. Deleted file disappears from document_count and from nav results (no requires_reload)
            // -----------------------------------------------------------------
            File.Delete(externalFilePath);
            File.Delete(analyzerFilePath);

            JsonElement implsAfterDelete = await host.CallAsync("nav.find_implementations", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 8,
                column = 18,
                require_workspace = true,
            });
            Assert.True(implsAfterDelete.GetProperty("ok").GetBoolean(), implsAfterDelete.GetRawText());
            Assert.Equal("incremental_document_update", implsAfterDelete.GetProperty("workspace").GetProperty("refresh_action").GetString());
            Assert.False(implsAfterDelete.GetProperty("workspace").GetProperty("requires_reload").GetBoolean());
            Assert.False(implsAfterDelete.GetProperty("workspace").GetProperty("dirty_after").GetBoolean());
            Assert.Equal(baselineDocuments, implsAfterDelete.GetProperty("workspace").GetProperty("document_count").GetInt32());

            string[] implDisplaysAfterDelete = DataOf(implsAfterDelete).GetProperty("matches").EnumerateArray()
                .Select(m => m.GetProperty("symbol_display").GetString()!)
                .ToArray();
            Assert.DoesNotContain("App.External.ExternalConsumer", implDisplaysAfterDelete);

            JsonElement statusAfterDelete = await host.SendAsync(new
            {
                id = "status-after-delete",
                method = WorkspaceHostProtocol.Method.WorkspaceStatus,
                input = new { workspace_handle = handle },
            });
            JsonElement statusAfterDeleteData = DataOf(statusAfterDelete);
            Assert.False(statusAfterDeleteData.GetProperty("dirty").GetBoolean(), statusAfterDeleteData.GetRawText());
            Assert.False(statusAfterDeleteData.GetProperty("requires_reload").GetBoolean(), statusAfterDeleteData.GetRawText());
            Assert.Equal(baselineDocuments, statusAfterDeleteData.GetProperty("documents_loaded").GetInt32());

            // -----------------------------------------------------------------
            // 3. git clean -fd after edit.create_file leaves no stale document
            // -----------------------------------------------------------------
            string createdFilePath = Path.Combine(root, "Consumer", "Scratch", "TempImpl.cs");
            JsonElement createResp = await host.CallAsync("edit.create_file", new
            {
                workspace_handle = handle,
                file_path = createdFilePath,
                content = """
                    namespace Consumer.Scratch;

                    public sealed class TempImpl : Lib.IStore
                    {
                        public void Save() { }
                    }
                    """,
                apply = true,
                require_workspace = true,
            });
            Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.GetRawText());

            JsonElement implsAfterCreate = await host.CallAsync("nav.find_implementations", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 8,
                column = 18,
                require_workspace = true,
            });
            Assert.Equal(baselineDocuments + 1, implsAfterCreate.GetProperty("workspace").GetProperty("document_count").GetInt32());
            Assert.Contains(
                "Consumer.Scratch.TempImpl",
                DataOf(implsAfterCreate).GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("symbol_display").GetString()!));

            RunGit(root, "clean -fd");
            Assert.False(File.Exists(createdFilePath));

            JsonElement implsAfterGitClean = await host.CallAsync("nav.find_implementations", new
            {
                workspace_handle = handle,
                file_path = servicePath,
                line = 8,
                column = 18,
                require_workspace = true,
            });
            Assert.True(implsAfterGitClean.GetProperty("ok").GetBoolean(), implsAfterGitClean.GetRawText());
            Assert.Equal("incremental_document_update", implsAfterGitClean.GetProperty("workspace").GetProperty("refresh_action").GetString());
            Assert.False(implsAfterGitClean.GetProperty("workspace").GetProperty("requires_reload").GetBoolean());
            Assert.False(implsAfterGitClean.GetProperty("workspace").GetProperty("dirty_after").GetBoolean());
            Assert.Equal(baselineDocuments, implsAfterGitClean.GetProperty("workspace").GetProperty("document_count").GetInt32());
            Assert.DoesNotContain(
                "Consumer.Scratch.TempImpl",
                DataOf(implsAfterGitClean).GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("symbol_display").GetString()!));

            // -----------------------------------------------------------------
            // 4. Changes under bin/, obj/, non-source folder (evidence/), and Compile Remove neither dirty nor reload
            // -----------------------------------------------------------------
            string binDir = Path.Combine(root, "Lib", "bin", "Debug", "net10.0");
            string objDir = Path.Combine(root, "Lib", "obj", "Debug", "net10.0");
            string evidenceDir = Path.Combine(root, "evidence", "run-1");
            string excludedDir = Path.Combine(root, "Lib", "Excluded");
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(objDir);
            Directory.CreateDirectory(evidenceDir);
            Directory.CreateDirectory(excludedDir);

            await File.WriteAllTextAsync(Path.Combine(binDir, "GeneratedInBin.cs"), "public class InBin { }");
            await File.WriteAllTextAsync(Path.Combine(binDir, "Lib.dll"), "fake");
            await File.WriteAllTextAsync(Path.Combine(objDir, "Lib.GlobalUsings.g.cs"), "global using System;");
            await File.WriteAllTextAsync(Path.Combine(evidenceDir, "report.json"), "{\"ok\":true}");
            await File.WriteAllTextAsync(Path.Combine(evidenceDir, "analyzer_summary.json"), "{\"ok\":true}");
            await File.WriteAllTextAsync(Path.Combine(evidenceDir, "OutsideProject.cs"), "public class OutsideProject { }");
            await File.WriteAllTextAsync(Path.Combine(root, "Lib", "README.md"), "# notes");
            await File.WriteAllTextAsync(Path.Combine(excludedDir, "IgnoredByProject.cs"), "public class IgnoredByProject { }");
            await File.WriteAllTextAsync(Path.Combine(root, "Lib", "ExcludedSingle.cs"), "public class ExcludedSingle { }");

            // Also delete a directory under evidence/ to trigger directory-level watcher events.
            string transientDir = Path.Combine(root, "evidence", "transient");
            Directory.CreateDirectory(transientDir);
            Directory.Delete(transientDir);

            // Give the FileSystemWatcher time to deliver all events.
            await Task.Delay(300);

            JsonElement statusAfterNoise = await host.SendAsync(new
            {
                id = "status-after-noise",
                method = WorkspaceHostProtocol.Method.WorkspaceStatus,
                input = new { workspace_handle = handle },
            });
            JsonElement statusAfterNoiseData = DataOf(statusAfterNoise);
            Assert.False(statusAfterNoiseData.GetProperty("dirty").GetBoolean(), statusAfterNoiseData.GetRawText());
            Assert.False(statusAfterNoiseData.GetProperty("requires_reload").GetBoolean(), statusAfterNoiseData.GetRawText());
            Assert.Empty(statusAfterNoiseData.GetProperty("invalidated_paths").EnumerateArray());
            Assert.Equal(baselineDocuments, statusAfterNoiseData.GetProperty("documents_loaded").GetInt32());

            JsonElement navAfterNoise = await host.CallAsync("nav.find_symbol", new
            {
                workspace_handle = handle,
                file_path = appPath,
                symbol_name = "AppRunner",
                require_workspace = true,
                brief = true,
            });
            Assert.True(navAfterNoise.GetProperty("ok").GetBoolean(), navAfterNoise.GetRawText());
            Assert.Equal("none", navAfterNoise.GetProperty("workspace").GetProperty("refresh_action").GetString());
            Assert.False(navAfterNoise.GetProperty("workspace").GetProperty("dirty_before").GetBoolean());
            Assert.False(navAfterNoise.GetProperty("workspace").GetProperty("requires_reload").GetBoolean());
            Assert.Equal(baselineDocuments, navAfterNoise.GetProperty("workspace").GetProperty("document_count").GetInt32());

            // -----------------------------------------------------------------
            // 5. A .csproj change DOES ask for a reload
            // -----------------------------------------------------------------
            await File.AppendAllTextAsync(libProjectPath, Environment.NewLine + "<!-- project structure changed -->");

            JsonElement statusAfterCsproj = await host.SendAsync(new
            {
                id = "status-after-csproj",
                method = WorkspaceHostProtocol.Method.WorkspaceStatus,
                input = new { workspace_handle = handle },
            });
            JsonElement statusAfterCsprojData = DataOf(statusAfterCsproj);
            Assert.True(statusAfterCsprojData.GetProperty("dirty").GetBoolean(), statusAfterCsprojData.GetRawText());
            Assert.True(statusAfterCsprojData.GetProperty("requires_reload").GetBoolean(), statusAfterCsprojData.GetRawText());
            Assert.Contains(
                "project_structure_change",
                statusAfterCsprojData.GetProperty("dirty_kinds").EnumerateArray().Select(k => k.GetString()));

            JsonElement navAfterCsproj = await host.CallAsync("nav.find_symbol", new
            {
                workspace_handle = handle,
                file_path = consumerPath,
                symbol_name = "Caller",
                require_workspace = true,
                brief = true,
            });
            Assert.True(navAfterCsproj.GetProperty("ok").GetBoolean(), navAfterCsproj.GetRawText());
            Assert.True(navAfterCsproj.GetProperty("workspace").GetProperty("requires_reload").GetBoolean());

            await process.StandardInput.WriteLineAsync("{\"id\":\"shutdown\",\"method\":\"shutdown\"}");
            _ = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after shutdown.");
        }
        finally
        {
            TryKillProcessTree(process);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ProjectSourceRules_RespectsDefaultGlobsExcludedDirsAndCompileRemoveInclude()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-proj-rules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string projDir = Path.Combine(root, "Proj");
            Directory.CreateDirectory(projDir);
            string projPath = Path.Combine(projDir, "Proj.csproj");
            File.WriteAllText(
                projPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <BaseOutputPath>custom-out\</BaseOutputPath>
                    <DefaultItemExcludes>$(DefaultItemExcludes);Scratch\**</DefaultItemExcludes>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Remove="Legacy\**\*.cs;SingleExcluded.cs" />
                    <Compile Include="Legacy\KeepMe.cs" />
                  </ItemGroup>
                </Project>
                """);

            ProjectSourceRules rules = ProjectSourceRules.LoadForProject(projPath, LanguageNames.CSharp, root)!;
            Assert.NotNull(rules);

            Assert.True(rules.IncludesSourceFile(Path.Combine(projDir, "Normal.cs"), includeGenerated: false));
            Assert.True(rules.IncludesSourceFile(Path.Combine(projDir, "Sub", "Nested.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "Script.csx"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "Notes.md"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "bin", "Debug", "Foo.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "obj", "Debug", "Foo.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "custom-out", "Debug", "Foo.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "Scratch", "Temp.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "SingleExcluded.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "Legacy", "Old.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(projDir, "Legacy", "Deep", "Old.cs"), includeGenerated: false));
            Assert.True(rules.IncludesSourceFile(Path.Combine(projDir, "Legacy", "KeepMe.cs"), includeGenerated: false));
            Assert.False(rules.IncludesSourceFile(Path.Combine(root, "evidence", "Outside.cs"), includeGenerated: false));

            // Project with EnableDefaultCompileItems=false only includes explicit Compile Include items.
            string explicitDir = Path.Combine(root, "Explicit");
            Directory.CreateDirectory(explicitDir);
            string explicitProjPath = Path.Combine(explicitDir, "Explicit.csproj");
            File.WriteAllText(
                explicitProjPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Allowed\*.cs" />
                  </ItemGroup>
                </Project>
                """);

            ProjectSourceRules explicitRules = ProjectSourceRules.LoadForProject(explicitProjPath, LanguageNames.CSharp, root)!;
            Assert.False(explicitRules.IncludesSourceFile(Path.Combine(explicitDir, "Random.cs"), includeGenerated: false));
            Assert.True(explicitRules.IncludesSourceFile(Path.Combine(explicitDir, "Allowed", "Good.cs"), includeGenerated: false));
            Assert.False(explicitRules.IncludesSourceFile(Path.Combine(explicitDir, "Allowed", "Deep", "NotMatched.cs"), includeGenerated: false));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorkspaceChangeClassifier_IgnoresNonSourceAndOutputsAndReloadsOnlyProjectOrSolutionFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-classifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string projDir = Path.Combine(root, "Lib");
            Directory.CreateDirectory(projDir);
            string projPath = Path.Combine(projDir, "Lib.csproj");
            File.WriteAllText(
                projPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Remove="Excluded/**" /></ItemGroup>
                </Project>
                """);
            string solutionPath = Path.Combine(root, "Repo.slnx");
            ProjectSourceRules[] rules = { ProjectSourceRules.LoadForProject(projPath, LanguageNames.CSharp, root)! };
            HashSet<string> baselineExcluded = new(StringComparer.OrdinalIgnoreCase);

            // Non-source, evidence, bin/obj, excluded source, and unknown deleted paths are all ignored.
            string[] ignoredPaths =
            {
                Path.Combine(root, "evidence", "report.json"),
                Path.Combine(root, "evidence", "analyzer_report.json"),
                Path.Combine(root, "evidence", "Outside.cs"),
                Path.Combine(projDir, "bin", "Debug", "Foo.cs"),
                Path.Combine(projDir, "obj", "Debug", "Foo.g.cs"),
                Path.Combine(projDir, "Excluded", "Skip.cs"),
                Path.Combine(projDir, "README.md"),
                root,
            };
            foreach (string ignored in ignoredPaths)
            {
                WorkspaceChange change = WorkspaceChangeClassifier.Classify(
                    ignored,
                    root,
                    isTrackedSource: false,
                    isKnownProject: false,
                    solutionPath,
                    rules,
                    baselineExcluded,
                    "Changed",
                    exists: true,
                    lastWriteUtc: DateTimeOffset.UtcNow);
                Assert.True(change.Ignored, $"Expected ignored for {ignored}, got {change.DirtyKind} (requires_reload={change.RequiresReload}).");
                Assert.False(change.RequiresReload);
            }

            // New C# file (including one with "Analyzer" in its name) under a loaded project -> source_added (incremental).
            WorkspaceChange addedAnalyzer = WorkspaceChangeClassifier.Classify(
                Path.Combine(projDir, "MyAnalyzer.cs"),
                root,
                isTrackedSource: false,
                isKnownProject: false,
                solutionPath,
                rules,
                baselineExcluded,
                "Created",
                exists: true,
                lastWriteUtc: DateTimeOffset.UtcNow);
            Assert.False(addedAnalyzer.Ignored);
            Assert.Equal("source_added", addedAnalyzer.DirtyKind);
            Assert.True(addedAnalyzer.CanIncrementallyUpdate);
            Assert.False(addedAnalyzer.RequiresReload);

            // Deleted tracked source file -> source_removed (incremental).
            WorkspaceChange removedTracked = WorkspaceChangeClassifier.Classify(
                Path.Combine(projDir, "Existing.cs"),
                root,
                isTrackedSource: true,
                isKnownProject: false,
                solutionPath,
                rules,
                baselineExcluded,
                "Deleted",
                exists: false,
                lastWriteUtc: null);
            Assert.False(removedTracked.Ignored);
            Assert.Equal("source_removed", removedTracked.DirtyKind);
            Assert.True(removedTracked.CanIncrementallyUpdate);
            Assert.False(removedTracked.RequiresReload);

            // Project and solution files -> project_structure_change (requires_reload).
            foreach (string structural in new[] { projPath, solutionPath })
            {
                WorkspaceChange change = WorkspaceChangeClassifier.Classify(
                    structural,
                    root,
                    isTrackedSource: false,
                    isKnownProject: string.Equals(structural, projPath, StringComparison.OrdinalIgnoreCase),
                    solutionPath,
                    rules,
                    baselineExcluded,
                    "Changed",
                    exists: true,
                    lastWriteUtc: DateTimeOffset.UtcNow);
                Assert.False(change.Ignored);
                Assert.Equal("project_structure_change", change.DirtyKind);
                Assert.False(change.CanIncrementallyUpdate);
                Assert.True(change.RequiresReload);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<(string Solution, string LibProject, string Service, string App, string Consumer)> CreateThreeProjectGitRepoAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "Consumer"));

        string libProjectPath = Path.Combine(root, "Lib", "Lib.csproj");
        await File.WriteAllTextAsync(
            libProjectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Remove="Excluded\**" />
                <Compile Remove="ExcludedSingle.cs" />
              </ItemGroup>
            </Project>
            """);

        const string refProjectTemplate = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"), refProjectTemplate);
        await File.WriteAllTextAsync(Path.Combine(root, "Consumer", "Consumer.csproj"), refProjectTemplate);

        string servicePath = Path.Combine(root, "Lib", "Service.cs");
        await File.WriteAllTextAsync(
            servicePath,
            """
            namespace Lib;

            public sealed class Service
            {
                public int Run(int x) => x + 1;
            }

            public interface IStore
            {
                void Save();
            }

            public sealed class FileStore : IStore
            {
                public void Save() { }
            }
            """);

        string appPath = Path.Combine(root, "App", "AppRunner.cs");
        await File.WriteAllTextAsync(
            appPath,
            """
            namespace App;

            public sealed class AppRunner : Lib.IStore
            {
                public int Go() => new Lib.Service().Run(1);
                public void Save() { }
            }
            """);

        string consumerPath = Path.Combine(root, "Consumer", "Caller.cs");
        await File.WriteAllTextAsync(
            consumerPath,
            """
            namespace Consumer;

            public sealed class Caller : Lib.IStore
            {
                public int Use() => new Lib.Service().Run(2);
                public void Save() { }
            }
            """);

        string solutionPath = Path.Combine(root, "SyncFixture.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="App/App.csproj" />
              <Project Path="Consumer/Consumer.csproj" />
            </Solution>
            """);

        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "bin/\nobj/\n");

        RunGit(root, "init");
        RunGit(root, "config user.email test@example.com");
        RunGit(root, "config user.name TestUser");
        RunGit(root, "config commit.gpgsign false");
        RunGit(root, "add .");
        RunGit(root, "commit -m \"Initial fixture\"");

        return (solutionPath, libProjectPath, servicePath, appPath, consumerPath);
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
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start git {arguments}.");
        try
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                TryKillProcessTree(process);
                Assert.Fail($"git {arguments} timed out after 30s.");
            }

            string stdout = stdoutTask.Wait(TimeSpan.FromSeconds(5)) ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.Wait(TimeSpan.FromSeconds(5)) ? stderrTask.Result : string.Empty;
            Assert.True(process.ExitCode == 0, $"git {arguments} failed ({process.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        finally
        {
            TryKillProcessTree(process);
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

    private static JsonElement DataOf(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("envelope").GetProperty("Data");
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
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            }
            catch (TimeoutException)
            {
                TryKillProcessTree(process);
                string stderr;
                try
                {
                    stderr = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    stderr = string.Empty;
                }

                throw new TimeoutException($"Timed out waiting for workspace host response. Stderr: {stderr}");
            }

            Assert.False(string.IsNullOrWhiteSpace(line), "Workspace host closed stdout without responding.");
            using JsonDocument document = JsonDocument.Parse(line!);
            return document.RootElement.Clone();
        }
    }
}
