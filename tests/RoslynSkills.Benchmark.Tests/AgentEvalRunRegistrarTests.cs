using RoslynSkills.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynSkills.Benchmark.Tests;

public sealed class AgentEvalRunRegistrarTests
{
    [Fact]
    public async Task RegisterAsync_WritesCanonicalRunFileWithContext()
    {
        string root = Path.Combine(Path.GetTempPath(), $"run-registrar-test-{Guid.NewGuid():N}");
        string runsDir = Path.Combine(root, "runs");
        Directory.CreateDirectory(runsDir);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson());

            AgentEvalRunRegistrar registrar = new();
            AgentEvalRunRegistration registration = new(
                RunId: "custom-run-id",
                TaskId: "task-001",
                ConditionId: "treatment-roslyn-optional",
                Replicate: 1,
                Agent: "codex-cli",
                Model: "gpt-5-codex",
                Succeeded: true,
                CompilePassed: true,
                TestsPassed: true,
                DurationSeconds: 120.5,
                PromptTokens: 1400,
                CompletionTokens: 600,
                TotalTokens: 2000,
                ToolsOffered: new[] { "search", "roslyn-agent.run" },
                ToolCalls: new[] { new AgentToolCall("roslyn-agent.run", true) },
                ReflectionSummary: "Useful run.",
                ReflectionHelpfulTools: new[] { "roslyn-agent.run" },
                ReflectionUnhelpfulTools: Array.Empty<string>(),
                RoslynHelpfulnessScore: 4);

            string output = await registrar.RegisterAsync(
                manifestPath,
                runsDir,
                registration,
                overwrite: false,
                CancellationToken.None);

            Assert.True(File.Exists(output));
            string json = await File.ReadAllTextAsync(output);
            Assert.Contains("\"run_id\": \"custom-run-id\"", json);
            Assert.Contains("\"task_title\": \"Task 1\"", json);
            Assert.Contains("\"roslyn_helpfulness_score\": 4", json);
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
            experiment_id = "exp-register",
            description = "register test",
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
                    title = "Task 1",
                    repo = "owner/repo",
                    repo_url = "https://github.com/owner/repo.git",
                    commit = "abc123",
                    task_prompt_file = "prompts/task-1.md",
                    acceptance_checks = new[] { "dotnet test" },
                },
            },
        };

        return JsonSerializer.Serialize(manifest);
    }
}

