namespace RoslynAgent.Benchmark.AgentEval;

public sealed class AgentEvalWorklistBuilder
{
    public Task<AgentEvalWorklistReport> BuildAsync(
        string manifestPath,
        string runsDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentEvalManifest manifest = AgentEvalStorage.LoadManifest(manifestPath);
        AgentEvalRun[] runs = AgentEvalStorage.LoadRuns(runsDirectory);

        AgentEvalValidation.ValidateRuns(manifest, runs);

        List<AgentEvalCellSummary> cells = new();
        List<AgentEvalPendingRun> pendingRuns = new();

        foreach (AgentEvalTask task in manifest.Tasks)
        {
            foreach (AgentEvalCondition condition in manifest.Conditions)
            {
                AgentEvalRun[] cellRuns = runs
                    .Where(r =>
                        string.Equals(r.TaskId, task.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.ConditionId, condition.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                int observedCount = cellRuns.Length;
                int targetCount = manifest.RunsPerCell;
                int missingCount = Math.Max(0, targetCount - observedCount);

                cells.Add(new AgentEvalCellSummary(
                    task_id: task.Id,
                    condition_id: condition.Id,
                    observed_runs: observedCount,
                    target_runs: targetCount,
                    missing_runs: missingCount));

                if (missingCount > 0)
                {
                    HashSet<int> occupiedReplicates = cellRuns
                        .Select(r => r.Replicate)
                        .Where(r => r.HasValue && r.Value > 0)
                        .Select(r => r!.Value)
                        .ToHashSet();

                    int nextReplicate = 1;
                    for (int i = 0; i < missingCount; i++)
                    {
                        while (occupiedReplicates.Contains(nextReplicate))
                        {
                            nextReplicate++;
                        }

                        pendingRuns.Add(new AgentEvalPendingRun(
                            task_id: task.Id,
                            condition_id: condition.Id,
                            replicate: nextReplicate,
                            suggested_run_id: $"{task.Id}__{condition.Id}__r{nextReplicate:00}"));

                        occupiedReplicates.Add(nextReplicate);
                        nextReplicate++;
                    }
                }
            }
        }

        int expectedRuns = manifest.Tasks.Count * manifest.Conditions.Count * manifest.RunsPerCell;
        int observedRuns = runs.Length;
        double completion = expectedRuns == 0 ? 0 : Math.Min(1.0, (double)observedRuns / expectedRuns);

        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-worklist.json"));
        AgentEvalWorklistReport report = new(
            experiment_id: manifest.ExperimentId,
            generated_utc: DateTimeOffset.UtcNow,
            runs_per_cell: manifest.RunsPerCell,
            expected_runs: expectedRuns,
            observed_runs: observedRuns,
            completion_rate: completion,
            cells: cells,
            pending_runs: pendingRuns,
            output_path: outputPath);

        AgentEvalStorage.WriteJson(outputPath, report);
        return Task.FromResult(report);
    }

}
