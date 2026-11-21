using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;
using System.Text.Json;

namespace RoslynSkills.Core.Tests;

[CollectionDefinition("WorkspaceAutoRestore", DisableParallelization = true)]
public sealed class WorkspaceAutoRestoreCollectionDefinition;

[Collection("WorkspaceAutoRestore")]
public sealed class WorkspaceProjectResolutionTests
{
    [Fact]
    public async Task WorkspacePreload_ReportsUnresolvedProjects_WhenAutoRestoreDisabled_AndQueriesReportUnresolved()
    {
        (string root, string slnxPath, string libFilePath, string testFilePath) = await CreateUnrestoredSolutionAsync("unresolved-disabled");
        string? previousAutoRestore = Environment.GetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE");
        string? workspaceHandle = null;

        try
        {
            Environment.SetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE", "false");
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();

            WorkspacePreloadCommand preload = new();
            CommandExecutionResult preloadResult = await preload.ExecuteAsync(
                ToJsonElement(new { workspace_path = slnxPath, require_solution = true }),
                CancellationToken.None);

            Assert.True(preloadResult.Ok, DescribeErrors(preloadResult));
            JsonElement preloadData = ToJsonElement(preloadResult.Data!);
            workspaceHandle = preloadData.GetProperty("workspace_handle").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workspaceHandle));

            int unresolvedCount = preloadData.GetProperty("unresolved_project_count").GetInt32();
            Assert.True(unresolvedCount >= 1, $"Expected at least 1 unresolved project, got {unresolvedCount}.");

            JsonElement unresolvedProjects = preloadData.GetProperty("unresolved_projects");
            Assert.Contains(
                unresolvedProjects.EnumerateArray(),
                p => string.Equals(p.GetProperty("project_name").GetString(), "CoreLib.Tests", StringComparison.OrdinalIgnoreCase) &&
                     p.GetProperty("needs_restore").GetBoolean() &&
                     p.GetProperty("reasons").EnumerateArray().Any(r => (r.GetString() ?? string.Empty).Contains("project.assets.json", StringComparison.OrdinalIgnoreCase)));

            string[] preloadDiagnostics = preloadData.GetProperty("workspace_diagnostics")
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();
            Assert.Contains(
                preloadDiagnostics,
                d => d.StartsWith("unresolved_project:", StringComparison.Ordinal) &&
                     d.Contains("CoreLib.Tests", StringComparison.OrdinalIgnoreCase));

            // workspace.status must also surface unresolved projects and diagnostics.
            WorkspaceStatusCommand statusCommand = new();
            CommandExecutionResult statusResult = await statusCommand.ExecuteAsync(
                ToJsonElement(new { workspace_handle = workspaceHandle }),
                CancellationToken.None);
            Assert.True(statusResult.Ok, DescribeErrors(statusResult));
            JsonElement statusData = ToJsonElement(statusResult.Data!);
            Assert.True(statusData.GetProperty("unresolved_project_count").GetInt32() >= 1);
            Assert.Contains(
                statusData.GetProperty("workspace_diagnostics").EnumerateArray().Select(d => d.GetString() ?? string.Empty),
                d => d.StartsWith("unresolved_project:", StringComparison.Ordinal) &&
                     d.Contains("CoreLib.Tests", StringComparison.OrdinalIgnoreCase));

