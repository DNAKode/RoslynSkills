using RoslynSkills.Core.Commands;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Tests;

public sealed class SessionAndExplorationCommandTests
{
    [Fact]
    public async Task MemberSourceCommand_ReturnsBodySnippetForAnchoredMethod()
    {
        string filePath = WriteTempFile(
            """
            public class Calculator
            {
                public int Add(int left, int right)
                {
                    return left + right;
                }
            }
            """);

        try
        {
            MemberSourceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 3,
                column = 22,
                mode = "body",
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"member_name\":\"Add\"", json);
            Assert.Contains("return left", json);
            Assert.Contains("right;", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_BriefMode_OmitsSourceTextByDefault()
    {
        string filePath = WriteTempFile(
            """
            public class Calculator
            {
                public int Add(int left, int right)
                {
                    return left + right;
                }
            }
            """);

        try
        {
            MemberSourceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 3,
                column = 22,
                mode = "body",
                brief = true,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement root = doc.RootElement;

            JsonElement query = root.GetProperty("query");
            Assert.True(query.GetProperty("brief").GetBoolean());
            Assert.False(query.GetProperty("include_source_text").GetBoolean());

            JsonElement source = root.GetProperty("source");
            Assert.True(source.GetProperty("omitted").GetBoolean());
            Assert.False(source.GetProperty("truncated").GetBoolean());
            Assert.Equal(0, source.GetProperty("character_count").GetInt32());
            Assert.False(source.TryGetProperty("text", out _));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SessionCommands_OpenSetDiffCommitAndClose()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public int Sum()
                {
                    return 1 + 2;
                }
            }
            """);
        string sessionId = $"test-{Guid.NewGuid():N}";

        try
        {
            SessionOpenCommand open = new();
            JsonElement openInput = ToJsonElement(new
            {
                file_path = filePath,
                session_id = sessionId,
            });

            CommandExecutionResult openResult = await open.ExecuteAsync(openInput, CancellationToken.None);
            Assert.True(openResult.Ok);

            SessionSetContentCommand setContent = new();
            JsonElement setInput = ToJsonElement(new
            {
                session_id = sessionId,
                new_content =
                """
                public class Demo
                {
                    public int Sum()
                    {
                        return 3 + 4;
                    }
                }
                """,
            });

            CommandExecutionResult setResult = await setContent.ExecuteAsync(setInput, CancellationToken.None);
            Assert.True(setResult.Ok);
            string setJson = JsonSerializer.Serialize(setResult.Data);
            Assert.Contains("\"changed\":true", setJson);

            SessionDiffCommand diff = new();
            JsonElement diffInput = ToJsonElement(new
            {
                session_id = sessionId,
            });

            CommandExecutionResult diffResult = await diff.ExecuteAsync(diffInput, CancellationToken.None);
            Assert.True(diffResult.Ok);
            string diffJson = JsonSerializer.Serialize(diffResult.Data);
            Assert.Contains("\"total_changed_lines\":1", diffJson);
            Assert.Contains("return 1", diffJson);
            Assert.Contains("return 3", diffJson);

            SessionCommitCommand commit = new();
            JsonElement commitInput = ToJsonElement(new
            {
                session_id = sessionId,
                keep_session = true,
            });

            CommandExecutionResult commitResult = await commit.ExecuteAsync(commitInput, CancellationToken.None);
            Assert.True(commitResult.Ok);
            Assert.Contains("return 3 + 4;", File.ReadAllText(filePath));

            SessionCloseCommand close = new();
            JsonElement closeInput = ToJsonElement(new
            {
                session_id = sessionId,
            });

            CommandExecutionResult closeResult = await close.ExecuteAsync(closeInput, CancellationToken.None);
            Assert.True(closeResult.Ok);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SessionSetContentCommand_ReturnsGenerationConflictWhenExpectedGenerationIsStale()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public int Sum() => 1;
            }
            """);
        string sessionId = $"gen-{Guid.NewGuid():N}";

        try
        {
            SessionOpenCommand open = new();
            CommandExecutionResult openResult = await open.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = filePath,
                    session_id = sessionId,
                }),
                CancellationToken.None);
            Assert.True(openResult.Ok);

            SessionSetContentCommand setContent = new();
            CommandExecutionResult firstUpdate = await setContent.ExecuteAsync(
                ToJsonElement(new
                {
                    session_id = sessionId,
                    expected_generation = 0,
                    new_content =
                    """
                    public class Demo
                    {
                        public int Sum() => 2;
                    }
                    """,
                }),
                CancellationToken.None);
            Assert.True(firstUpdate.Ok);

