using RoslynSkills.Core.Commands;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Tests;

public sealed class BreadthCommandTests
{
    [Fact]
    public async Task FindReferencesCommand_FindsOverloadSpecificReferences()
    {
        string filePath = WriteTempFile(
            """
            public class Overloads
            {
                public void Process(int value) { }
                public void Process(string value) { }
                public void Execute()
                {
                    Process(1);
                    Process("x");
                }
            }
            """);

        try
        {
            FindReferencesCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 3,
                column = 17,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_matches\":2", json);
            Assert.Contains("\"is_declaration\":true", json);
            Assert.Contains("\"is_declaration\":false", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SearchTextCommand_FindsLiteralMatchesAcrossScopedRoots()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-search-{Guid.NewGuid():N}");
        string sourceDir = Path.Combine(root, "src");
        string objDir = Path.Combine(root, "obj");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(objDir);

        string firstPath = Path.Combine(sourceDir, "First.cs");
        string secondPath = Path.Combine(sourceDir, "Second.cs");
        string generatedPath = Path.Combine(objDir, "Generated.g.cs");

        await File.WriteAllTextAsync(firstPath, "public class First { public string Name => \"RemoteUserAction\"; }");
        await File.WriteAllTextAsync(secondPath, "public class Second { public string Name => \"ReplicationUpdate\"; }");
        await File.WriteAllTextAsync(generatedPath, "public class Generated { public string Name => \"RemoteUserAction\"; }");

        try
        {
            SearchTextCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                patterns = new[] { "RemoteUserAction", "ReplicationUpdate" },
                mode = "literal",
                roots = new[] { root },
                max_results = 50,
                include_globs = new[] { "**/*.cs" },
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_matches\":2", json);
            Assert.Contains("First.cs", json);
            Assert.Contains("Second.cs", json);
            Assert.DoesNotContain("Generated.g.cs", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FindInvocationsCommand_FindsCrossFileCallsInWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-invocations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "TargetHarness.csproj");
        string servicePath = Path.Combine(root, "Service.cs");
        string consumerPath = Path.Combine(root, "Consumer.cs");
        string programPath = Path.Combine(root, "Program.cs");

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
            public class Service
            {
                public int Transform(int value)
                {
                    return value * 2;
                }
            }
            """);

        await File.WriteAllTextAsync(
            consumerPath,
            """
            public class Consumer
            {
                public int Use(Service service)
                {
                    return service.Transform(2);
                }
            }
            """);

        await File.WriteAllTextAsync(
            programPath,
            """
            public static class Program
            {
                public static int Main()
                {
                    var service = new Service();
                    return service.Transform(3);
                }
            }
            """);

        try
        {
            FindInvocationsCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = servicePath,
                line = 3,
                column = 16,
                workspace_path = projectPath,
                require_workspace = true,
                brief = true,
                max_results = 20,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_matches\":2", json);
            Assert.Contains("Consumer.cs", json);
            Assert.Contains("Program.cs", json);
            Assert.Contains("\"workspace_context\":{\"mode\":\"workspace\"", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task QueryBatchCommand_ExecutesMultipleQueries()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public int Add(int left, int right)
                {
                    return left + right;
                }

                public int Run()
                {
                    return Add(1, 2);
                }
            }
            """);

        try
        {
            QueryBatchCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                continue_on_error = true,
                queries = new object[]
                {
                    new
                    {
                        command_id = "ctx.search_text",
                        input = new
                        {
                            patterns = new[] { "Add(" },
                            mode = "literal",
                            file_path = filePath,
                        },
                    },
                    new
                    {
                        command_id = "nav.find_invocations",
                        input = new
                        {
                            file_path = filePath,
                            line = 3,
                            column = 16,
                            brief = true,
                            max_results = 10,
                        },
                    },
                },
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_executed\":2", json);
            Assert.Contains("\"succeeded\":2", json);
            Assert.Contains("\"command_id\":\"ctx.search_text\"", json);
            Assert.Contains("\"command_id\":\"nav.find_invocations\"", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task QueryBatchCommand_SupportsAnalyzeCommands()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-batch-analyze-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "BatchSample.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            public class BatchSample
            {
                private int _unused = 1;
            }
            """);

        try
        {
            QueryBatchCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                continue_on_error = true,
                queries = new object[]
                {
                    new
                    {
                        command_id = "analyze.unused_private_symbols",
                        input = new
                        {
                            workspace_path = root,
                            brief = true,
                            max_symbols = 20,
                        },
                    },
                    new
                    {
                        command_id = "analyze.async_risk_scan",
                        input = new
                        {
                            workspace_path = root,
                            brief = true,
                            max_findings = 20,
                        },
                    },
                },
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_executed\":2", json);
            Assert.Contains("\"command_id\":\"analyze.unused_private_symbols\"", json);
            Assert.Contains("\"command_id\":\"analyze.async_risk_scan\"", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CallHierarchyCommand_ReturnsIncomingAndOutgoingEdgesAcrossWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-call-hierarchy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "TargetHarness.csproj");
        string servicePath = Path.Combine(root, "Service.cs");
        string callerPath = Path.Combine(root, "Caller.cs");
        string programPath = Path.Combine(root, "Program.cs");

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
            public class Service
            {
                public int Step2(int value)
                {
                    return Step3(value) + 1;
                }

                public int Step3(int value)
                {
                    return value * 2;
                }
            }
            """);

        await File.WriteAllTextAsync(
            callerPath,
            """
            public class Caller
            {
                public int Run(Service service)
                {
                    return service.Step2(2);
                }
            }
            """);

        await File.WriteAllTextAsync(
            programPath,
            """
            public static class Program
            {
                public static int Main()
                {
                    return new Caller().Run(new Service());
                }
            }
            """);

        try
        {
            CallHierarchyCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = servicePath,
                line = 3,
                column = 16,
                direction = "both",
                max_depth = 2,
                max_nodes = 20,
                max_edges = 40,
                workspace_path = projectPath,
                require_workspace = true,
                brief = true,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_nodes\":", json);
            Assert.Contains("\"total_edges\":", json);
            Assert.Contains("Caller.Run", json);
            Assert.Contains("Service.Step3", json);
            Assert.Contains("\"direction\":\"both\"", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnusedPrivateSymbolsCommand_FindsLikelyUnusedMembers()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-unused-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "Sample.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            public class Sample
            {
                private int _unusedField = 1;
                private int _usedField = 2;

                private int UnusedMethod() => 42;
                private int UsedMethod() => _usedField;

                public int Run()
                {
                    return UsedMethod();
                }
            }
            """);

        try
        {
            UnusedPrivateSymbolsCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                workspace_path = root,
                brief = true,
                max_symbols = 50,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"unused_candidates\":", json);
            Assert.Contains("_unusedField", json);
            Assert.Contains("UnusedMethod", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DependencyViolationsCommand_FindsLayerDirectionViolations()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "LayerSample.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            namespace App.Web;
            public class WebType { }

            namespace App.Domain;
            public class DomainType
            {
                private readonly App.Web.WebType _badDependency = new();
            }
            """);

        try
        {
            DependencyViolationsCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                workspace_path = root,
                layers = new[] { "App.Web", "App.Application", "App.Domain" },
                direction = "toward_end",
                brief = true,
                max_violations = 20,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_violations\":", json);
            Assert.Contains("App.Domain", json);
            Assert.Contains("App.Web", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImpactSliceCommand_ReturnsCallersAndCallees()
    {
        string filePath = WriteTempFile(
            """
            public class SliceDemo
            {
                public int A(int value)
                {
                    return B(value) + 1;
                }

                public int B(int value) => value * 2;

                public int C()
                {
                    return A(2);
                }
            }
            """);

        try
        {
            ImpactSliceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 3,
                column = 16,
                include_references = true,
                include_callers = true,
                include_callees = true,
                include_overrides = false,
                include_implementations = false,
                brief = true,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"impact_counts\":", json);
            Assert.Contains("\"callers\":", json);
            Assert.Contains("\"callees\":", json);
            Assert.Contains("SliceDemo.C()", json);
            Assert.Contains("SliceDemo.B(int)", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task OverrideCoverageCommand_FindsLowCoverageVirtualMembers()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-override-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "CoverageSample.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            public abstract class BaseType
            {
                public abstract void Required();
                public virtual void Optional() { }
            }

            public class ChildOne : BaseType
            {
                public override void Required() { }
            }

            public class ChildTwo : BaseType
            {
                public override void Required() { }
                public override void Optional() { }
            }
            """);

        try
        {
            OverrideCoverageCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                workspace_path = root,
                coverage_threshold = 0.8,
                min_derived_types = 1,
                brief = true,
                max_members = 20,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"findings\":", json);
            Assert.Contains("Optional()", json);
            Assert.DoesNotContain("Required()", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AsyncRiskScanCommand_FindsBlockingPatterns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-async-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "AsyncSample.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            using System.Threading;
            using System.Threading.Tasks;

            public class AsyncSample
            {
                public async void BadHandler()
                {
                    await Task.Delay(1);
                }

                public int Blocking()
                {
                    Task.Delay(1).Wait();
                    int value = Task.FromResult(2).Result;
                    Thread.Sleep(1);
                    return value;
                }
            }
            """);

        try
        {
            AsyncRiskScanCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                workspace_path = root,
                brief = true,
                max_findings = 100,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_findings\":", json);
            Assert.Contains("\"async_void\"", json);
            Assert.Contains("\"task_wait\"", json);
            Assert.Contains("\"task_result\"", json);
            Assert.Contains("\"thread_sleep\"", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnusedPrivateSymbolsCommand_DoesNotFlagPrivateCtorUsedByTargetTypedNew()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-unused-ctor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "CtorSample.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            public sealed class CtorSample
            {
                private CtorSample() { }
                private int _unused = 1;

                public static CtorSample Create()
                {
                    CtorSample value = new();
                    return value;
                }
            }
            """);

        try
        {
            UnusedPrivateSymbolsCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                workspace_path = root,
                brief = true,
                max_symbols = 50,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("_unused", json);
            Assert.DoesNotContain("CtorSample.CtorSample()", json);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImpactSliceCommand_FindsCtorImpactForTargetTypedNew()
    {
        string filePath = WriteTempFile(
            """
            public sealed class ImpactCtor
            {
                private ImpactCtor() { }

                public static ImpactCtor Create()
                {
                    ImpactCtor value = new();
                    return value;
                }
            }
            """);

        try
        {
            ImpactSliceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 3,
                column = 20,
                include_references = true,
                include_callers = true,
                include_callees = false,
                include_overrides = false,
                include_implementations = false,
                brief = true,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement counts = doc.RootElement.GetProperty("impact_counts");
            int references = counts.GetProperty("references").GetInt32();
            int callers = counts.GetProperty("callers").GetInt32();
            Assert.True(references > 0 || callers > 0);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task FindImplementationsAndOverridesCommands_ReturnExpectedMembers()
    {
        string filePath = WriteTempFile(
            """
            public interface IRunner
            {
                void Run();
            }

            public class BaseRunner : IRunner
            {
                public virtual void Run() { }
            }

            public class DerivedRunner : BaseRunner
            {
                public override void Run() { }
            }
            """);

        try
        {
            FindImplementationsCommand implementations = new();
            JsonElement implementationInput = ToJsonElement(new
            {
                file_path = filePath,
                line = 3,
                column = 10,
            });

            CommandExecutionResult implementationResult = await implementations.ExecuteAsync(implementationInput, CancellationToken.None);
            Assert.True(implementationResult.Ok);
            string implementationJson = JsonSerializer.Serialize(implementationResult.Data);
            Assert.Contains("BaseRunner.Run()", implementationJson);

            FindOverridesCommand overrides = new();
            JsonElement overrideInput = ToJsonElement(new
            {
                file_path = filePath,
                line = 8,
                column = 25,
            });

            CommandExecutionResult overrideResult = await overrides.ExecuteAsync(overrideInput, CancellationToken.None);
            Assert.True(overrideResult.Ok);
            string overrideJson = JsonSerializer.Serialize(overrideResult.Data);
            Assert.Contains("DerivedRunner.Run()", overrideJson);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SymbolEnvelopeCommand_ReturnsStructuredEnvelope()
    {
        string filePath = WriteTempFile(
            """
            namespace Demo;
            public class Worker
            {
                public void Run()
                {
                    Run();
                }
            }
            """);

        try
        {
            SymbolEnvelopeCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 4,
                column = 17,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"display_name\":\"", json);
            Assert.Contains("Worker.Run", json);
            Assert.Contains("\"reference_count_hint\":2", json);
            Assert.Contains("\"namespace\":\"Demo\"", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task FileOutlineCommand_ReturnsTypeAndMemberStructure()
    {
        string filePath = WriteTempFile(
            """
            using System;

            namespace Demo.Tools;

            public class Worker
            {
                public int Value { get; set; }

                public void Run()
                {
                    Console.WriteLine(Value);
                }
            }
            """);

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
    public async Task ChangeSignatureAndAddMemberCommands_ApplyUpdates()
    {
        string filePath = WriteTempFile(
            """
            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);

        try
        {
            ChangeSignatureCommand changeSignature = new();
            JsonElement signatureInput = ToJsonElement(new
            {
                file_path = filePath,
                line = 3,
                column = 20,
                parameters = "int left, int right, int extra",
                return_type = "long",
                new_name = "Sum",
                apply = true,
            });

            CommandExecutionResult signatureResult = await changeSignature.ExecuteAsync(signatureInput, CancellationToken.None);
            Assert.True(signatureResult.Ok);

            AddMemberCommand addMember = new();
            JsonElement memberInput = ToJsonElement(new
            {
                file_path = filePath,
                member_declaration = "public int LastResult { get; set; }",
                apply = true,
            });

            CommandExecutionResult addMemberResult = await addMember.ExecuteAsync(memberInput, CancellationToken.None);
            Assert.True(addMemberResult.Ok);

            string updated = File.ReadAllText(filePath);
            Assert.Contains("public long Sum(int left, int right, int extra)", updated);
            Assert.Contains("public int LastResult { get; set; }", updated);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task UpdateUsingsAndApplyCodeFixCommands_AdjustUsingDirectives()
    {
        string updateUsingsPath = WriteTempFile(
            """
            using System;
            using System.Text;
            using System;

            public class Demo
            {
                public void Run()
                {
                    Console.WriteLine("ok");
                }
            }
            """);

        string applyFixPath = WriteTempFile(
            """
            using System;
            using System;

            public class Demo
            {
            }
            """);

        try
        {
            UpdateUsingsCommand updateUsings = new();
            JsonElement updateInput = ToJsonElement(new
            {
                file_path = updateUsingsPath,
                remove_unused = true,
                apply = true,
            });

            CommandExecutionResult updateResult = await updateUsings.ExecuteAsync(updateInput, CancellationToken.None);
            Assert.True(updateResult.Ok);

            ApplyCodeFixCommand applyCodeFix = new();
            JsonElement fixInput = ToJsonElement(new
            {
                file_path = applyFixPath,
                diagnostic_id = "CS0105",
                apply = true,
            });

            CommandExecutionResult fixResult = await applyCodeFix.ExecuteAsync(fixInput, CancellationToken.None);
            Assert.True(fixResult.Ok);

            string updateUsingsResult = File.ReadAllText(updateUsingsPath);
            Assert.DoesNotContain("using System.Text;", updateUsingsResult);

            string applyFixResult = File.ReadAllText(applyFixPath);
            Assert.Equal(1, CountOccurrences(applyFixResult, "using System;"));
        }
        finally
        {
            File.Delete(updateUsingsPath);
            File.Delete(applyFixPath);
        }
    }

    [Fact]
    public async Task DiagnosticsCommands_ReportDeltaAndSnapshot()
    {
        string beforePath = WriteTempFile(
            """
            public class Demo
            {
                public void Run() { }
            }
            """);

        string afterPath = WriteTempFile(
            """
            public class Demo
            {
                public void Run()
                {
                    int value = ;
                }
            }
            """);

        try
        {
            GetAfterEditDiagnosticsCommand afterEdit = new();
            JsonElement afterEditInput = ToJsonElement(new
            {
                file_path = beforePath,
                proposed_content = File.ReadAllText(afterPath),
            });

            CommandExecutionResult afterEditResult = await afterEdit.ExecuteAsync(afterEditInput, CancellationToken.None);
            Assert.True(afterEditResult.Ok);
            string afterEditJson = JsonSerializer.Serialize(afterEditResult.Data);
            Assert.Contains("\"introduced\":", afterEditJson);

            DiagnosticsDiffCommand diff = new();
            JsonElement diffInput = ToJsonElement(new
            {
                before_path = beforePath,
                after_path = afterPath,
            });

            CommandExecutionResult diffResult = await diff.ExecuteAsync(diffInput, CancellationToken.None);
            Assert.True(diffResult.Ok);
            string diffJson = JsonSerializer.Serialize(diffResult.Data);
            Assert.Contains("\"introduced_count\":", diffJson);

            GetSolutionSnapshotCommand snapshot = new();
            JsonElement snapshotInput = ToJsonElement(new
            {
                file_paths = new[] { beforePath, afterPath },
            });

            CommandExecutionResult snapshotResult = await snapshot.ExecuteAsync(snapshotInput, CancellationToken.None);
            Assert.True(snapshotResult.Ok);
            string snapshotJson = JsonSerializer.Serialize(snapshotResult.Data);
            Assert.Contains("\"total_files\":2", snapshotJson);
        }
        finally
        {
            File.Delete(beforePath);
            File.Delete(afterPath);
        }
    }

    [Fact]
    public async Task RepairCommands_ProposeAndApplyPlan()
    {
        string filePath = WriteTempFile(
            """
            using System;
            using System;

            public class Demo
            {
                public void Run()
                {
                    Console.WriteLine("ok");
                }
            }
            """);

        try
        {
            ProposeFromDiagnosticsCommand propose = new();
            JsonElement proposeInput = ToJsonElement(new { file_path = filePath });
            CommandExecutionResult proposeResult = await propose.ExecuteAsync(proposeInput, CancellationToken.None);
            Assert.True(proposeResult.Ok);
            string proposeJson = JsonSerializer.Serialize(proposeResult.Data);
            Assert.Contains("edit.apply_code_fix", proposeJson);

            ApplyRepairPlanCommand applyPlan = new();
            JsonElement planInput = ToJsonElement(new
            {
                file_path = filePath,
                steps = new object[]
                {
                    new
                    {
                        operation_id = "edit.apply_code_fix",
                        input = new
                        {
                            diagnostic_id = "CS0105",
                            apply = true,
                        },
                    },
                },
                stop_on_error = true,
            });

            CommandExecutionResult applyResult = await applyPlan.ExecuteAsync(planInput, CancellationToken.None);
            Assert.True(applyResult.Ok);

            string updated = File.ReadAllText(filePath);
            Assert.Equal(1, CountOccurrences(updated, "using System;"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task EditTransactionCommand_AppliesAcrossMultipleFiles()
    {
        string helperPath = WriteTempFile(
            """
            public static class Helper
            {
                public static int Value() => 1;
            }
            """);
        string consumerPath = WriteTempFile(
            """
            public static class Consumer
            {
                public static int Read() => Helper.Value();
            }
            """);

        try
        {
            EditTransactionCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                apply = true,
                operations = new object[]
                {
                    new
                    {
                        operation = "replace_span",
                        file_path = helperPath,
                        start_line = 3,
                        start_column = 23,
                        end_line = 3,
                        end_column = 28,
                        new_text = "CurrentValue",
                    },
                    new
                    {
                        operation = "replace_span",
                        file_path = consumerPath,
                        start_line = 3,
                        start_column = 40,
                        end_line = 3,
                        end_column = 45,
                        new_text = "CurrentValue",
                    },
                },
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);

            string helperUpdated = File.ReadAllText(helperPath);
            string consumerUpdated = File.ReadAllText(consumerPath);
            Assert.Contains("CurrentValue()", helperUpdated);
            Assert.Contains("Helper.CurrentValue()", consumerUpdated);

            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"changed_file_count\":2", json);
            Assert.Contains("\"errors\":0", json);
        }
        finally
        {
            File.Delete(helperPath);
            File.Delete(consumerPath);
        }
    }

    [Fact]
    public async Task RepairApplyPlanCommand_AcceptsEditTransactionStep()
    {
        string helperPath = WriteTempFile(
            """
            public static class Helper
            {
                public static int Value() => 1;
            }
            """);
        string consumerPath = WriteTempFile(
            """
            public static class Consumer
            {
                public static int Read() => Helper.Value();
            }
            """);

        try
        {
            ApplyRepairPlanCommand applyPlan = new();
            JsonElement input = ToJsonElement(new
            {
                steps = new object[]
                {
                    new
                    {
                        operation_id = "edit.transaction",
                        input = new
                        {
                            apply = true,
                            operations = new object[]
                            {
                                new
                                {
                                    operation = "replace_span",
                                    file_path = helperPath,
                                    start_line = 3,
                                    start_column = 23,
                                    end_line = 3,
                                    end_column = 28,
                                    new_text = "CurrentValue",
                                },
                                new
                                {
                                    operation = "replace_span",
                                    file_path = consumerPath,
                                    start_line = 3,
                                    start_column = 40,
                                    end_line = 3,
                                    end_column = 45,
                                    new_text = "CurrentValue",
                                },
                            },
                        },
                    },
                },
                stop_on_error = true,
            });

            CommandExecutionResult result = await applyPlan.ExecuteAsync(input, CancellationToken.None);
            Assert.True(result.Ok);
            Assert.Contains("CurrentValue()", File.ReadAllText(helperPath));
            Assert.Contains("Helper.CurrentValue()", File.ReadAllText(consumerPath));
        }
        finally
        {
            File.Delete(helperPath);
            File.Delete(consumerPath);
        }
    }

    private static string WriteTempFile(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"roslyn-agent-breadth-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, contents);
        return path;
    }

    private static JsonElement ToJsonElement(object value)
    {
        string json = JsonSerializer.Serialize(value);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while (true)
        {
            index = text.IndexOf(value, index, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            index += value.Length;
        }

        return count;
    }
}

