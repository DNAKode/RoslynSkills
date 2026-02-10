using System.Text.Json;

namespace RoslynSkills.Benchmark.AgentEval;

public sealed class AgentEvalScorer
{
    public async Task<AgentEvalReport> ScoreAsync(
        string manifestPath,
        string runsDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        AgentEvalManifest manifest = AgentEvalStorage.LoadManifest(manifestPath);
        AgentEvalRun[] runs = AgentEvalStorage.LoadRuns(runsDirectory);
        AgentEvalValidation.ValidateRuns(manifest, runs);

        List<AgentEvalConditionSummary> summaries = manifest.Conditions
            .Select(c => BuildConditionSummary(c, runs, manifest.RoslynToolPrefixes))
            .ToList();

        AgentEvalComparison? comparison = BuildPrimaryComparison(manifest, summaries);
        List<AgentEvalTaskComparison> taskComparisons = BuildTaskComparisons(manifest, runs, manifest.RoslynToolPrefixes);
        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-report.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        AgentEvalReport report = new(
            experiment_id: manifest.ExperimentId,
            generated_utc: DateTimeOffset.UtcNow,
            total_runs: runs.Length,
            condition_summaries: summaries,
            primary_comparison: comparison,
            task_comparisons: taskComparisons,
            output_path: outputPath);

        string json = JsonSerializer.Serialize(report, AgentEvalStorage.SerializerOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static AgentEvalConditionSummary BuildConditionSummary(
        AgentEvalCondition condition,
        IReadOnlyList<AgentEvalRun> runs,
        IReadOnlyList<string> roslynToolPrefixes)
    {
        AgentEvalRun[] conditionRuns = runs
            .Where(r => string.Equals(r.ConditionId, condition.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        AgentEvalRunMetrics metrics = BuildRunMetrics(conditionRuns, roslynToolPrefixes);

        return new AgentEvalConditionSummary(
            condition_id: condition.Id,
            condition_name: condition.Name,
            run_count: metrics.run_count,
            success_rate: metrics.success_rate,
            compile_rate: metrics.compile_rate,
            tests_rate: metrics.tests_rate,
            average_duration_seconds: metrics.average_duration_seconds,
            roslyn_used_runs: metrics.roslyn_used_runs,
            roslyn_used_rate: metrics.roslyn_used_rate,
            roslyn_call_share: metrics.roslyn_call_share,
            average_roslyn_helpfulness_score: metrics.average_roslyn_helpfulness_score,
            runs_with_token_counts: metrics.runs_with_token_counts,
            average_total_tokens: metrics.average_total_tokens,
            median_total_tokens: metrics.median_total_tokens);
    }

    private static bool IsRoslynTool(string toolName, IReadOnlyList<string> roslynToolPrefixes)
    {
        foreach (string prefix in roslynToolPrefixes)
        {
            if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AgentEvalComparison? BuildPrimaryComparison(
        AgentEvalManifest manifest,
        IReadOnlyList<AgentEvalConditionSummary> summaries)
    {
        AgentEvalCondition? control = manifest.Conditions.FirstOrDefault(c => !c.RoslynToolsEnabled);
        AgentEvalCondition? treatment = manifest.Conditions.FirstOrDefault(c => c.RoslynToolsEnabled);
        if (control is null || treatment is null)
        {
            return null;
        }

        AgentEvalConditionSummary? controlSummary = summaries.FirstOrDefault(s =>
            string.Equals(s.condition_id, control.Id, StringComparison.OrdinalIgnoreCase));
        AgentEvalConditionSummary? treatmentSummary = summaries.FirstOrDefault(s =>
            string.Equals(s.condition_id, treatment.Id, StringComparison.OrdinalIgnoreCase));

        if (controlSummary is null || treatmentSummary is null)
        {
            return null;
        }

        bool sufficientData = controlSummary.run_count > 0 && treatmentSummary.run_count > 0;
        if (!sufficientData)
        {
            return new AgentEvalComparison(
                sufficient_data: false,
                control_condition_id: control.Id,
                treatment_condition_id: treatment.Id,
                control_run_count: controlSummary.run_count,
                treatment_run_count: treatmentSummary.run_count,
                success_rate_delta: null,
                compile_rate_delta: null,
                tests_rate_delta: null,
                roslyn_used_rate_in_treatment: treatmentSummary.run_count > 0 ? treatmentSummary.roslyn_used_rate : null,
                average_total_tokens_control: controlSummary.average_total_tokens,
                average_total_tokens_treatment: treatmentSummary.average_total_tokens,
                average_total_tokens_delta: TrySubtract(treatmentSummary.average_total_tokens, controlSummary.average_total_tokens),
                token_reduction_ratio: TryComputeReductionRatio(controlSummary.average_total_tokens, treatmentSummary.average_total_tokens),
                note: "Insufficient runs in one or both conditions. Collect additional runs before interpreting deltas.");
        }

        return new AgentEvalComparison(
            sufficient_data: true,
            control_condition_id: control.Id,
            treatment_condition_id: treatment.Id,
            control_run_count: controlSummary.run_count,
            treatment_run_count: treatmentSummary.run_count,
            success_rate_delta: treatmentSummary.success_rate - controlSummary.success_rate,
            compile_rate_delta: treatmentSummary.compile_rate - controlSummary.compile_rate,
            tests_rate_delta: treatmentSummary.tests_rate - controlSummary.tests_rate,
            roslyn_used_rate_in_treatment: treatmentSummary.roslyn_used_rate,
            average_total_tokens_control: controlSummary.average_total_tokens,
            average_total_tokens_treatment: treatmentSummary.average_total_tokens,
            average_total_tokens_delta: TrySubtract(treatmentSummary.average_total_tokens, controlSummary.average_total_tokens),
            token_reduction_ratio: TryComputeReductionRatio(controlSummary.average_total_tokens, treatmentSummary.average_total_tokens),
            note: null);
    }

    private static List<AgentEvalTaskComparison> BuildTaskComparisons(
        AgentEvalManifest manifest,
        IReadOnlyList<AgentEvalRun> runs,
        IReadOnlyList<string> roslynToolPrefixes)
    {
        AgentEvalCondition? control = manifest.Conditions.FirstOrDefault(c => !c.RoslynToolsEnabled);
        AgentEvalCondition? treatment = manifest.Conditions.FirstOrDefault(c => c.RoslynToolsEnabled);
        if (control is null || treatment is null)
        {
            return new List<AgentEvalTaskComparison>();
        }

        List<AgentEvalTaskComparison> comparisons = new(capacity: manifest.Tasks.Count);
        foreach (AgentEvalTask task in manifest.Tasks)
        {
            AgentEvalRun[] controlRuns = runs
                .Where(r =>
                    string.Equals(r.TaskId, task.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ConditionId, control.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            AgentEvalRun[] treatmentRuns = runs
                .Where(r =>
                    string.Equals(r.TaskId, task.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ConditionId, treatment.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            AgentEvalRunMetrics controlMetrics = BuildRunMetrics(controlRuns, roslynToolPrefixes);
            AgentEvalRunMetrics treatmentMetrics = BuildRunMetrics(treatmentRuns, roslynToolPrefixes);

            bool sufficientData = controlMetrics.run_count > 0 && treatmentMetrics.run_count > 0;
            comparisons.Add(new AgentEvalTaskComparison(
                task_id: task.Id,
                task_title: task.Title,
                sufficient_data: sufficientData,
                control_run_count: controlMetrics.run_count,
                treatment_run_count: treatmentMetrics.run_count,
                success_rate_delta: sufficientData ? treatmentMetrics.success_rate - controlMetrics.success_rate : null,
                compile_rate_delta: sufficientData ? treatmentMetrics.compile_rate - controlMetrics.compile_rate : null,
                tests_rate_delta: sufficientData ? treatmentMetrics.tests_rate - controlMetrics.tests_rate : null,
                treatment_roslyn_used_rate: treatmentMetrics.run_count > 0 ? treatmentMetrics.roslyn_used_rate : null,
                average_total_tokens_delta: TrySubtract(treatmentMetrics.average_total_tokens, controlMetrics.average_total_tokens),
                note: sufficientData ? null : "Insufficient task-level data for control/treatment comparison."));
        }

        return comparisons;
    }

    private static AgentEvalRunMetrics BuildRunMetrics(
        IReadOnlyList<AgentEvalRun> runs,
        IReadOnlyList<string> roslynToolPrefixes)
    {
        int runCount = runs.Count;
        if (runCount == 0)
        {
            return new AgentEvalRunMetrics(
                run_count: 0,
                success_rate: 0,
                compile_rate: 0,
                tests_rate: 0,
                average_duration_seconds: 0,
                roslyn_used_runs: 0,
                roslyn_used_rate: 0,
                roslyn_call_share: 0,
                average_roslyn_helpfulness_score: null,
                runs_with_token_counts: 0,
                average_total_tokens: null,
                median_total_tokens: null);
        }

        int successCount = runs.Count(r => r.Succeeded);
        int compileCount = runs.Count(r => r.CompilePassed);
        int testsCount = runs.Count(r => r.TestsPassed);
        double avgDuration = runs.Average(r => r.DurationSeconds);

        int totalToolCalls = runs.Sum(r => r.ToolCalls.Count);
        int roslynToolCalls = runs.Sum(r => r.ToolCalls.Count(c => IsRoslynTool(c.ToolName, roslynToolPrefixes)));
        int roslynUsedRuns = runs.Count(r => r.ToolCalls.Any(c => IsRoslynTool(c.ToolName, roslynToolPrefixes)));

        int[] reflectionScores = runs
            .Select(r => r.PostRunReflection?.RoslynHelpfulnessScore)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToArray();

        double[] totalTokens = runs
            .Select(TryGetTotalTokens)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();

        double? avgReflection = reflectionScores.Length == 0 ? null : reflectionScores.Average();
        double roslynCallShare = totalToolCalls == 0 ? 0 : (double)roslynToolCalls / totalToolCalls;
        double? avgTotalTokens = totalTokens.Length == 0 ? null : totalTokens.Average();
        double? medianTotalTokens = totalTokens.Length == 0 ? null : ComputeMedian(totalTokens);

        return new AgentEvalRunMetrics(
            run_count: runCount,
            success_rate: (double)successCount / runCount,
            compile_rate: (double)compileCount / runCount,
            tests_rate: (double)testsCount / runCount,
            average_duration_seconds: avgDuration,
            roslyn_used_runs: roslynUsedRuns,
            roslyn_used_rate: (double)roslynUsedRuns / runCount,
            roslyn_call_share: roslynCallShare,
            average_roslyn_helpfulness_score: avgReflection,
            runs_with_token_counts: totalTokens.Length,
            average_total_tokens: avgTotalTokens,
            median_total_tokens: medianTotalTokens);
    }

    private static double? TryGetTotalTokens(AgentEvalRun run)
    {
        if (run.TotalTokens.HasValue)
        {
            return run.TotalTokens.Value;
        }

        if (run.PromptTokens.HasValue && run.CompletionTokens.HasValue)
        {
            return run.PromptTokens.Value + run.CompletionTokens.Value;
        }

        return null;
    }

    private static double? TrySubtract(double? left, double? right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return null;
        }

        return left.Value - right.Value;
    }

    private static double? TryComputeReductionRatio(double? controlTokens, double? treatmentTokens)
    {
        if (!controlTokens.HasValue || !treatmentTokens.HasValue)
        {
            return null;
        }

        if (controlTokens.Value <= 0)
        {
            return null;
        }

        return (controlTokens.Value - treatmentTokens.Value) / controlTokens.Value;
    }

    private static double ComputeMedian(double[] values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        if (sorted.Length % 2 == 0)
        {
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        return sorted[mid];
    }

    private sealed record AgentEvalRunMetrics(
        int run_count,
        double success_rate,
        double compile_rate,
        double tests_rate,
        double average_duration_seconds,
        int roslyn_used_runs,
        double roslyn_used_rate,
        double roslyn_call_share,
        double? average_roslyn_helpfulness_score,
        int runs_with_token_counts,
        double? average_total_tokens,
        double? median_total_tokens);
}

