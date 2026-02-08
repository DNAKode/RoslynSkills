using RoslynAgent.Core.Commands;
using System.Text.Json;

namespace RoslynAgent.Core.Tests;

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
