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
    public async Task CfgCommand_ReturnsBlockAndEdgeSummaryInVb()
    {
        string filePath = WriteTempFile(
            """
            Public Class FlowSample
                Public Function Compute(value As Integer) As Integer
                    If value > 0 Then
                        Return value + 1
                    End If

                    Return value - 1
                End Function
            End Class
            """,
            ".vb");

        try
        {
            CfgCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 2,
                column = 25,
                brief = true,
                max_blocks = 50,
                max_edges = 100,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"cfg_summary\":", json);
            Assert.Contains("\"total_blocks\":", json);
            Assert.Contains("\"total_edges\":", json);
            Assert.Contains("\"language\":\"Visual Basic\"", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DataflowSliceCommand_ReturnsDataFlowSetsInVb()
    {
        string filePath = WriteTempFile(
            """
            Public Class DataflowSample
                Public Function Compute(input As Integer) As Integer
                    Dim value As Integer = input
                    If value > 0 Then
                        value = value + 1
                    End If

                    Return value
                End Function
            End Class
            """,
            ".vb");

        try
        {
            DataflowSliceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 5,
                column = 25,
                brief = true,
                max_symbols = 50,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"dataflow\":", json);
            Assert.Contains("\"read_inside\":", json);
            Assert.Contains("\"written_inside\":", json);
            Assert.Contains("\"anchor_symbol\":", json);
            Assert.Contains("value", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task FileOutlineCommand_ReturnsTypeAndMemberStructureInVb()
    {
        string filePath = WriteTempFile(
            """
            Imports System

            Namespace Demo.Tools
                Public Class Worker
                    Public Property Value As Integer

                    Public Sub Run()
                        Console.WriteLine(Value)
                    End Sub
                End Class
            End Namespace
            """,
            ".vb");

        try
        {
            FileOutlineCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"type_name\":\"Worker\"", json);
            Assert.Contains("\"member_name\":\"Run\"", json);
            Assert.Contains("\"using_count\":1", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_ReturnsBodySnippetForAnchoredVbMethod()
    {
        string filePath = WriteTempFile(
            """
            Public Class Calculator
                Public Function Add(left As Integer, right As Integer) As Integer
                    Return left + right
                End Function
            End Class
            """,
            ".vb");

        try
        {
            MemberSourceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 2,
                column = 25,
                mode = "body",
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"member_name\":\"Add\"", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_BriefMode_OmitsSourceTextByDefaultInVb()
    {
        string filePath = WriteTempFile(
            """
            Public Class Calculator
                Public Function Add(left As Integer, right As Integer) As Integer
                    Return left + right
                End Function
            End Class
            """,
            ".vb");

        try
        {
            MemberSourceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 2,
                column = 25,
                mode = "body",
                brief = true,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement query = doc.RootElement.GetProperty("query");
            Assert.True(query.GetProperty("brief").GetBoolean());
            Assert.False(query.GetProperty("include_source_text").GetBoolean());

            JsonElement source = doc.RootElement.GetProperty("source");
            Assert.True(source.GetProperty("omitted").GetBoolean());
            Assert.False(source.TryGetProperty("text", out _));
        }
        finally
        {
            File.Delete(filePath);
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

    [Fact]
    public async Task StaticAnalysisWorkspace_LoadsSlnxAsMsBuildSolution()
    {
        string root = CreateWorkspaceRoot("slnx");
        (string solutionPath, string appPath, _) = await CreateTwoProjectSlnxWorkspaceAsync(root);

        try
        {
            (StaticAnalysisWorkspace? workspace, CommandError? error) result = await StaticAnalysisWorkspace.LoadAsync(
                workspacePath: solutionPath,
                includeGenerated: false,
                maxFiles: 100,
                cancellationToken: CancellationToken.None);

            Assert.Null(result.error);
            Assert.NotNull(result.workspace);
            Assert.Equal("msbuild_solution", result.workspace!.AnalysisMode);
            Assert.Equal("slnx", result.workspace.WorkspaceKind);
            Assert.Equal(Path.GetFullPath(solutionPath), result.workspace.ResolvedWorkspacePath);
            Assert.True(result.workspace.ProjectCount >= 2);
            Assert.True(result.workspace.DocumentCount >= 2);
            Assert.Contains(result.workspace.SyntaxTrees, tree => Path.GetFullPath(tree.FilePath) == Path.GetFullPath(appPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UnusedPrivateSymbolsCommand_WithSlnxReportsMsBuildSolutionMode()
    {
        string root = CreateWorkspaceRoot("unused-slnx");
        (string solutionPath, _, string libPath) = await CreateTwoProjectSlnxWorkspaceAsync(root);

        try
        {
            UnusedPrivateSymbolsCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                workspace_path = solutionPath,
                brief = true,
                max_symbols = 20,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement scope = doc.RootElement.GetProperty("analysis_scope");
            Assert.Equal("msbuild_solution", scope.GetProperty("analysis_mode").GetString());
            Assert.Equal("slnx", scope.GetProperty("workspace_kind").GetString());
            Assert.Equal(Path.GetFullPath(solutionPath), scope.GetProperty("resolved_workspace_path").GetString());
            Assert.True(scope.GetProperty("project_count").GetInt32() >= 2);
            Assert.True(scope.GetProperty("document_count").GetInt32() >= 2);
            Assert.Contains("Lib", await File.ReadAllTextAsync(libPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceLifecycleCommands_PreloadSlnxAsHotSolution()
    {
        string root = CreateWorkspaceRoot("hot-slnx");
        (string solutionPath, string appPath, _) = await CreateTwoProjectSlnxWorkspaceAsync(root);
        string? handle = null;

        try
        {
            WorkspacePreloadCommand preload = new();
            JsonElement preloadInput = ToJsonElement(new
            {
                workspace_path = solutionPath,
                require_solution = true,
                max_files = 100,
            });

            CommandExecutionResult preloadResult = await preload.ExecuteAsync(preloadInput, CancellationToken.None);

            Assert.True(preloadResult.Ok);
            using JsonDocument preloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(preloadResult.Data));
            JsonElement preloadRoot = preloadDoc.RootElement;
            handle = preloadRoot.GetProperty("workspace_handle").GetString();
            Assert.False(string.IsNullOrWhiteSpace(handle));
            Assert.Equal("msbuild_solution", preloadRoot.GetProperty("analysis_mode").GetString());
            Assert.Equal("slnx", preloadRoot.GetProperty("workspace_kind").GetString());
            Assert.True(preloadRoot.GetProperty("solution_scoped").GetBoolean());
            Assert.True(preloadRoot.GetProperty("projects_loaded").GetInt32() >= 2);
            Assert.True(preloadRoot.GetProperty("documents_loaded").GetInt32() >= 2);

            WorkspaceStatusCommand status = new();
            CommandExecutionResult statusResult = await status.ExecuteAsync(
                ToJsonElement(new { workspace_handle = handle }),
                CancellationToken.None);

            Assert.True(statusResult.Ok);
            using JsonDocument statusDoc = JsonDocument.Parse(JsonSerializer.Serialize(statusResult.Data));
            JsonElement statusRoot = statusDoc.RootElement;
            Assert.Equal(handle, statusRoot.GetProperty("workspace_handle").GetString());
            Assert.True(statusRoot.GetProperty("loaded").GetBoolean());
            Assert.False(statusRoot.GetProperty("dirty").GetBoolean());
            Assert.Equal("slnx", statusRoot.GetProperty("workspace_kind").GetString());

            FindSymbolCommand findSymbol = new();
            CommandExecutionResult symbolResult = await findSymbol.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = appPath,
                    symbol_name = "Program",
                    workspace_handle = handle,
                    require_workspace = true,
                    brief = true,
                }),
                CancellationToken.None);

            Assert.True(symbolResult.Ok);
            using JsonDocument symbolDoc = JsonDocument.Parse(JsonSerializer.Serialize(symbolResult.Data));
            JsonElement symbolContext = symbolDoc.RootElement.GetProperty("query").GetProperty("workspace_context");
            Assert.Equal("workspace", symbolContext.GetProperty("mode").GetString());
            Assert.Equal("workspace_handle", symbolContext.GetProperty("resolution_source").GetString());
            Assert.Equal("process_hot", symbolContext.GetProperty("workspace_cache_mode").GetString());
            Assert.True(symbolContext.GetProperty("workspace_cache_hit").GetBoolean());
            Assert.Equal(handle, symbolContext.GetProperty("workspace_handle").GetString());

            GetFileDiagnosticsCommand diagnostics = new();
            CommandExecutionResult diagnosticsResult = await diagnostics.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = appPath,
                    workspace_handle = handle,
                    require_workspace = true,
                }),
                CancellationToken.None);

            Assert.True(diagnosticsResult.Ok);
            using JsonDocument diagnosticsDoc = JsonDocument.Parse(JsonSerializer.Serialize(diagnosticsResult.Data));
            JsonElement diagnosticsContext = diagnosticsDoc.RootElement.GetProperty("workspace_context");
            Assert.Equal("workspace_handle", diagnosticsContext.GetProperty("resolution_source").GetString());
            Assert.Equal(handle, diagnosticsContext.GetProperty("workspace_handle").GetString());

            QueryBatchCommand batch = new();
            CommandExecutionResult batchResult = await batch.ExecuteAsync(
                ToJsonElement(new
                {
                    workspace_handle = handle,
                    queries = new object[]
                    {
                        new
                        {
                            command_id = "nav.find_symbol",
                            input = new
                            {
                                file_path = appPath,
                                symbol_name = "Helper",
                                require_workspace = true,
                                brief = true,
                            },
                        },
                        new
                        {
                            command_id = "diag.get_file_diagnostics",
                            input = new
                            {
                                file_path = appPath,
                                require_workspace = true,
                            },
                        },
                    },
                }),
                CancellationToken.None);

            Assert.True(batchResult.Ok);
            using JsonDocument batchDoc = JsonDocument.Parse(JsonSerializer.Serialize(batchResult.Data));
            Assert.Equal(handle, batchDoc.RootElement.GetProperty("query").GetProperty("workspace_handle").GetString());
            Assert.Equal(2, batchDoc.RootElement.GetProperty("succeeded").GetInt32());

            WorkspaceCloseCommand close = new();
            CommandExecutionResult closeResult = await close.ExecuteAsync(
                ToJsonElement(new { workspace_handle = handle }),
                CancellationToken.None);

            Assert.True(closeResult.Ok);
            handle = null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(handle))
            {
                await new WorkspaceCloseCommand().ExecuteAsync(
                    ToJsonElement(new { workspace_handle = handle }),
                    CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceRefreshCommand_ClassifiesSourceAndStructuralChanges()
    {
        string root = CreateWorkspaceRoot("hot-refresh-classify");
        (string solutionPath, string appPath, _) = await CreateTwoProjectSlnxWorkspaceAsync(root);
        string projectPath = Path.Combine(Path.GetDirectoryName(appPath)!, "App.csproj");
        string? sourceHandle = null;
        string? structuralHandle = null;

        try
        {
            WorkspacePreloadCommand preload = new();
            CommandExecutionResult sourcePreload = await preload.ExecuteAsync(
                ToJsonElement(new
                {
                    workspace_path = solutionPath,
                    require_solution = true,
                    max_files = 100,
                }),
                CancellationToken.None);
            Assert.True(sourcePreload.Ok);
            sourceHandle = GetWorkspaceHandle(sourcePreload);

            await File.AppendAllTextAsync(appPath, Environment.NewLine + "public class AddedSourceMarker { }");

            WorkspaceRefreshCommand refresh = new();
            CommandExecutionResult sourceRefresh = await refresh.ExecuteAsync(
                ToJsonElement(new { workspace_handle = sourceHandle }),
                CancellationToken.None);

            Assert.True(sourceRefresh.Ok);
            using JsonDocument sourceDoc = JsonDocument.Parse(JsonSerializer.Serialize(sourceRefresh.Data));
            JsonElement sourceRoot = sourceDoc.RootElement;
            Assert.True(sourceRoot.GetProperty("dirty_before").GetBoolean());
            Assert.False(sourceRoot.GetProperty("dirty_after").GetBoolean());
            Assert.False(sourceRoot.GetProperty("dirty").GetBoolean());
            Assert.Equal("incremental_document_update", sourceRoot.GetProperty("refresh_action").GetString());
            Assert.False(sourceRoot.GetProperty("requires_reload").GetBoolean());
            Assert.Contains(Path.GetFullPath(appPath), sourceRoot.GetProperty("updated_paths").EnumerateArray().Select(item => item.GetString()));

            FindSymbolCommand findAdded = new();
            CommandExecutionResult addedSymbol = await findAdded.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = appPath,
                    symbol_name = "AddedSourceMarker",
                    workspace_handle = sourceHandle,
                    require_workspace = true,
                    brief = true,
                }),
                CancellationToken.None);

            Assert.True(addedSymbol.Ok);
            using JsonDocument addedDoc = JsonDocument.Parse(JsonSerializer.Serialize(addedSymbol.Data));
            Assert.True(addedDoc.RootElement.GetProperty("total_matches").GetInt32() >= 1);

            await new WorkspaceCloseCommand().ExecuteAsync(
                ToJsonElement(new { workspace_handle = sourceHandle }),
                CancellationToken.None);
            sourceHandle = null;

            CommandExecutionResult structuralPreload = await preload.ExecuteAsync(
                ToJsonElement(new
                {
                    workspace_path = solutionPath,
                    require_solution = true,
                    max_files = 100,
                }),
                CancellationToken.None);
            Assert.True(structuralPreload.Ok);
            structuralHandle = GetWorkspaceHandle(structuralPreload);

            await File.AppendAllTextAsync(projectPath, Environment.NewLine + "<!-- structure changed -->");

            CommandExecutionResult structuralRefresh = await refresh.ExecuteAsync(
                ToJsonElement(new { workspace_handle = structuralHandle }),
                CancellationToken.None);

            Assert.True(structuralRefresh.Ok);
            using JsonDocument structuralDoc = JsonDocument.Parse(JsonSerializer.Serialize(structuralRefresh.Data));
            JsonElement structuralRoot = structuralDoc.RootElement;
            Assert.True(structuralRoot.GetProperty("dirty").GetBoolean());
            Assert.False(structuralRoot.GetProperty("can_incrementally_update").GetBoolean());
            Assert.True(structuralRoot.GetProperty("requires_reload").GetBoolean());
            Assert.Contains("project_structure_change", structuralRoot.GetProperty("dirty_kinds").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sourceHandle))
            {
                await new WorkspaceCloseCommand().ExecuteAsync(
                    ToJsonElement(new { workspace_handle = sourceHandle }),
                    CancellationToken.None);
            }

            if (!string.IsNullOrWhiteSpace(structuralHandle))
            {
                await new WorkspaceCloseCommand().ExecuteAsync(
                    ToJsonElement(new { workspace_handle = structuralHandle }),
                    CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceRefreshCommand_ClassifiesNewSourceFileFromWatcherAsMembershipChange()
    {
        string root = CreateWorkspaceRoot("hot-refresh-watcher");
        (string solutionPath, string appPath, _) = await CreateTwoProjectSlnxWorkspaceAsync(root);
        string newSourcePath = Path.Combine(Path.GetDirectoryName(appPath)!, "NewMember.cs");
        string? handle = null;

        try
        {
            WorkspacePreloadCommand preload = new();
            CommandExecutionResult preloadResult = await preload.ExecuteAsync(
                ToJsonElement(new
                {
                    workspace_path = solutionPath,
                    require_solution = true,
                    max_files = 100,
                }),
                CancellationToken.None);
            Assert.True(preloadResult.Ok);
            handle = GetWorkspaceHandle(preloadResult);

            await File.WriteAllTextAsync(newSourcePath, "public class NewMember { }");

            WorkspaceRefreshCommand refresh = new();
            JsonElement refreshRoot = await WaitForDirtyKindAsync(
                refresh,
                handle,
                "unknown_or_membership_change");

            Assert.True(refreshRoot.GetProperty("dirty").GetBoolean());
            Assert.False(refreshRoot.GetProperty("can_incrementally_update").GetBoolean());
            Assert.True(refreshRoot.GetProperty("requires_reload").GetBoolean());
            Assert.Contains(
                Path.GetFullPath(newSourcePath),
                refreshRoot.GetProperty("invalidated_paths").EnumerateArray().Select(item => item.GetString()));

            CommandExecutionResult strictRefresh = await refresh.ExecuteAsync(
                ToJsonElement(new { workspace_handle = handle, mode = "strict" }),
                CancellationToken.None);

            Assert.True(strictRefresh.Ok);
            using JsonDocument strictDoc = JsonDocument.Parse(JsonSerializer.Serialize(strictRefresh.Data));
            JsonElement strictRoot = strictDoc.RootElement;
            Assert.Equal("reload", strictRoot.GetProperty("refresh_action").GetString());
            Assert.True(strictRoot.GetProperty("dirty_before").GetBoolean());
            Assert.False(strictRoot.GetProperty("dirty_after").GetBoolean());
            Assert.False(strictRoot.GetProperty("requires_reload").GetBoolean());

            FindSymbolCommand findNewMember = new();
            CommandExecutionResult symbolResult = await findNewMember.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = newSourcePath,
                    symbol_name = "NewMember",
                    workspace_handle = handle,
                    require_workspace = true,
                    brief = true,
                }),
                CancellationToken.None);

            Assert.True(symbolResult.Ok);
            using JsonDocument symbolDoc = JsonDocument.Parse(JsonSerializer.Serialize(symbolResult.Data));
            Assert.True(symbolDoc.RootElement.GetProperty("total_matches").GetInt32() >= 1);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(handle))
            {
                await new WorkspaceCloseCommand().ExecuteAsync(
                    ToJsonElement(new { workspace_handle = handle }),
                    CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceRefreshCommand_IgnoresClientStateDirectory()
    {
        string root = CreateWorkspaceRoot("hot-refresh-client-state");
        (string solutionPath, _, _) = await CreateTwoProjectSlnxWorkspaceAsync(root);
        string? handle = null;

        try
        {
            WorkspacePreloadCommand preload = new();
            CommandExecutionResult preloadResult = await preload.ExecuteAsync(
                ToJsonElement(new
                {
                    workspace_path = solutionPath,
                    require_solution = true,
                    max_files = 100,
                }),
                CancellationToken.None);
            Assert.True(preloadResult.Ok);
            handle = GetWorkspaceHandle(preloadResult);

            string clientStateDirectory = Path.Combine(root, ".roslynskills");
            Directory.CreateDirectory(clientStateDirectory);
            await File.WriteAllTextAsync(Path.Combine(clientStateDirectory, "workspaces.json"), "{}");
            await Task.Delay(250);

            WorkspaceRefreshCommand refresh = new();
            CommandExecutionResult result = await refresh.ExecuteAsync(
                ToJsonElement(new { workspace_handle = handle }),
                CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement rootElement = doc.RootElement;
            Assert.False(rootElement.GetProperty("dirty").GetBoolean());
            Assert.Empty(rootElement.GetProperty("invalidated_paths").EnumerateArray());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(handle))
            {
                await new WorkspaceCloseCommand().ExecuteAsync(
                    ToJsonElement(new { workspace_handle = handle }),
                    CancellationToken.None);
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspacePreloadCommand_RequireSolutionRejectsProjectScope()
    {
        string root = CreateWorkspaceRoot("hot-project-reject");
        (string _, string appPath, _) = await CreateTwoProjectSlnxWorkspaceAsync(root);
        string projectPath = Path.Combine(Path.GetDirectoryName(appPath)!, "App.csproj");

        try
        {
            WorkspacePreloadCommand preload = new();
            CommandExecutionResult result = await preload.ExecuteAsync(
                ToJsonElement(new
                {
                    workspace_path = projectPath,
                    require_solution = true,
                    max_files = 100,
                }),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains(result.Errors, error => error.Code == "solution_required");
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

    private static string GetWorkspaceHandle(CommandExecutionResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        return doc.RootElement.GetProperty("workspace_handle").GetString()!;
    }

    private static async Task<JsonElement> WaitForDirtyKindAsync(
        WorkspaceRefreshCommand refresh,
        string handle,
        string dirtyKind)
    {
        for (int i = 0; i < 25; i++)
        {
            CommandExecutionResult result = await refresh.ExecuteAsync(
                ToJsonElement(new { workspace_handle = handle }),
                CancellationToken.None);
            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement root = doc.RootElement.Clone();
            if (root.GetProperty("dirty_kinds").EnumerateArray().Any(item => string.Equals(item.GetString(), dirtyKind, StringComparison.Ordinal)))
            {
                return root;
            }

            await Task.Delay(100);
        }

        CommandExecutionResult finalResult = await refresh.ExecuteAsync(
            ToJsonElement(new { workspace_handle = handle }),
            CancellationToken.None);
        using JsonDocument finalDoc = JsonDocument.Parse(JsonSerializer.Serialize(finalResult.Data));
        return finalDoc.RootElement.Clone();
    }

    private static async Task<(string solutionPath, string appPath, string libPath)> CreateTwoProjectSlnxWorkspaceAsync(string root)
    {
        string appDirectory = Path.Combine(root, "App");
        string libDirectory = Path.Combine(root, "Lib");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(libDirectory);

        string appProjectPath = Path.Combine(appDirectory, "App.csproj");
        string libProjectPath = Path.Combine(libDirectory, "Lib.csproj");
        await File.WriteAllTextAsync(
            appProjectPath,
            """
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
            """);
        await File.WriteAllTextAsync(
            libProjectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        string appPath = Path.Combine(appDirectory, "Program.cs");
        string libPath = Path.Combine(libDirectory, "Helper.cs");
        await File.WriteAllTextAsync(
            appPath,
            """
            using Lib;

            public sealed class Program
            {
                public string Run() => Helper.Name;
            }
            """);
        await File.WriteAllTextAsync(
            libPath,
            """
            namespace Lib;

            public static class Helper
            {
                public static string Name => "Lib";
            }
            """);

        string solutionPath = Path.Combine(root, "Host.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """);

        return (solutionPath, appPath, libPath);
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
