using RoslynAgent.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalRunTemplateGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_CreatesTemplatesForMissingRuns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-template-test-{Guid.NewGuid():N}");
        string runsDir = Path.Combine(root, "runs");
        string outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(runsDir);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson());

            AgentEvalRunTemplateGenerator generator = new();
            AgentEvalTemplateGenerationReport report = await generator.GenerateAsync(
                manifestPath,
                runsDir,
                outputDir,
                CancellationToken.None);

            Assert.Equal(2, report.pending_count);
            Assert.Equal(2, report.template_files_created);
            Assert.True(Directory.Exists(report.templates_directory));

            string[] files = Directory.GetFiles(report.templates_directory, "*.json", SearchOption.TopDirectoryOnly);
            Assert.Equal(2, files.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string BuildManifestJson()
    {
        object manifest = new
        {
            experiment_id = "exp-template",
            description = "template test",
            roslyn_tool_prefixes = new[] { "roslyn-agent." },
            runs_per_cell = 1,
            conditions = new[]
            {
                new { id = "control-text-only", name = "Control", roslyn_tools_enabled = false, notes = "n" },
                new { id = "treatment-roslyn-optional", name = "Treatment", roslyn_tools_enabled = true, notes = "n" },
            },
            tasks = new[]
            {
                new { id = "task-001", title = "Task 1", repo = "o/r", commit = "abc", acceptance_checks = new[] { "dotnet test" } },
            },
        };

        return JsonSerializer.Serialize(manifest);
    }
}
