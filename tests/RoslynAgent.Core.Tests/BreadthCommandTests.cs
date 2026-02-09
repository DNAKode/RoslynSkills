using RoslynAgent.Core.Commands;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Tests;

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
