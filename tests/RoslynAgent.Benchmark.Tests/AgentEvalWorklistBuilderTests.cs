using RoslynAgent.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalWorklistBuilderTests
{
    [Fact]
    public async Task BuildAsync_ProducesPendingRunsForIncompleteCells()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-worklist-test-{Guid.NewGuid():N}");
        string runsDir = Path.Combine(root, "runs");
        string outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(runsDir);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson());

            // Only one of four expected runs exists.
            await File.WriteAllTextAsync(Path.Combine(runsDir, "run-1.json"), BuildRunJson(
                runId: "run-1",
                taskId: "task-001",
                conditionId: "control-text-only",
                replicate: 1));

            AgentEvalWorklistBuilder builder = new();
            AgentEvalWorklistReport report = await builder.BuildAsync(manifestPath, runsDir, outputDir, CancellationToken.None);

            Assert.Equal(4, report.expected_runs);
            Assert.Equal(1, report.observed_runs);
            Assert.Equal(3, report.pending_runs.Count);
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

    private static string BuildManifestJson()
    {
        object manifest = new
        {
            experiment_id = "exp-worklist",
            description = "worklist test",
            roslyn_tool_prefixes = new[] { "roslyn-agent." },
            runs_per_cell = 2,
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

    private static string BuildRunJson(string runId, string taskId, string conditionId, int replicate)
    {
        object run = new
        {
            run_id = runId,
            task_id = taskId,
            condition_id = conditionId,
            replicate,
            agent = "codex-cli",
            model = "gpt-5-codex",
            succeeded = true,
            compile_passed = true,
            tests_passed = true,
            duration_seconds = 100.0,
            tools_offered = Array.Empty<string>(),
            tool_calls = Array.Empty<object>(),
            post_run_reflection = new
            {
                summary = "ok",
                helpful_tools = Array.Empty<string>(),
                unhelpful_tools = Array.Empty<string>(),
                roslyn_helpfulness_score = (int?)null,
            },
        };

        return JsonSerializer.Serialize(run);
    }
}
