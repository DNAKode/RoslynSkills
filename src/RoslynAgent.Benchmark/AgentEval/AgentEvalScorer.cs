using System.Text.Json;

namespace RoslynAgent.Benchmark.AgentEval;

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
        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-report.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        AgentEvalReport report = new(
            experiment_id: manifest.ExperimentId,
            generated_utc: DateTimeOffset.UtcNow,
            total_runs: runs.Length,
            condition_summaries: summaries,
            primary_comparison: comparison,
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

        int runCount = conditionRuns.Length;
        if (runCount == 0)
        {
            return new AgentEvalConditionSummary(
                condition_id: condition.Id,
                condition_name: condition.Name,
                run_count: 0,
                success_rate: 0,
                compile_rate: 0,
                tests_rate: 0,
                average_duration_seconds: 0,
                roslyn_used_runs: 0,
                roslyn_used_rate: 0,
                roslyn_call_share: 0,
                average_roslyn_helpfulness_score: null);
        }

        int successCount = conditionRuns.Count(r => r.Succeeded);
        int compileCount = conditionRuns.Count(r => r.CompilePassed);
        int testsCount = conditionRuns.Count(r => r.TestsPassed);
        double avgDuration = conditionRuns.Average(r => r.DurationSeconds);

        int totalToolCalls = conditionRuns.Sum(r => r.ToolCalls.Count);
        int roslynToolCalls = conditionRuns.Sum(r => r.ToolCalls.Count(c => IsRoslynTool(c.ToolName, roslynToolPrefixes)));
        int roslynUsedRuns = conditionRuns.Count(r => r.ToolCalls.Any(c => IsRoslynTool(c.ToolName, roslynToolPrefixes)));

        int[] reflectionScores = conditionRuns
            .Select(r => r.PostRunReflection?.RoslynHelpfulnessScore)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToArray();

        double? avgReflection = reflectionScores.Length == 0 ? null : reflectionScores.Average();
        double roslynCallShare = totalToolCalls == 0 ? 0 : (double)roslynToolCalls / totalToolCalls;

        return new AgentEvalConditionSummary(
            condition_id: condition.Id,
            condition_name: condition.Name,
            run_count: runCount,
            success_rate: (double)successCount / runCount,
            compile_rate: (double)compileCount / runCount,
            tests_rate: (double)testsCount / runCount,
            average_duration_seconds: avgDuration,
            roslyn_used_runs: roslynUsedRuns,
            roslyn_used_rate: (double)roslynUsedRuns / runCount,
            roslyn_call_share: roslynCallShare,
            average_roslyn_helpfulness_score: avgReflection);
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
            note: null);
    }
}
