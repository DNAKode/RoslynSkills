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
    public async Task MemberSourceCommand_EditTargetExplainsPrefixRepairWithTrivia()
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
                column = 13,
                mode = "member",
                include_edit_target_text = true,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement trivia = doc.RootElement
                .GetProperty("edit_target")
                .GetProperty("trivia");

            Assert.True(trivia.GetProperty("span_preserves_existing_line_prefix").GetBoolean());
            Assert.Contains("include_trivia=true", trivia.GetProperty("prefix_edit_rule").GetString());
            Assert.Contains("duplicated indentation", trivia.GetProperty("prefix_edit_rule").GetString());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_ReplaceSpanOperationIncludesExpectedTextWhenAvailable()
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
                column = 16,
                mode = "member",
                include_edit_target_text = true,
                max_chars = 1000,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement operation = doc.RootElement
                .GetProperty("edit_target")
                .GetProperty("replace_span_operation");

            Assert.Equal("replace_span", operation.GetProperty("kind").GetString());
            Assert.Contains("public int Add", operation.GetProperty("expected_text").GetString());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_FocusTextReturnsSmallWindowInsideLargeMember()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public void Run()
                {
                    Step1();
                    Step2();
                    ImportantMarker();
                    Step4();
                    Step5();
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
                column = 17,
                mode = "member",
                focus_text = "ImportantMarker",
                context_lines_before = 1,
                context_lines_after = 1,
                max_chars = 1000,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement query = doc.RootElement.GetProperty("query");
            Assert.Equal("ImportantMarker", query.GetProperty("focus_text").GetString());
            Assert.False(query.GetProperty("include_edit_target_text").GetBoolean());
            JsonElement exactSpanText = doc.RootElement.GetProperty("edit_target").GetProperty("exact_span_text");
            Assert.True(exactSpanText.GetProperty("omitted").GetBoolean());
            Assert.Contains("source.text", exactSpanText.GetProperty("use_as_replacement_base").GetString());
            JsonElement source = doc.RootElement.GetProperty("source");
            string text = source.GetProperty("text").GetString()!;
            Assert.DoesNotContain("Step1", text);
            Assert.Contains("Step2", text);
            Assert.Contains("ImportantMarker", text);
            Assert.Contains("Step4", text);
            Assert.DoesNotContain("Step5", text);

            JsonElement focus = source.GetProperty("focus");
            Assert.True(focus.GetProperty("matched").GetBoolean());
            Assert.Equal(7, focus.GetProperty("line").GetInt32());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_MissingFocusInLargeMemberCapsSourceAndGuidesNarrowing()
    {
        string memberBody = string.Join(Environment.NewLine, Enumerable.Range(1, 240).Select(i => $"        Step{i}();"));
        string filePath = WriteTempFile(
            $$"""
            public class Demo
            {
                public void Run()
                {
            {{memberBody}}
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
                column = 17,
                mode = "member",
                focus_text = "MissingMarker",
                context_lines_before = 0,
                context_lines_after = 0,
                max_chars = 200_000,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement member = doc.RootElement.GetProperty("member");
            Assert.True(member.GetProperty("source_line_count").GetInt32() <= 32);
            JsonElement source = doc.RootElement.GetProperty("source");
            Assert.DoesNotContain("Step240", source.GetProperty("text").GetString()!);
            JsonElement focus = source.GetProperty("focus");
            Assert.False(focus.GetProperty("matched").GetBoolean());
            Assert.True(focus.GetProperty("guard_applied").GetBoolean());
            JsonElement guidance = doc.RootElement.GetProperty("payload_guidance");
            Assert.True(guidance.GetProperty("focus_not_found").GetBoolean());
            Assert.Contains("ctx.search_text", guidance.GetProperty("next_steps")[0].GetString());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_MemberNameAnchorReturnsUniqueMember()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public void First()
                {
                    Step1();
                }

                public void TargetCase()
                {
                    ImportantMarker();
                }
            }
            """);

        try
        {
            MemberSourceCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                member_name = "TargetCase",
                mode = "member",
                focus_text = "ImportantMarker",
                context_lines_before = 1,
                context_lines_after = 1,
                max_chars = 1000,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement root = doc.RootElement;
            Assert.Equal("TargetCase", root.GetProperty("member").GetProperty("member_name").GetString());
            Assert.Equal("TargetCase", root.GetProperty("query").GetProperty("member_name").GetString());
            Assert.Equal(8, root.GetProperty("query").GetProperty("line").GetInt32());
            string source = root.GetProperty("source").GetProperty("text").GetString()!;
            Assert.Contains("ImportantMarker", source);
            Assert.DoesNotContain("Step1", source);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task MemberSourceCommand_TruncatedFocusedEditTargetWarnsAgainstWholeSpanReplacement()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public void Run()
                {
                    Step1();
                    Step2();
                    Step3();
                    Step4();
                    Step5();
                    Step6();
                    Step7();
                    Step8();
                    Step9();
                    Step10();
                    ImportantMarker();
                    Step12();
                    Step13();
                    Step14();
                    Step15();
                    Step16();
                    Step17();
                    Step18();
                    Step19();
                    Step20();
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
                column = 17,
                mode = "member",
                focus_text = "ImportantMarker",
                context_lines_before = 1,
                context_lines_after = 1,
                include_edit_target_text = true,
                max_chars = 200,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement editTarget = doc.RootElement.GetProperty("edit_target");
            JsonElement exactSpanText = editTarget.GetProperty("exact_span_text");
            Assert.True(exactSpanText.GetProperty("truncated").GetBoolean());
            string guidance = exactSpanText.GetProperty("use_as_replacement_base").GetString()!;
            Assert.Contains("Do not use this truncated exact_span_text", guidance);
            Assert.Contains("source.text", guidance);
            Assert.Contains("without focus_text", guidance);

            JsonElement operation = editTarget.GetProperty("replace_span_operation");
            Assert.False(operation.TryGetProperty("expected_text", out _));
            Assert.Contains("expected_text is omitted", operation.GetProperty("expected_text_omitted_reason").GetString());
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
    public async Task SessionOpenCommand_RejectsNonCSharpFiles()
    {
        string path = Path.Combine(Path.GetTempPath(), $"roslynskills-session-{Guid.NewGuid():N}.slnx");
        await File.WriteAllTextAsync(path, "<Solution />");
        string sessionId = $"reject-{Guid.NewGuid():N}";

        try
        {
            SessionOpenCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = path,
                session_id = sessionId,
            });

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.Code == "unsupported_file_type");
        }
        finally
        {
            File.Delete(path);
            SessionCloseCommand close = new();
            await close.ExecuteAsync(ToJsonElement(new { session_id = sessionId }), CancellationToken.None);
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