            CommandExecutionResult staleUpdate = await setContent.ExecuteAsync(
                ToJsonElement(new
                {
                    session_id = sessionId,
                    expected_generation = 0,
                    new_content =
                    """
                    public class Demo
                    {
                        public int Sum() => 3;
                    }
                    """,
                }),
                CancellationToken.None);
            Assert.False(staleUpdate.Ok);
            Assert.Contains(staleUpdate.Errors, e => e.Code == "generation_conflict");
        }
        finally
        {
            SessionCloseCommand close = new();
            await close.ExecuteAsync(ToJsonElement(new { session_id = sessionId }), CancellationToken.None);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SessionApplyTextEditsCommand_UpdatesSessionAndGuardsGeneration()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public int Sum()
                {
                    return 1 + 2;
                }
            }
            """);
        string sessionId = $"edits-{Guid.NewGuid():N}";

        try
        {
            SessionOpenCommand open = new();
            CommandExecutionResult openResult = await open.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = filePath,
                    session_id = sessionId,
                }),
                CancellationToken.None);
            Assert.True(openResult.Ok);

            SessionApplyTextEditsCommand apply = new();
            CommandExecutionResult applyResult = await apply.ExecuteAsync(
                ToJsonElement(new
                {
                    session_id = sessionId,
                    expected_generation = 0,
                    edits = new object[]
                    {
                        new
                        {
                            start_line = 5,
                            start_column = 16,
                            end_line = 5,
                            end_column = 21,
                            new_text = "3 + 4",
                        },
                    },
                }),
                CancellationToken.None);
            Assert.True(applyResult.Ok);
            string applyJson = JsonSerializer.Serialize(applyResult.Data);
            Assert.Contains("\"changed\":true", applyJson);
            Assert.Contains("\"generation\":1", applyJson);
            Assert.Contains("\"changed_lines\":[5]", applyJson);

            CommandExecutionResult staleApplyResult = await apply.ExecuteAsync(
                ToJsonElement(new
                {
                    session_id = sessionId,
                    expected_generation = 0,
                    edits = new object[]
                    {
                        new
                        {
                            start_line = 5,
                            start_column = 16,
                            end_line = 5,
                            end_column = 21,
                            new_text = "5 + 6",
                        },
                    },
                }),
                CancellationToken.None);
            Assert.False(staleApplyResult.Ok);
            Assert.Contains(staleApplyResult.Errors, e => e.Code == "generation_conflict");

            SessionCommitCommand commit = new();
            CommandExecutionResult commitResult = await commit.ExecuteAsync(
                ToJsonElement(new
                {
                    session_id = sessionId,
                    keep_session = true,
                }),
                CancellationToken.None);
            Assert.True(commitResult.Ok);
            Assert.Contains("return 3 + 4;", File.ReadAllText(filePath));
        }
        finally
        {
            SessionCloseCommand close = new();
            await close.ExecuteAsync(ToJsonElement(new { session_id = sessionId }), CancellationToken.None);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SessionStatusCommand_DetectsExternalDiskChange()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public int Sum() => 1;
            }
            """);
        string sessionId = $"status-{Guid.NewGuid():N}";

        try
        {
            SessionOpenCommand open = new();
            CommandExecutionResult openResult = await open.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = filePath,
                    session_id = sessionId,
                }),
                CancellationToken.None);
            Assert.True(openResult.Ok);

            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Sum() => 42;
                }
                """);

            SessionStatusCommand status = new();
            CommandExecutionResult statusResult = await status.ExecuteAsync(
                ToJsonElement(new { session_id = sessionId }),
                CancellationToken.None);

            Assert.True(statusResult.Ok);
            string json = JsonSerializer.Serialize(statusResult.Data);
            Assert.Contains("\"sync_state\":\"disk_changed_external\"", json);
        }
        finally
        {
            SessionCloseCommand close = new();
            await close.ExecuteAsync(ToJsonElement(new { session_id = sessionId }), CancellationToken.None);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SessionApplyAndCommitCommand_AppliesEditsAndClosesByDefault()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public int Sum()
                {
                    return 1 + 2;
                }
            }
            """);
        string sessionId = $"apply-commit-{Guid.NewGuid():N}";

        try
        {
            SessionOpenCommand open = new();
            CommandExecutionResult openResult = await open.ExecuteAsync(
                ToJsonElement(new
                {
                    file_path = filePath,
                    session_id = sessionId,
                }),
                CancellationToken.None);
            Assert.True(openResult.Ok);

            SessionApplyAndCommitCommand applyAndCommit = new();
            CommandExecutionResult applyAndCommitResult = await applyAndCommit.ExecuteAsync(
                ToJsonElement(new
                {
                    session_id = sessionId,
                    expected_generation = 0,
                    edits = new object[]
                    {
                        new
                        {
                            start_line = 5,
                            start_column = 16,
                            end_line = 5,
                            end_column = 21,
                            new_text = "9 + 10",
                        },
                    },
                }),
                CancellationToken.None);

            Assert.True(applyAndCommitResult.Ok);
            string applyCommitJson = JsonSerializer.Serialize(applyAndCommitResult.Data);
            Assert.Contains($"\"session_id\":\"{sessionId}\"", applyCommitJson);
            Assert.Contains("\"wrote_file\":true", applyCommitJson);
            Assert.Contains("\"keep_session\":false", applyCommitJson);
            Assert.Contains("return 9 + 10;", File.ReadAllText(filePath));

            SessionStatusCommand status = new();
            CommandExecutionResult statusResult = await status.ExecuteAsync(
                ToJsonElement(new { session_id = sessionId }),
                CancellationToken.None);
            Assert.False(statusResult.Ok);
            Assert.Contains(statusResult.Errors, e => e.Code == "session_not_found");
        }
        finally
        {
            SessionCloseCommand close = new();
            await close.ExecuteAsync(ToJsonElement(new { session_id = sessionId }), CancellationToken.None);
            File.Delete(filePath);
        }
    }

    private static string WriteTempFile(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"roslyn-agent-session-tests-{Guid.NewGuid():N}.cs");
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

