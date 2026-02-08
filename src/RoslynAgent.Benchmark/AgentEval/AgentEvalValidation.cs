namespace RoslynAgent.Benchmark.AgentEval;

internal static class AgentEvalValidation
{
    public static void ValidateManifest(AgentEvalManifest manifest)
    {
        if (manifest.Conditions.Count == 0)
        {
            throw new InvalidOperationException("Manifest must include at least one condition.");
        }

        if (manifest.Tasks.Count == 0)
        {
            throw new InvalidOperationException("Manifest must include at least one task.");
        }

        EnsureUnique(
            manifest.Conditions.Select(c => c.Id),
            "condition id");
        EnsureUnique(
            manifest.Tasks.Select(t => t.Id),
            "task id");

        int controlCount = manifest.Conditions.Count(c => !c.RoslynToolsEnabled);
        int treatmentCount = manifest.Conditions.Count(c => c.RoslynToolsEnabled);
        if (controlCount == 0 || treatmentCount == 0)
        {
            throw new InvalidOperationException(
                "Manifest should include at least one control condition and one Roslyn-enabled treatment condition.");
        }

        foreach (AgentEvalTask task in manifest.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Repo))
            {
                throw new InvalidOperationException($"Task '{task.Id}' has an empty repo value.");
            }

            if (string.IsNullOrWhiteSpace(task.Commit))
            {
                throw new InvalidOperationException($"Task '{task.Id}' has an empty commit value.");
            }

            if (task.AcceptanceChecks.Count == 0)
            {
                throw new InvalidOperationException($"Task '{task.Id}' must define at least one acceptance check.");
            }
        }
    }

    public static void ValidateRuns(AgentEvalManifest manifest, IReadOnlyList<AgentEvalRun> runs)
    {
        HashSet<string> conditionIds = manifest.Conditions.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> taskIds = manifest.Tasks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> runIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (AgentEvalRun run in runs)
        {
            if (!runIds.Add(run.RunId))
            {
                throw new InvalidOperationException($"Duplicate run_id found: '{run.RunId}'.");
            }

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

            if (run.Replicate.HasValue && run.Replicate <= 0)
            {
                throw new InvalidOperationException(
                    $"Run '{run.RunId}' has invalid replicate value '{run.Replicate}'.");
            }
        }
    }

    private static void EnsureUnique(IEnumerable<string> ids, string label)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Duplicate {label} found: '{id}'.");
            }
        }
    }
}
