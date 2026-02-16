using Microsoft.CodeAnalysis;
using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;
using System.Text.Json;

namespace RoslynSkills.Core.Tests;

public sealed class VbCommandTests
{
    [Fact]
    public async Task FindSymbolCommand_FindsMatchesInVbFile()
    {
        string filePath = WriteTempFile(
            """
            Public Class Demo
                Public Function Add(x As Integer, y As Integer) As Integer
                    Return x + y
                End Function

                Public Function Run() As Integer
                    Return Add(1, 2)
                End Function
            End Class
            """,
            ".vb");

        try
        {
            FindSymbolCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                symbol_name = "Add",
                brief = true,
                max_results = 10,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_matches\":2", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task FindReferencesCommand_FindsReferencesInVbFile()
    {
        string filePath = WriteTempFile(
            """
            Public Class Demo
                Public Function Add(x As Integer, y As Integer) As Integer
                    Return x + y
                End Function

                Public Function Run() As Integer
                    Return Add(1, 2)
                End Function
            End Class
            """,
            ".vb");

        try
        {
            FindReferencesCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 2,
                column = 22,
                max_results = 10,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_matches\":2", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task FindInvocationsCommand_FindsCrossFileCallsInVbWorkspace()
    {
        string root = CreateWorkspaceRoot("invocations");
        string projectPath = Path.Combine(root, "TargetHarness.vbproj");
        string servicePath = Path.Combine(root, "Service.vb");
        string consumerPath = Path.Combine(root, "Consumer.vb");
        string programPath = Path.Combine(root, "Program.vb");

        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OptionStrict>On</OptionStrict>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            servicePath,
            """
            Public Class Service
                Public Function Transform(value As Integer) As Integer
                    Return value * 2
                End Function
            End Class
            """);
        await File.WriteAllTextAsync(
            consumerPath,
            """
            Public Class Consumer
                Public Function Use(service As Service) As Integer
                    Return service.Transform(2)
                End Function
            End Class
            """);
        await File.WriteAllTextAsync(
            programPath,
            """
            Public Module Program
                Public Function Main() As Integer
                    Dim service As New Service()
                    Return service.Transform(3)
                End Function
            End Module
            """);

        try
        {
            FindInvocationsCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = servicePath,
                line = 2,
                column = 25,
                workspace_path = projectPath,
                require_workspace = true,
                brief = true,
                max_results = 20,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_matches\":2", json);
            Assert.Contains("Consumer.vb", json);
            Assert.Contains("Program.vb", json);
            Assert.Contains("\"workspace_context\":{\"mode\":\"workspace\"", json);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CallHierarchyCommand_ReturnsEdgesInVbWorkspace()
    {
        string root = CreateWorkspaceRoot("call-hierarchy");
        string projectPath = Path.Combine(root, "TargetHarness.vbproj");
        string servicePath = Path.Combine(root, "Service.vb");
        string callerPath = Path.Combine(root, "Caller.vb");

        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            servicePath,
            """
            Public Class Service
                Public Function Step2(value As Integer) As Integer
                    Return Step3(value) + 1
                End Function

                Public Function Step3(value As Integer) As Integer
                    Return value * 2
                End Function
            End Class
            """);
        await File.WriteAllTextAsync(
            callerPath,
            """
            Public Class Caller
                Public Function Run(service As Service) As Integer
                    Return service.Step2(2)
                End Function
            End Class
            """);

        try
        {
            CallHierarchyCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = servicePath,
                line = 2,
                column = 25,
                workspace_path = projectPath,
                require_workspace = true,
                direction = "both",
                max_depth = 2,
                brief = true,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_edges\":", json);
            Assert.Contains("Caller.vb", json);
            Assert.Contains("Service.vb", json);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FindImplementationsAndOverrides_WorkForVbMembers()
    {
        string filePath = WriteTempFile(
            """
            Public MustInherit Class BaseType
                Public MustOverride Function Compute(value As Integer) As Integer
            End Class

            Public Class DerivedType
                Inherits BaseType
                Public Overrides Function Compute(value As Integer) As Integer
                    Return value + 1
                End Function
            End Class
            """,
            ".vb");

        try
        {
            FindImplementationsCommand implementationsCommand = new();
            JsonElement implementationsInput = ToJsonElement(new
            {
                file_path = filePath,
                line = 2,
                column = 38,
                max_results = 10,
            });

            CommandExecutionResult implementationsResult = await implementationsCommand.ExecuteAsync(implementationsInput, CancellationToken.None);
            Assert.True(implementationsResult.Ok);
            string implementationsJson = JsonSerializer.Serialize(implementationsResult.Data);
            Assert.Contains("\"total_matches\":", implementationsJson);

            FindOverridesCommand overridesCommand = new();
            JsonElement overridesInput = ToJsonElement(new
            {
                file_path = filePath,
                line = 2,
                column = 38,
                max_results = 10,
            });

            CommandExecutionResult overridesResult = await overridesCommand.ExecuteAsync(overridesInput, CancellationToken.None);
            Assert.True(overridesResult.Ok);
            string overridesJson = JsonSerializer.Serialize(overridesResult.Data);
            Assert.Contains("\"total_matches\":", overridesJson);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task StaticAnalysisWorkspace_LoadsMixedCSharpAndVbSources()
    {
        string root = CreateWorkspaceRoot("mixed");
        string csPath = Path.Combine(root, "Alpha.cs");
        string vbPath = Path.Combine(root, "Beta.vb");

        await File.WriteAllTextAsync(csPath, "public sealed class Alpha { public int M() => 1; }");
        await File.WriteAllTextAsync(vbPath, "Public Class Beta\n    Public Function N() As Integer\n        Return 2\n    End Function\nEnd Class");

        try
        {
            (StaticAnalysisWorkspace? workspace, CommandError? error) result = await StaticAnalysisWorkspace.LoadAsync(
                workspacePath: root,
                includeGenerated: false,
                maxFiles: 100,
                cancellationToken: CancellationToken.None);

            Assert.Null(result.error);
            Assert.NotNull(result.workspace);
            Assert.Equal(2, result.workspace!.SyntaxTrees.Count);
            Assert.Contains(result.workspace.SyntaxTrees, tree => string.Equals(tree.Options.Language, LanguageNames.CSharp, StringComparison.Ordinal));
            Assert.Contains(result.workspace.SyntaxTrees, tree => string.Equals(tree.Options.Language, LanguageNames.VisualBasic, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string CreateWorkspaceRoot(string suffix)
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-vb-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteTempFile(string contents, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"roslynskills-vb-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, contents);
        return path;
    }

    private static JsonElement ToJsonElement(object value)
    {
        string json = JsonSerializer.Serialize(value);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
