using System.Text;
using System.Text.Json;

namespace RoslynAgent.Benchmark.AgentEval;

public sealed class AgentEvalReportExporter
{
    public async Task<string> ExportMarkdownAsync(
        string reportPath,
        string? runValidationPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentEvalReport report = ReadJson<AgentEvalReport>(reportPath)
            ?? throw new InvalidOperationException($"Failed to parse report JSON at '{reportPath}'.");
        AgentEvalRunValidationReport? runValidation = null;
        if (!string.IsNullOrWhiteSpace(runValidationPath))
        {
            runValidation = ReadJson<AgentEvalRunValidationReport>(runValidationPath)
                ?? throw new InvalidOperationException($"Failed to parse run validation JSON at '{runValidationPath}'.");
        }

        string markdown = BuildMarkdown(report, runValidation);
        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-summary.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, markdown, cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static T? ReadJson<T>(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("JSON file not found.", fullPath);
        }

        string json = File.ReadAllText(fullPath);
        return JsonSerializer.Deserialize<T>(json, AgentEvalStorage.SerializerOptions);
    }

    private static string BuildMarkdown(
        AgentEvalReport report,
        AgentEvalRunValidationReport? runValidation)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Agent Eval Summary");
        sb.AppendLine();
        sb.AppendLine($"- Experiment: `{report.experiment_id}`");
        sb.AppendLine($"- Generated UTC: `{report.generated_utc:O}`");
        sb.AppendLine($"- Total runs: `{report.total_runs}`");
        sb.AppendLine();

        sb.AppendLine("## Primary Comparison");
        if (report.primary_comparison is null)
        {
            sb.AppendLine();
            sb.AppendLine("No primary comparison was available.");
        }
        else if (!report.primary_comparison.sufficient_data)
        {
            sb.AppendLine();
            sb.AppendLine($"Insufficient data. Control runs: `{report.primary_comparison.control_run_count}`, treatment runs: `{report.primary_comparison.treatment_run_count}`.");
            if (!string.IsNullOrWhiteSpace(report.primary_comparison.note))
            {
                sb.AppendLine();
                sb.AppendLine($"Note: {report.primary_comparison.note}");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine($"- Control condition: `{report.primary_comparison.control_condition_id}`");
            sb.AppendLine($"- Treatment condition: `{report.primary_comparison.treatment_condition_id}`");
            sb.AppendLine($"- Success delta: `{FormatPercent(report.primary_comparison.success_rate_delta)}`");
            sb.AppendLine($"- Compile delta: `{FormatPercent(report.primary_comparison.compile_rate_delta)}`");
            sb.AppendLine($"- Tests delta: `{FormatPercent(report.primary_comparison.tests_rate_delta)}`");
            sb.AppendLine($"- Treatment Roslyn usage rate: `{FormatPercent(report.primary_comparison.roslyn_used_rate_in_treatment)}`");
        }

        sb.AppendLine();
        sb.AppendLine("## Condition Summaries");
        sb.AppendLine();
        sb.AppendLine("| Condition | Runs | Success | Compile | Tests | Roslyn Used | Roslyn Call Share | Avg Roslyn Helpfulness |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (AgentEvalConditionSummary condition in report.condition_summaries)
        {
            sb.AppendLine(
                $"| `{condition.condition_id}` | {condition.run_count} | {FormatPercent(condition.success_rate)} | {FormatPercent(condition.compile_rate)} | {FormatPercent(condition.tests_rate)} | {FormatPercent(condition.roslyn_used_rate)} | {FormatPercent(condition.roslyn_call_share)} | {FormatDouble(condition.average_roslyn_helpfulness_score)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Task Comparisons");
        sb.AppendLine();
        sb.AppendLine("| Task | Sufficient Data | Control Runs | Treatment Runs | Success Delta | Compile Delta | Tests Delta | Treatment Roslyn Usage |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (AgentEvalTaskComparison task in report.task_comparisons)
        {
            sb.AppendLine(
                $"| `{task.task_id}` | {BoolYesNo(task.sufficient_data)} | {task.control_run_count} | {task.treatment_run_count} | {FormatPercent(task.success_rate_delta)} | {FormatPercent(task.compile_rate_delta)} | {FormatPercent(task.tests_rate_delta)} | {FormatPercent(task.treatment_roslyn_used_rate)} |");
        }

        if (runValidation is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Run Validation");
            sb.AppendLine();
            sb.AppendLine($"- Valid: `{runValidation.valid}`");
            sb.AppendLine($"- Issues: `{runValidation.issue_count}` (errors={runValidation.error_count}, warnings={runValidation.warning_count})");
            sb.AppendLine($"- Contaminated control runs: `{runValidation.contaminated_control_runs}`");
            sb.AppendLine($"- Treatment missing Roslyn offered: `{runValidation.treatment_runs_without_roslyn_offered}`");
            sb.AppendLine($"- Treatment missing Roslyn usage: `{runValidation.treatment_runs_without_roslyn_usage}`");

            if (runValidation.issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Top validation issues:");
                foreach (AgentEvalRunValidationIssue issue in runValidation.issues.Take(10))
                {
                    sb.AppendLine($"- [{issue.severity}] run={issue.run_id} task={issue.task_id} cond={issue.condition_id} {issue.message}");
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatPercent(double? value)
    {
        if (!value.HasValue)
        {
            return "n/a";
        }

        return value.Value.ToString("P2");
    }

    private static string FormatDouble(double? value)
    {
        if (!value.HasValue)
        {
            return "n/a";
        }

        return value.Value.ToString("0.00");
    }

    private static string BoolYesNo(bool value)
        => value ? "Yes" : "No";
}
