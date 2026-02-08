using System.Text.Json;

namespace RoslynAgent.Benchmark.AgentEval;

public sealed class AgentEvalScorer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AgentEvalReport> ScoreAsync(
        string manifestPath,
        string runsDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        AgentEvalManifest manifest = LoadManifest(manifestPath);
        AgentEvalRun[] runs = LoadRuns(runsDirectory);
        ValidateRuns(manifest, runs);

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

        string json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static AgentEvalManifest LoadManifest(string manifestPath)
    {
        string fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Agent eval manifest was not found.", fullPath);
        }

        string json = File.ReadAllText(fullPath);
        AgentEvalManifest? manifest = JsonSerializer.Deserialize<AgentEvalManifest>(json, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException("Failed to parse agent eval manifest.");
        }

        return manifest;
    }

    private static AgentEvalRun[] LoadRuns(string runsDirectory)
    {
        string fullDirectory = Path.GetFullPath(runsDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"Runs directory '{fullDirectory}' does not exist.");
        }

        string[] files = Directory.GetFiles(fullDirectory, "*.json", SearchOption.TopDirectoryOnly);
        List<AgentEvalRun> runs = new();
        foreach (string file in files)
        {
            string json = File.ReadAllText(file);
            AgentEvalRun? run = JsonSerializer.Deserialize<AgentEvalRun>(json, JsonOptions);
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return runs.ToArray();
    }

    private static void ValidateRuns(AgentEvalManifest manifest, IReadOnlyList<AgentEvalRun> runs)
    {
        HashSet<string> conditionIds = manifest.Conditions.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> taskIds = manifest.Tasks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (AgentEvalRun run in runs)
        {
            if (!conditionIds.Contains(run.ConditionId))
            {
                throw new InvalidOperationException(
                    $"Run '{run.RunId}' references unknown condition id '{run.ConditionId}'.");
            }

            if (!taskIds.Contains(run.TaskId))
            {
                throw new InvalidOperationException(
                    $"Run '{run.RunId}' references unknown task id '{run.TaskId}'.");
            }
        }
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

        return new AgentEvalComparison(
            control_condition_id: control.Id,
            treatment_condition_id: treatment.Id,
            success_rate_delta: treatmentSummary.success_rate - controlSummary.success_rate,
            compile_rate_delta: treatmentSummary.compile_rate - controlSummary.compile_rate,
            tests_rate_delta: treatmentSummary.tests_rate - controlSummary.tests_rate,
            roslyn_used_rate_in_treatment: treatmentSummary.roslyn_used_rate);
    }
}
