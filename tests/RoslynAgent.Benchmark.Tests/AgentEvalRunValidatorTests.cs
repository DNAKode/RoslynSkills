using RoslynAgent.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalRunValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ReturnsValidTrue_ForCompleteCleanRuns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"run-validator-test-{Guid.NewGuid():N}");
        string runsDir = Path.Combine(root, "runs");
        string outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(runsDir);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson(runsPerCell: 1));

            await File.WriteAllTextAsync(Path.Combine(runsDir, "run-control.json"), BuildRunJson(
                runId: "run-control",
                taskId: "task-001",
                conditionId: "control-text-only",
                roslynToolOffered: false,
                roslynToolUsed: false,
                roslynHelpfulnessScore: null));

            await File.WriteAllTextAsync(Path.Combine(runsDir, "run-treatment.json"), BuildRunJson(
                runId: "run-treatment",
                taskId: "task-001",
                conditionId: "treatment-roslyn-optional",
                roslynToolOffered: true,
                roslynToolUsed: true,
                roslynHelpfulnessScore: 4));

            AgentEvalRunValidator validator = new();
            AgentEvalRunValidationReport report = await validator.ValidateAsync(
                manifestPath,
                runsDir,
                outputDir,
                CancellationToken.None);

            Assert.True(report.valid);
            Assert.Equal(0, report.error_count);
            Assert.Equal(0, report.issue_count);
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

    [Fact]
    public async Task ValidateAsync_FlagsControlContaminationAndInvalidHelpfulness()
    {
        string root = Path.Combine(Path.GetTempPath(), $"run-validator-test-{Guid.NewGuid():N}");
        string runsDir = Path.Combine(root, "runs");
        string outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(runsDir);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson(runsPerCell: 1));

            await File.WriteAllTextAsync(Path.Combine(runsDir, "run-control-contaminated.json"), BuildRunJson(
                runId: "run-control-contaminated",
                taskId: "task-001",
                conditionId: "control-text-only",
                roslynToolOffered: true,
                roslynToolUsed: true,
                roslynHelpfulnessScore: 3));

            await File.WriteAllTextAsync(Path.Combine(runsDir, "run-treatment-invalid-score.json"), BuildRunJson(
                runId: "run-treatment-invalid-score",
                taskId: "task-001",
                conditionId: "treatment-roslyn-optional",
                roslynToolOffered: true,
                roslynToolUsed: true,
                roslynHelpfulnessScore: 8));

            AgentEvalRunValidator validator = new();
            AgentEvalRunValidationReport report = await validator.ValidateAsync(
                manifestPath,
                runsDir,
                outputDir,
                CancellationToken.None);

            Assert.False(report.valid);
            Assert.True(report.error_count > 0);
            Assert.True(report.warning_count > 0);
            Assert.True(report.contaminated_control_runs > 0);
            Assert.Contains(report.issues, i => i.message.Contains("condition contamination", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(report.issues, i => i.message.Contains("1..5", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string BuildManifestJson(int runsPerCell)
    {
        object manifest = new
        {
            experiment_id = "exp-run-validator",
            description = "run validator tests",
            roslyn_tool_prefixes = new[] { "roslyn-agent." },
            runs_per_cell = runsPerCell,
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
                    commit = "abc123",
                    acceptance_checks = new[] { "dotnet test" },
                },
            },
        };

        return JsonSerializer.Serialize(manifest);
    }

    private static string BuildRunJson(
        string runId,
        string taskId,
        string conditionId,
        bool roslynToolOffered,
        bool roslynToolUsed,
        int? roslynHelpfulnessScore)
    {
        List<string> toolsOffered = new() { "read_file", "run_shell", "search" };
        if (roslynToolOffered)
        {
            toolsOffered.Add("roslyn-agent.run");
        }

        List<object> toolCalls = new()
        {
            new { tool_name = "search", ok = true },
        };

        if (roslynToolUsed)
        {
            toolCalls.Add(new { tool_name = "roslyn-agent.run", ok = true });
        }

        object run = new
        {
            run_id = runId,
            task_id = taskId,
            condition_id = conditionId,
            replicate = 1,
            agent = "codex-cli",
            model = "gpt-5-codex",
            succeeded = true,
            compile_passed = true,
            tests_passed = true,
            duration_seconds = 120.0,
            tools_offered = toolsOffered,
            tool_calls = toolCalls,
            context = new
            {
                task_title = "Task 1",
                repo = "owner/repo",
                repo_url = "https://github.com/owner/repo.git",
                commit = "abc123",
                acceptance_checks = new[] { "dotnet test" },
                task_prompt_file = "prompts/task-1.md",
            },
            post_run_reflection = new
            {
                summary = "run summary",
                helpful_tools = roslynToolUsed ? new[] { "roslyn-agent.run" } : new[] { "search" },
                unhelpful_tools = Array.Empty<string>(),
                roslyn_helpfulness_score = roslynHelpfulnessScore,
            },
        };

        return JsonSerializer.Serialize(run);
    }
}
