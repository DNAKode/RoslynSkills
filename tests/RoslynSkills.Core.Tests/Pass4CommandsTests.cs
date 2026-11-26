using System.Diagnostics;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.Core.Tests;

public sealed class Pass4CommandsTests
{
    [Fact]
    public void FileOutlineCommand_DeclaresHotWorkspaceOptional()
    {
        FileOutlineCommand command = new();
        Assert.True(command.Descriptor.RunsOnHotWorkspace);
        Assert.True(command.Descriptor.HotWorkspaceOptional);
    }

    [Fact]
    public async Task FileOutlineCommand_UsesCachedSyntaxTreeFromHotWorkspace_AndFallsBackToDisk()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p4-outline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            string projPath = Path.Combine(root, "Lib", "Lib.csproj");
            string filePath = Path.Combine(root, "Lib", "Sample.cs");
            string slnxPath = Path.Combine(root, "Outline.slnx");
            await File.WriteAllTextAsync(
                projPath,
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
                filePath,
                """
                namespace Lib;

                public sealed class Sample
                {
                    public int Alpha(int x) => x + 1;
                    public string Beta() => "ok";
                }
                """);
            await File.WriteAllTextAsync(
                slnxPath,
                """
                <Solution>
                  <Project Path="Lib/Lib.csproj" />
                </Solution>
                """);

            WorkspacePreloadCommand preload = new();
            CommandExecutionResult preloadResult = await preload.ExecuteAsync(
                JsonSerializer.SerializeToElement(new { workspace_path = slnxPath, require_solution = true }),
                CancellationToken.None);
            Assert.True(preloadResult.Ok);
            JsonElement preloadData = JsonSerializer.SerializeToElement(preloadResult.Data);
            workspaceHandle = preloadData.GetProperty("workspace_handle").GetString()!;

            FileOutlineCommand outline = new();

            // 1. With hot workspace handle -> reads cached SyntaxTree without disk re-parse.
            CommandExecutionResult hotResult = await outline.ExecuteAsync(
                JsonSerializer.SerializeToElement(new { file_path = filePath, workspace_handle = workspaceHandle }),
                CancellationToken.None);
            Assert.True(hotResult.Ok);
            JsonElement hotData = JsonSerializer.SerializeToElement(hotResult.Data);
            Assert.Equal("hot_workspace", hotData.GetProperty("summary").GetProperty("syntax_source").GetString());
            Assert.Equal(1, hotData.GetProperty("summary").GetProperty("type_count").GetInt32());
            Assert.Equal(2, hotData.GetProperty("summary").GetProperty("member_count").GetInt32());

