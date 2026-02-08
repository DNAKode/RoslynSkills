using RoslynAgent.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalManifestValidatorTests
{
    [Fact]
    public async Task ValidateAsync_FailsWhenPromptFileMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"manifest-validator-test-{Guid.NewGuid():N}");
        string outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(root);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson(promptFile: "missing-prompt.md"));

            AgentEvalManifestValidator validator = new();
            AgentEvalManifestValidationReport report = await validator.ValidateAsync(manifestPath, outputDir, CancellationToken.None);

            Assert.False(report.valid);
            Assert.True(report.issue_count > 0);
            Assert.Contains(report.issues, i => i.severity == "error");
            Assert.True(File.Exists(report.output_path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string BuildManifestJson(string promptFile)
    {
        object manifest = new
        {
            experiment_id = "exp-validate",
            description = "validate manifest",
            roslyn_tool_prefixes = new[] { "roslyn-agent." },
            runs_per_cell = 1,
            conditions = new[]
            {
                new { id = "control-text-only", name = "Control", roslyn_tools_enabled = false, notes = "n" },
                new { id = "treatment-roslyn-optional", name = "Treatment", roslyn_tools_enabled = true, notes = "n" },
            },
            tasks = new[]
            {
                new
                {
                    id = "task-001",
                    title = "Task",
                    repo = "owner/repo",
                    commit = "abc123",
                    repo_url = "https://github.com/owner/repo",
                    task_prompt_file = promptFile,
                    acceptance_checks = new[] { "dotnet test" },
                },
            },
        };

        return JsonSerializer.Serialize(manifest);
    }
}
