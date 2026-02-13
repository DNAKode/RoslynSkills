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
}
