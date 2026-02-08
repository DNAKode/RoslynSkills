using RoslynAgent.Benchmark.AgentEval;
using System.Text.Json;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalReportExporterTests
{
    [Fact]
    public async Task ExportMarkdownAsync_WritesSummaryWithTaskComparisonsAndValidation()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-eval-export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string reportPath = Path.Combine(root, "report.json");
            string validationPath = Path.Combine(root, "validation.json");
            string outputDir = Path.Combine(root, "output");

            AgentEvalReport report = new(
                experiment_id: "exp-export",
                generated_utc: DateTimeOffset.UtcNow,
                total_runs: 2,
                condition_summaries: new[]
                {
                    new AgentEvalConditionSummary("control-text-only", "Control", 1, 0, 0, 0, 210, 0, 0, 0, null),
                    new AgentEvalConditionSummary("treatment-roslyn-optional", "Treatment", 1, 1, 1, 1, 180, 1, 1, 0.4, 4),
                },
                primary_comparison: new AgentEvalComparison(
                    sufficient_data: true,
                    control_condition_id: "control-text-only",
                    treatment_condition_id: "treatment-roslyn-optional",
                    control_run_count: 1,
                    treatment_run_count: 1,
                    success_rate_delta: 1,
                    compile_rate_delta: 1,
                    tests_rate_delta: 1,
                    roslyn_used_rate_in_treatment: 1,
                    note: null),
                task_comparisons: new[]
                {
                    new AgentEvalTaskComparison(
                        task_id: "task-001",
                        task_title: "Task One",
                        sufficient_data: true,
                        control_run_count: 1,
                        treatment_run_count: 1,
                        success_rate_delta: 1,
                        compile_rate_delta: 1,
                        tests_rate_delta: 1,
                        treatment_roslyn_used_rate: 1,
                        note: null),
                },
                output_path: reportPath);

            AgentEvalRunValidationReport validation = new(
                experiment_id: "exp-export",
                generated_utc: DateTimeOffset.UtcNow,
                valid: true,
                total_runs: 2,
                expected_runs: 2,
                issue_count: 1,
                error_count: 0,
                warning_count: 1,
                contaminated_control_runs: 0,
                treatment_runs_without_roslyn_offered: 0,
                treatment_runs_without_roslyn_usage: 0,
                issues: new[]
                {
                    new AgentEvalRunValidationIssue(
                        severity: "warning",
                        run_id: "<summary>",
                        task_id: "<summary>",
                        condition_id: "<summary>",
                        message: "Observed run count is below expected."),
                },
                output_path: validationPath);

            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report));
            await File.WriteAllTextAsync(validationPath, JsonSerializer.Serialize(validation));

            AgentEvalReportExporter exporter = new();
            string summaryPath = await exporter.ExportMarkdownAsync(reportPath, validationPath, outputDir, CancellationToken.None);

            Assert.True(File.Exists(summaryPath));
            string markdown = await File.ReadAllTextAsync(summaryPath);
            Assert.Contains("# Agent Eval Summary", markdown);
            Assert.Contains("## Task Comparisons", markdown);
            Assert.Contains("task-001", markdown);
            Assert.Contains("## Run Validation", markdown);
            Assert.Contains("Contaminated control runs", markdown);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
