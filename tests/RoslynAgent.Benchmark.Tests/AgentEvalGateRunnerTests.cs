using RoslynAgent.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalGateRunnerTests
{
    [Fact]
    public async Task RunAsync_PassesGate_ForCompleteSampleRunSet()
    {
        string repoRoot = FindRepoRoot();
        string manifestPath = Path.Combine(repoRoot, "benchmarks", "samples", "agent-eval", "manifest.sample.json");
        string runsPath = Path.Combine(repoRoot, "benchmarks", "samples", "agent-eval", "runs");
        string outputDir = Path.Combine(Path.GetTempPath(), $"agent-eval-gate-{Guid.NewGuid():N}");

        try
        {
            AgentEvalGateRunner gateRunner = new();
            AgentEvalGateReport report = await gateRunner.RunAsync(
                manifestPath,
                runsPath,
                outputDir,
                CancellationToken.None);

            Assert.True(report.manifest_valid);
            Assert.True(report.runs_valid);
            Assert.True(report.sufficient_data);
            Assert.True(report.gate_passed);
            Assert.True(File.Exists(report.output_path));
            Assert.True(File.Exists(report.summary_path));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_FailsGate_WhenDataIsInsufficient()
    {
        string repoRoot = FindRepoRoot();
        string manifestPath = Path.Combine(repoRoot, "benchmarks", "experiments", "oss-csharp-pilot-v1", "manifest.json");
        string runsPath = Path.Combine(Path.GetTempPath(), $"agent-eval-gate-runs-{Guid.NewGuid():N}");
        string outputDir = Path.Combine(Path.GetTempPath(), $"agent-eval-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runsPath);

        try
        {
            AgentEvalGateRunner gateRunner = new();
            AgentEvalGateReport report = await gateRunner.RunAsync(
                manifestPath,
                runsPath,
                outputDir,
                CancellationToken.None);

            Assert.True(report.manifest_valid);
            Assert.True(report.runs_valid);
            Assert.False(report.sufficient_data);
            Assert.False(report.gate_passed);
            Assert.Contains(report.notes, n => n.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }

            if (Directory.Exists(runsPath))
            {
                Directory.Delete(runsPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_WarningPolicyControlsGateOutcome()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-eval-gate-policy-{Guid.NewGuid():N}");
        string runsPath = Path.Combine(root, "runs");
        string outputDefault = Path.Combine(root, "output-default");
        string outputStrict = Path.Combine(root, "output-strict");
        Directory.CreateDirectory(runsPath);

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BuildManifestJson());

            await File.WriteAllTextAsync(Path.Combine(runsPath, "run-control.json"), BuildRunJson(
                runId: "run-control",
                taskId: "task-001",
                conditionId: "control-text-only",
                roslynToolOffered: false,
                roslynToolUsed: false,
                roslynHelpfulnessScore: null));

            await File.WriteAllTextAsync(Path.Combine(runsPath, "run-treatment-warning.json"), BuildRunJson(
                runId: "run-treatment-warning",
                taskId: "task-001",
                conditionId: "treatment-roslyn-optional",
                roslynToolOffered: true,
                roslynToolUsed: false,
                roslynHelpfulnessScore: null));

            AgentEvalGateRunner gateRunner = new();
            AgentEvalGateReport defaultPolicyReport = await gateRunner.RunAsync(
                manifestPath,
                runsPath,
                outputDefault,
                CancellationToken.None);
            AgentEvalGateReport strictPolicyReport = await gateRunner.RunAsync(
                manifestPath,
                runsPath,
                outputStrict,
                CancellationToken.None,
                failOnWarnings: true);

            Assert.True(defaultPolicyReport.manifest_valid);
            Assert.True(defaultPolicyReport.runs_valid);
            Assert.True(defaultPolicyReport.sufficient_data);
            Assert.True(defaultPolicyReport.run_validation_warning_count > 0);
            Assert.False(defaultPolicyReport.fail_on_run_warnings);
            Assert.True(defaultPolicyReport.gate_passed);

            Assert.True(strictPolicyReport.manifest_valid);
            Assert.True(strictPolicyReport.runs_valid);
            Assert.True(strictPolicyReport.sufficient_data);
            Assert.True(strictPolicyReport.run_validation_warning_count > 0);
            Assert.True(strictPolicyReport.fail_on_run_warnings);
            Assert.False(strictPolicyReport.gate_passed);
            Assert.Contains(strictPolicyReport.notes, n =>
                n.Contains("hard failures", StringComparison.OrdinalIgnoreCase));
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
            experiment_id = "exp-gate-policy",
            description = "gate policy tests",
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

    private static string FindRepoRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            string candidate = Path.Combine(cursor.FullName, "RoslynSkill.slnx");
            if (File.Exists(candidate))
            {
                return cursor.FullName;
            }

            cursor = cursor.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test execution directory.");
    }
}
