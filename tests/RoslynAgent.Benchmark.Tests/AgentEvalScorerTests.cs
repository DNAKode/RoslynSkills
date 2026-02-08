using RoslynAgent.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalScorerTests
{
    [Fact]
    public async Task ScoreAsync_ComputesConditionAndComparisonMetrics()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-eval-test-{Guid.NewGuid():N}");
        string runsDir = Path.Combine(root, "runs");
        string outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(runsDir);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson());

            await File.WriteAllTextAsync(Path.Combine(runsDir, "run1.json"), BuildRunJson(
                runId: "run1",
                conditionId: "control-text-only",
                succeeded: false,
                compilePassed: false,
                testsPassed: false,
                toolCalls: new[] { "read_file" },
                roslynScore: null));
            await File.WriteAllTextAsync(Path.Combine(runsDir, "run2.json"), BuildRunJson(
                runId: "run2",
                conditionId: "treatment-roslyn-optional",
                succeeded: true,
                compilePassed: true,
                testsPassed: true,
                toolCalls: new[] { "read_file", "roslyn-agent.run" },
                roslynScore: 4));

            AgentEvalScorer scorer = new();
            AgentEvalReport report = await scorer.ScoreAsync(manifestPath, runsDir, outputDir, CancellationToken.None);

            Assert.Equal(2, report.total_runs);
            Assert.NotNull(report.primary_comparison);
            Assert.True(report.primary_comparison!.success_rate_delta > 0);
            Assert.True(report.primary_comparison.roslyn_used_rate_in_treatment > 0);
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
            experiment_id = "exp-1",
            description = "test",
            roslyn_tool_prefixes = new[] { "roslyn-agent." },
            conditions = new[]
            {
                new { id = "control-text-only", name = "Control", roslyn_tools_enabled = false, notes = "n" },
                new { id = "treatment-roslyn-optional", name = "Treatment", roslyn_tools_enabled = true, notes = "n" },
            },
            tasks = new[]
            {
                new { id = "task-1", title = "t", repo = "o/r", commit = "abc", acceptance_checks = new[] { "dotnet test" } },
            },
        };

        return JsonSerializer.Serialize(manifest);
    }

    private static string BuildRunJson(
        string runId,
        string conditionId,
        bool succeeded,
        bool compilePassed,
        bool testsPassed,
        IReadOnlyList<string> toolCalls,
        int? roslynScore)
    {
        object run = new
        {
            run_id = runId,
            task_id = "task-1",
            condition_id = conditionId,
            agent = "codex-cli",
            model = "gpt-5-codex",
            succeeded,
            compile_passed = compilePassed,
            tests_passed = testsPassed,
            duration_seconds = 10.0,
            tools_offered = new[] { "read_file", "roslyn-agent.run" },
            tool_calls = toolCalls.Select(t => new { tool_name = t, ok = true }).ToArray(),
            post_run_reflection = new
            {
                summary = "s",
                helpful_tools = toolCalls.ToArray(),
                unhelpful_tools = Array.Empty<string>(),
                roslyn_helpfulness_score = roslynScore,
            },
        };

        return JsonSerializer.Serialize(run);
    }
}
