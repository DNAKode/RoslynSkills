using Microsoft.CodeAnalysis;
using RoslynSkills.Core.Commands;
using Xunit.Sdk;

namespace RoslynSkills.Core.Tests;

public sealed class WorkspaceSemanticLoaderTests
{
    [Fact]
    public async Task LoadForFileAsync_WithExplicitCsprojWorkspace_HasCoreLibraryTypes()
    {
        string root = Path.Combine(Path.GetTempPath(), "roslynskills-workspace-loader-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            string csprojPath = Path.Combine(root, "TargetHarness.csproj");
            File.WriteAllText(csprojPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""", System.Text.Encoding.UTF8);

            string targetPath = Path.Combine(root, "Target.cs");
            File.WriteAllText(targetPath, """
public sealed class Demo
{
    public string M(int x) => x.ToString();
}
""", System.Text.Encoding.UTF8);

            WorkspaceSemanticLoadResult result = await WorkspaceSemanticLoader.LoadForFileAsync(
                filePath: targetPath,
                workspacePath: csprojPath,
                cancellationToken: default);

            if (!string.Equals("workspace", result.workspace_context.mode, StringComparison.Ordinal))
            {
                string attempted = string.Join(" | ", result.workspace_context.attempted_workspace_paths ?? Array.Empty<string>());
                string diagnostics = string.Join("\n", result.workspace_context.workspace_diagnostics ?? Array.Empty<string>());
                throw new XunitException(
                    $"Expected workspace load but got mode='{result.workspace_context.mode}'. " +
                    $"fallback_reason='{result.workspace_context.fallback_reason}'. " +
                    $"attempted='{attempted}'. diagnostics='{diagnostics}'.");
            }

            // Regression guard: when MSBuild is registered to an incompatible instance, MSBuildWorkspace can yield
            // a project with missing reference assemblies, causing CS0518 for core types like System.String.
            Assert.NotEqual(TypeKind.Error, result.compilation.GetSpecialType(SpecialType.System_String).TypeKind);
            Assert.DoesNotContain(result.compilation.GetDiagnostics(), d => d.Id == "CS0518");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadForFileAsync_RepeatedWorkspaceLoad_ReportsProcessCacheHit()
    {
        string root = Path.Combine(Path.GetTempPath(), "roslynskills-workspace-loader-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            (string csprojPath, string targetPath) = CreateSimpleProject(root, "public sealed class Demo { public string M(int x) => x.ToString(); }");

            WorkspaceSemanticLoadResult first = await WorkspaceSemanticLoader.LoadForFileAsync(
                filePath: targetPath,
                workspacePath: csprojPath,
                cancellationToken: default);
            WorkspaceSemanticLoadResult second = await WorkspaceSemanticLoader.LoadForFileAsync(
                filePath: targetPath,
                workspacePath: csprojPath,
                cancellationToken: default);
            string debugInfo = WorkspaceSemanticLoader.GetProcessWorkspaceCacheDebugInfoForTests(csprojPath, targetPath);

            Assert.Equal("workspace", first.workspace_context.mode);
            Assert.Equal("process_balanced", first.workspace_context.workspace_cache_mode);
            Assert.False(first.workspace_context.workspace_cache_hit);

            Assert.Equal("workspace", second.workspace_context.mode);
            Assert.Equal("process_balanced", second.workspace_context.workspace_cache_mode);
            Assert.True(second.workspace_context.workspace_cache_hit, debugInfo);
        }
        finally
        {
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadForFileAsync_WithExplicitDirectory_PrefersSolutionOverNestedProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "roslynskills-workspace-loader-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            (string solutionPath, string appFilePath) = CreateTwoProjectSlnxWorkspace(root);

            WorkspaceSemanticLoadResult result = await WorkspaceSemanticLoader.LoadForFileAsync(
                filePath: appFilePath,
                workspacePath: root,
                cancellationToken: default);

            Assert.Equal("workspace", result.workspace_context.mode);
            Assert.Equal(solutionPath, result.workspace_context.resolved_workspace_path);
            Assert.Equal("slnx", result.workspace_context.workspace_kind);
            Assert.True(result.workspace_context.project_count >= 2, $"Expected full solution project count, got {result.workspace_context.project_count}.");
        }
        finally
        {
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadForFileAsync_WithAutoInference_PrefersSolutionOverNestedProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "roslynskills-workspace-loader-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            (string solutionPath, string appFilePath) = CreateTwoProjectSlnxWorkspace(root);

            WorkspaceSemanticLoadResult result = await WorkspaceSemanticLoader.LoadForFileAsync(
                filePath: appFilePath,
                workspacePath: null,
                cancellationToken: default);

            Assert.Equal("workspace", result.workspace_context.mode);
            Assert.Equal(solutionPath, result.workspace_context.resolved_workspace_path);
            Assert.Equal("slnx", result.workspace_context.workspace_kind);
            Assert.True(result.workspace_context.project_count >= 2, $"Expected full solution project count, got {result.workspace_context.project_count}.");
        }
        finally
        {
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadForFileAsync_WhenTrackedFileChanges_InvalidatesProcessCache()
    {
        string root = Path.Combine(Path.GetTempPath(), "roslynskills-workspace-loader-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            (string csprojPath, string targetPath) = CreateSimpleProject(root, "public sealed class Demo { public int Value => 1; }");

            WorkspaceSemanticLoadResult first = await WorkspaceSemanticLoader.LoadForFileAsync(
                filePath: targetPath,
                workspacePath: csprojPath,
                cancellationToken: default);

            File.WriteAllText(targetPath, "public sealed class Demo { public int Value => 2; }", System.Text.Encoding.UTF8);
            File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow.AddSeconds(1));

            WorkspaceSemanticLoadResult second = await WorkspaceSemanticLoader.LoadForFileAsync(
                filePath: targetPath,
                workspacePath: csprojPath,
                cancellationToken: default);

            Assert.Equal("workspace", first.workspace_context.mode);
            Assert.False(first.workspace_context.workspace_cache_hit);

            Assert.Equal("workspace", second.workspace_context.mode);
            Assert.Equal("process_balanced", second.workspace_context.workspace_cache_mode);
            Assert.False(second.workspace_context.workspace_cache_hit);
            Assert.Contains("=> 2", second.source);
        }
        finally
        {
            WorkspaceSemanticLoader.ClearProcessWorkspaceCacheForTests();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (string csprojPath, string targetPath) CreateSimpleProject(string root, string source)
    {
        string csprojPath = Path.Combine(root, "TargetHarness.csproj");
        File.WriteAllText(csprojPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""", System.Text.Encoding.UTF8);

        string targetPath = Path.Combine(root, "Target.cs");
        File.WriteAllText(targetPath, source, System.Text.Encoding.UTF8);
        return (csprojPath, targetPath);
    }

    private static (string solutionPath, string appFilePath) CreateTwoProjectSlnxWorkspace(string root)
    {
        string appDirectory = Path.Combine(root, "App");
        string libDirectory = Path.Combine(root, "Lib");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(libDirectory);

        string appProjectPath = Path.Combine(appDirectory, "App.csproj");
        string libProjectPath = Path.Combine(libDirectory, "Lib.csproj");
        File.WriteAllText(appProjectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../Lib/Lib.csproj" />
  </ItemGroup>
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""", System.Text.Encoding.UTF8);

        File.WriteAllText(libProjectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""", System.Text.Encoding.UTF8);

        string appFilePath = Path.Combine(appDirectory, "Program.cs");
        File.WriteAllText(appFilePath, """
using Lib;

public sealed class Program
{
    public string Run() => Helper.Name;
}
""", System.Text.Encoding.UTF8);

        File.WriteAllText(Path.Combine(libDirectory, "Helper.cs"), """
namespace Lib;

public static class Helper
{
    public static string Name => "lib";
}
""", System.Text.Encoding.UTF8);

        string solutionPath = Path.Combine(root, "Host.slnx");
        File.WriteAllText(solutionPath, """
<Solution>
  <Project Path="App/App.csproj" />
  <Project Path="Lib/Lib.csproj" />
</Solution>
""", System.Text.Encoding.UTF8);

        return (solutionPath, appFilePath);
    }
}