            // diag.get_file_diagnostics against the unresolved test project must say project_resolved=false.
            GetFileDiagnosticsCommand diagCommand = new();
            CommandExecutionResult testDiagResult = await diagCommand.ExecuteAsync(
                ToJsonElement(new { file_path = testFilePath, workspace_handle = workspaceHandle, require_workspace = true }),
                CancellationToken.None);
            Assert.True(testDiagResult.Ok, DescribeErrors(testDiagResult));
            JsonElement testDiagContext = ToJsonElement(testDiagResult.Data!).GetProperty("workspace_context");
            Assert.Equal("workspace", testDiagContext.GetProperty("mode").GetString());
            Assert.False(testDiagContext.GetProperty("project_resolved").GetBoolean());
            Assert.EndsWith("CoreLib.Tests.csproj", testDiagContext.GetProperty("project_path").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                testDiagContext.GetProperty("project_resolution_issues").EnumerateArray().Select(i => i.GetString() ?? string.Empty),
                issue => issue.Contains("project.assets.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                testDiagContext.GetProperty("workspace_diagnostics").EnumerateArray().Select(d => d.GetString() ?? string.Empty),
                d => d.StartsWith("unresolved_project:", StringComparison.Ordinal) &&
                     d.Contains("CoreLib.Tests", StringComparison.OrdinalIgnoreCase));

            // nav.find_symbol against the unresolved test project must also say project_resolved=false.
            FindSymbolCommand findSymbolCommand = new();
            CommandExecutionResult symbolResult = await findSymbolCommand.ExecuteAsync(
                ToJsonElement(new { file_path = testFilePath, symbol_name = "AddsNumbers", workspace_handle = workspaceHandle, brief = true }),
                CancellationToken.None);
            Assert.True(symbolResult.Ok, DescribeErrors(symbolResult));
            JsonElement symbolContext = ToJsonElement(symbolResult.Data!).GetProperty("query").GetProperty("workspace_context");
            Assert.False(symbolContext.GetProperty("project_resolved").GetBoolean());
            Assert.NotEmpty(symbolContext.GetProperty("project_resolution_issues").EnumerateArray().ToArray());

            // diag.get_workspace_snapshot against the unresolved test project must report project_resolved=false in workspace_context and diagnostics_context.
            GetWorkspaceSnapshotCommand snapshotCommand = new();
            CommandExecutionResult snapshotResult = await snapshotCommand.ExecuteAsync(
                ToJsonElement(new { file_paths = new[] { testFilePath }, workspace_handle = workspaceHandle, require_workspace = true, mode = "compact" }),
                CancellationToken.None);
            Assert.True(snapshotResult.Ok, DescribeErrors(snapshotResult));
            JsonElement snapshotData = ToJsonElement(snapshotResult.Data!);
            Assert.False(snapshotData.GetProperty("workspace_context").GetProperty("project_resolved").GetBoolean());
            Assert.False(snapshotData.GetProperty("diagnostics_context").GetProperty("project_resolved").GetBoolean());
            Assert.Contains("unresolved", snapshotData.GetProperty("diagnostics_context").GetProperty("note").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Querying the resolved library project in the same solution reports project_resolved=true while noting unresolved_project_count >= 1.
            CommandExecutionResult libDiagResult = await diagCommand.ExecuteAsync(
                ToJsonElement(new { file_path = libFilePath, workspace_handle = workspaceHandle, require_workspace = true }),
                CancellationToken.None);
            Assert.True(libDiagResult.Ok, DescribeErrors(libDiagResult));
            JsonElement libDiagContext = ToJsonElement(libDiagResult.Data!).GetProperty("workspace_context");
            Assert.True(libDiagContext.GetProperty("project_resolved").GetBoolean());
            Assert.True(libDiagContext.GetProperty("unresolved_project_count").GetInt32() >= 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE", previousAutoRestore);
            if (!string.IsNullOrWhiteSpace(workspaceHandle))
            {
                WorkspaceCloseCommand close = new();
                await close.ExecuteAsync(ToJsonElement(new { workspace_handle = workspaceHandle }), CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspacePreload_AutoRestoresUnrestoredPackageReferenceProject_AndResolvesCleanly()
    {
        (string root, string slnxPath, _, string testFilePath) = await CreateUnrestoredSolutionAsync("auto-restore-slnx");
        string? previousAutoRestore = Environment.GetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE");
        string? workspaceHandle = null;

        try
        {
            Environment.SetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE", null);
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();

            WorkspacePreloadCommand preload = new();
            CommandExecutionResult preloadResult = await preload.ExecuteAsync(
                ToJsonElement(new { workspace_path = slnxPath, require_solution = true }),
                CancellationToken.None);

            Assert.True(preloadResult.Ok, DescribeErrors(preloadResult));
            JsonElement preloadData = ToJsonElement(preloadResult.Data!);
            workspaceHandle = preloadData.GetProperty("workspace_handle").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workspaceHandle));

            Assert.True(
                preloadData.GetProperty("unresolved_project_count").GetInt32() == 0,
                $"Expected 0 unresolved projects after auto-restore, got unresolved={preloadData.GetProperty("unresolved_projects").GetRawText()} diagnostics={preloadData.GetProperty("workspace_diagnostics").GetRawText()}");
            Assert.Empty(preloadData.GetProperty("unresolved_projects").EnumerateArray().ToArray());
            Assert.Contains(
                preloadData.GetProperty("workspace_diagnostics").EnumerateArray().Select(d => d.GetString() ?? string.Empty),
                d => d.StartsWith("auto_restore:", StringComparison.Ordinal) &&
                     d.Contains("CoreLib.Tests", StringComparison.OrdinalIgnoreCase));

            GetFileDiagnosticsCommand diagCommand = new();
            CommandExecutionResult diagResult = await diagCommand.ExecuteAsync(
                ToJsonElement(new { file_path = testFilePath, workspace_handle = workspaceHandle, require_workspace = true }),
                CancellationToken.None);
            Assert.True(diagResult.Ok, DescribeErrors(diagResult));
            JsonElement diagData = ToJsonElement(diagResult.Data!);
            Assert.True(diagData.GetProperty("workspace_context").GetProperty("project_resolved").GetBoolean());
            Assert.True(
                diagData.GetProperty("errors").GetInt32() == 0,
                $"Expected 0 errors after auto-restore, got: {diagData.GetProperty("diagnostics").GetRawText()}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE", previousAutoRestore);
            if (!string.IsNullOrWhiteSpace(workspaceHandle))
            {
                WorkspaceCloseCommand close = new();
                await close.ExecuteAsync(ToJsonElement(new { workspace_handle = workspaceHandle }), CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DirectQuery_ReportsUnresolvedProject_WhenAutoRestoreDisabled()
    {
        (string root, string slnxPath, _, string testFilePath) = await CreateUnrestoredSolutionAsync("direct-unresolved");
        string? previousAutoRestore = Environment.GetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE");

        try
        {
            Environment.SetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE", "false");
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();

            GetFileDiagnosticsCommand diagCommand = new();
            CommandExecutionResult diagResult = await diagCommand.ExecuteAsync(
                ToJsonElement(new { file_path = testFilePath, workspace_path = slnxPath, require_workspace = true }),
                CancellationToken.None);

            Assert.True(diagResult.Ok, DescribeErrors(diagResult));
            JsonElement context = ToJsonElement(diagResult.Data!).GetProperty("workspace_context");
            Assert.Equal("workspace", context.GetProperty("mode").GetString());
            Assert.False(context.GetProperty("project_resolved").GetBoolean());
            Assert.Contains(
                context.GetProperty("project_resolution_issues").EnumerateArray().Select(i => i.GetString() ?? string.Empty),
                issue => issue.Contains("project.assets.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                context.GetProperty("workspace_diagnostics").EnumerateArray().Select(d => d.GetString() ?? string.Empty),
                d => d.StartsWith("unresolved_project:", StringComparison.Ordinal) &&
                     d.Contains("CoreLib.Tests", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE", previousAutoRestore);
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<(string Root, string SlnxPath, string LibFilePath, string TestFilePath)> CreateUnrestoredSolutionAsync(string prefix)
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-{prefix}-{Guid.NewGuid():N}");
        string libDir = Path.Combine(root, "src", "CoreLib");
        string testDir = Path.Combine(root, "tests", "CoreLib.Tests");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(testDir);

        string slnxPath = Path.Combine(root, "Demo.slnx");
        string libProjPath = Path.Combine(libDir, "CoreLib.csproj");
        string testProjPath = Path.Combine(testDir, "CoreLib.Tests.csproj");
        string libFilePath = Path.Combine(libDir, "Calculator.cs");
        string testFilePath = Path.Combine(testDir, "CalculatorTests.cs");

        await File.WriteAllTextAsync(
            slnxPath,
            """
            <Solution>
              <Project Path="src/CoreLib/CoreLib.csproj" />
              <Project Path="tests/CoreLib.Tests/CoreLib.Tests.csproj" />
            </Solution>
            """);

        await File.WriteAllTextAsync(
            libProjPath,
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
            testProjPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <ProjectReference Include="..\..\src\CoreLib\CoreLib.csproj" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(
            libFilePath,
            """
            namespace CoreLib;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        await File.WriteAllTextAsync(
            testFilePath,
            """
            using CoreLib;
            using Xunit;

            namespace CoreLib.Tests;

            public sealed class CalculatorTests
            {
                [Fact]
                public void AddsNumbers()
                {
                    Assert.Equal(5, Calculator.Add(2, 3));
                }
            }
            """);

        return (root, slnxPath, libFilePath, testFilePath);
    }

    private static JsonElement ToJsonElement(object value)
        => JsonSerializer.SerializeToElement(value);

    private static string DescribeErrors(CommandExecutionResult result)
        => string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"));
}