            // 2. Without workspace handle -> parses from disk with identical outline.
            CommandExecutionResult diskResult = await outline.ExecuteAsync(
                JsonSerializer.SerializeToElement(new { file_path = filePath }),
                CancellationToken.None);
            Assert.True(diskResult.Ok);
            JsonElement diskData = JsonSerializer.SerializeToElement(diskResult.Data);
            Assert.Equal("disk", diskData.GetProperty("summary").GetProperty("syntax_source").GetString());
            Assert.Equal(diskData.GetProperty("types").GetRawText(), hotData.GetProperty("types").GetRawText());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workspaceHandle))
            {
                WorkspaceCloseCommand close = new();
                _ = await close.ExecuteAsync(
                    JsonSerializer.SerializeToElement(new { workspace_handle = workspaceHandle }),
                    CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task QueryBatchCommand_SupportsImplementationsOverridesSnapshotEnvelopeAndChangedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-p4-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        string? workspaceHandle = null;
        try
        {
            await RunGitAsync(root, "init");
            string projPath = Path.Combine(root, "Lib", "Lib.csproj");
            string codePath = Path.Combine(root, "Lib", "Hierarchy.cs");
            string slnxPath = Path.Combine(root, "Batch.slnx");
            await File.WriteAllTextAsync(
                projPath,
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
                codePath,
                """
                namespace Lib;

                public interface IWorker
                {
                    void Work();
                }

                public class BaseWorker : IWorker
                {
                    public virtual void Work() { }
                }

                public sealed class DerivedWorker : BaseWorker
                {
                    public override void Work() { }
                }
                """);
            await File.WriteAllTextAsync(
                slnxPath,
                """
                <Solution>
                  <Project Path="Lib/Lib.csproj" />
                </Solution>
                """);
            await RunGitAsync(root, "add Lib/Hierarchy.cs");

            WorkspacePreloadCommand preload = new();
            CommandExecutionResult preloadResult = await preload.ExecuteAsync(
                JsonSerializer.SerializeToElement(new { workspace_path = slnxPath, require_solution = true }),
                CancellationToken.None);
            Assert.True(preloadResult.Ok);
            workspaceHandle = JsonSerializer.SerializeToElement(preloadResult.Data).GetProperty("workspace_handle").GetString()!;

            QueryBatchCommand batch = new();
            JsonElement batchInput = JsonSerializer.SerializeToElement(new
            {
                workspace_handle = workspaceHandle,
                continue_on_error = false,
                queries = new object[]
                {
                    new
                    {
                        command_id = "nav.find_implementations",
                        input = new { symbol = "Lib.IWorker" },
                    },
                    new
                    {
                        command_id = "nav.find_overrides",
                        input = new { symbol = "Lib.BaseWorker.Work" },
                    },
                    new
                    {
                        command_id = "diag.get_workspace_snapshot",
                        input = new { directory_path = Path.Combine(root, "Lib"), brief = true, include_diagnostics = true, require_workspace = true },
                    },
                    new
                    {
                        command_id = "ctx.symbol_envelope",
                        input = new { file_path = codePath, line = 3, column = 18 },
                    },
                    new
                    {
                        command_id = "ctx.changed_files",
                        input = new { repo_root = root },
                    },
                },
            });

            Assert.Empty(batch.Validate(batchInput));
            CommandExecutionResult result = await batch.ExecuteAsync(batchInput, CancellationToken.None);
            Assert.True(result.Ok, JsonSerializer.Serialize(result.Errors));

            JsonElement data = JsonSerializer.SerializeToElement(result.Data);
            Assert.Equal(5, data.GetProperty("total_executed").GetInt32());
            Assert.Equal(5, data.GetProperty("succeeded").GetInt32());
            Assert.Equal(0, data.GetProperty("failed").GetInt32());

            JsonElement results = data.GetProperty("results");
            Assert.True(results[0].GetProperty("ok").GetBoolean(), results[0].GetRawText());
            Assert.True(results[0].GetProperty("data").GetProperty("total_matches").GetInt32() >= 1);

            Assert.True(results[1].GetProperty("ok").GetBoolean(), results[1].GetRawText());
            Assert.True(results[1].GetProperty("data").GetProperty("total_matches").GetInt32() >= 1);

            Assert.True(results[2].GetProperty("ok").GetBoolean(), results[2].GetRawText());
            Assert.Equal(0, results[2].GetProperty("data").GetProperty("summary").GetProperty("diagnostics").GetProperty("errors").GetInt32());

            Assert.True(results[3].GetProperty("ok").GetBoolean(), results[3].GetRawText());
            Assert.Contains("IWorker", results[3].GetProperty("data").GetProperty("display_name").GetString());

            Assert.True(results[4].GetProperty("ok").GetBoolean(), results[4].GetRawText());
            Assert.True(results[4].GetProperty("data").GetProperty("csharp_changed").GetInt32() >= 1);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workspaceHandle))
            {
                WorkspaceCloseCommand close = new();
                _ = await close.ExecuteAsync(
                    JsonSerializer.SerializeToElement(new { workspace_handle = workspaceHandle }),
                    CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CallHierarchyCommand_DefaultsToMaxDepth1_AndAllowsOptInDepth2()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-p4-depth-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public sealed class Chain
                {
                    public void Top() => Middle();
                    public void Middle() => Leaf();
                    public void Leaf() { }
                }
                """);

            CallHierarchyCommand command = new();

            // Default max_depth should be 1: includes Middle, omits Top.
            CommandExecutionResult depth1Result = await command.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    file_path = filePath,
                    line = 5,
                    column = 17,
                    direction = "incoming",
                    brief = true,
                }),
                CancellationToken.None);
            Assert.True(depth1Result.Ok);
            JsonElement depth1Data = JsonSerializer.SerializeToElement(depth1Result.Data);
            Assert.Equal(1, depth1Data.GetProperty("query").GetProperty("max_depth").GetInt32());
            string depth1Nodes = depth1Data.GetProperty("nodes").GetRawText();
            Assert.Contains("Middle", depth1Nodes, StringComparison.Ordinal);
            Assert.DoesNotContain("Top", depth1Nodes, StringComparison.Ordinal);

            // Explicit max_depth=2 reaches Top as well.
            CommandExecutionResult depth2Result = await command.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    file_path = filePath,
                    line = 5,
                    column = 17,
                    direction = "incoming",
                    max_depth = 2,
                    brief = true,
                }),
                CancellationToken.None);
            Assert.True(depth2Result.Ok);
            JsonElement depth2Data = JsonSerializer.SerializeToElement(depth2Result.Data);
            Assert.Equal(2, depth2Data.GetProperty("query").GetProperty("max_depth").GetInt32());
            string depth2Nodes = depth2Data.GetProperty("nodes").GetRawText();
            Assert.Contains("Middle", depth2Nodes, StringComparison.Ordinal);
            Assert.Contains("Top", depth2Nodes, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(filePath); } catch { }
        }
    }

    private static async Task RunGitAsync(string workingDirectory, string arguments)
    {
        ProcessStartInfo psi = new("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        await process.WaitForExitAsync();
    }
}
