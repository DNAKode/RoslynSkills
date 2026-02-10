using RoslynSkills.Core.Commands;
using System.Text.Json;

namespace RoslynSkills.Core.Tests;

public sealed class CommandTests
{
    [Fact]
    public async Task FindSymbolCommand_ReturnsStructuredMatches()
    {
        string filePath = WriteTempFile(
            """
            namespace Demo;
            public class Worker
            {
                public void Run()
                {
                    var worker = new Worker();
                }
            }
            """);

        try
        {
            FindSymbolCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                symbol_name = "Worker",
                context_lines = 1,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"total_matches\":2", json);
            Assert.Contains("\"namespace_name\":\"Demo\"", json);
            Assert.Contains("\"symbol_kind\":\"NamedType\"", json);
            Assert.Contains("\"is_resolved\":true", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task FindSymbolCommand_BriefMode_ReturnsCompactMatches()
    {
        string filePath = WriteTempFile(
            """
            namespace Demo;
            public class Worker
            {
                public void Run()
                {
                    var worker = new Worker();
                }
            }
            """);

        try
        {
            FindSymbolCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                symbol_name = "Worker",
                brief = true,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement root = doc.RootElement;

            JsonElement query = root.GetProperty("query");
            Assert.True(query.GetProperty("brief").GetBoolean());
            Assert.Equal(0, query.GetProperty("context_lines").GetInt32());
            Assert.Equal(2, root.GetProperty("total_matches").GetInt32());

            JsonElement firstMatch = root.GetProperty("matches")[0];
            Assert.False(firstMatch.TryGetProperty("context", out _));
            Assert.True(firstMatch.TryGetProperty("symbol_kind", out _));
            Assert.True(firstMatch.TryGetProperty("symbol_display", out _));
            Assert.True(firstMatch.TryGetProperty("symbol_id", out _));
            Assert.True(firstMatch.TryGetProperty("is_resolved", out _));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task GetFileDiagnosticsCommand_ReturnsErrorsForBrokenCode()
    {
        string filePath = WriteTempFile(
            """
            public class Broken
            {
                public void M()
                {
                    int value = ;
                }
            }
            """);

        try
        {
            GetFileDiagnosticsCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"errors\":", json);
            Assert.DoesNotContain("\"total\":0", json);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task GetSolutionSnapshotCommand_DefaultsToCompactAndSkipsGeneratedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslyn-agent-snapshot-{Guid.NewGuid():N}");
        string srcDir = Path.Combine(root, "src");
        string objDir = Path.Combine(root, "obj");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(objDir);

        string sourcePath = Path.Combine(srcDir, "Program.cs");
        await File.WriteAllTextAsync(sourcePath,
            """
            public class Program
            {
                public void Run()
                {
                    int value = ;
                }
            }
            """);

        string generatedPath = Path.Combine(objDir, "Program.g.cs");
        await File.WriteAllTextAsync(generatedPath,
            """
            public class Generated
            {
                public void M()
                {
                    int x = ;
                }
            }
            """);

        try
        {
            GetSolutionSnapshotCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                directory_path = root,
                recursive = true,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"mode\":\"compact\"", json);
            Assert.Contains("\"total_files\":1", json);
            Assert.Contains("\"skipped_generated_files\":1", json);
            Assert.DoesNotContain("Program.g.cs", json);
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
    public async Task GetSolutionSnapshotCommand_BriefMode_OmitsHeavySectionsByDefault()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslyn-agent-snapshot-brief-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        string sourcePath = Path.Combine(root, "Program.cs");
        await File.WriteAllTextAsync(sourcePath,
            """
            public class Program
            {
                public void Run()
                {
                    int value = ;
                }
            }
            """);

        try
        {
            GetSolutionSnapshotCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                directory_path = root,
                recursive = true,
                brief = true,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            JsonElement rootElement = doc.RootElement;
            Assert.Equal("compact", rootElement.GetProperty("mode").GetString());
            Assert.True(rootElement.TryGetProperty("summary", out _));
            Assert.False(rootElement.TryGetProperty("query", out _));
            Assert.False(rootElement.TryGetProperty("resolution", out _));
            Assert.False(rootElement.TryGetProperty("files", out _));
            Assert.False(rootElement.TryGetProperty("diagnostics", out _));
            Assert.False(rootElement.TryGetProperty("guidance", out _));
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
    public async Task GetSolutionSnapshotCommand_UsesFileOverridesWithoutWritingToDisk()
    {
        string filePath = WriteTempFile(
            """
            public class Demo
            {
                public void Run()
                {
                }
            }
            """);

        string original = await File.ReadAllTextAsync(filePath);

        try
        {
            GetSolutionSnapshotCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_paths = new[] { filePath },
                mode = "guided",
                file_overrides = new[]
                {
                    new
                    {
                        file_path = filePath,
                        content =
                            """
                            public class Demo
                            {
                                public void Run()
                                {
                                    int value = ;
                                }
                            }
                            """,
                    },
                },
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"mode\":\"guided\"", json);
            Assert.Contains("\"errors\":", json);
            Assert.Contains("\"guidance\":", json);

            string unchanged = await File.ReadAllTextAsync(filePath);
            Assert.Equal(original, unchanged);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RenameSymbolCommand_RenamesDeclarationAndReferencesInFile()
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
            RenameSymbolCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 4,
                column = 17,
                new_name = "Execute",
                apply = true,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"replacement_count\":2", json);
            Assert.Contains("\"wrote_file\":true", json);
            Assert.Contains("\"diagnostics_after_edit\":", json);
            Assert.Contains("\"errors\":0", json);

            string updated = File.ReadAllText(filePath);
            Assert.Contains("public void Execute()", updated);
            Assert.Contains("Execute();", updated);
            Assert.DoesNotContain("public void Run()", updated);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RenameSymbolCommand_DryRunDoesNotWriteFile()
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
            RenameSymbolCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 4,
                column = 17,
                new_name = "Execute",
                apply = false,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"wrote_file\":false", json);

            string unchanged = File.ReadAllText(filePath);
            Assert.Contains("public void Run()", unchanged);
            Assert.Contains("Run();", unchanged);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ReplaceMemberBodyCommand_UpdatesMethodBodyAndReturnsDiagnostics()
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
            ReplaceMemberBodyCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 4,
                column = 20,
                new_body = "return a - b;",
                apply = true,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.True(result.Ok);
            string json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"member_name\":\"Add\"", json);
            Assert.Contains("\"wrote_file\":true", json);
            Assert.Contains("\"diagnostics_after_edit\":", json);

            string updated = File.ReadAllText(filePath);
            Assert.Contains("return a - b;", updated);
            Assert.DoesNotContain("return a + b;", updated);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ReplaceMemberBodyCommand_ReturnsErrorForNonMethodAnchor()
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
            ReplaceMemberBodyCommand command = new();
            JsonElement input = ToJsonElement(new
            {
                file_path = filePath,
                line = 1,
                column = 1,
                new_body = "return 0;",
                apply = false,
            });

            var result = await command.ExecuteAsync(input, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.Code == "invalid_target");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static string WriteTempFile(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"roslyn-agent-{Guid.NewGuid():N}.cs");
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

