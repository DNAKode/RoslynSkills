namespace RoslynSkills.Benchmark.AgentEval;

public sealed class AgentEvalRunValidator
{
    public Task<AgentEvalRunValidationReport> ValidateAsync(
        string manifestPath,
        string runsDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentEvalManifest manifest = AgentEvalStorage.LoadManifest(manifestPath);
        AgentEvalRun[] runs = AgentEvalStorage.LoadRuns(runsDirectory);

        Dictionary<string, AgentEvalTask> tasksById = manifest.Tasks
            .ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AgentEvalCondition> conditionsById = manifest.Conditions
            .ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        List<AgentEvalRunValidationIssue> issues = new();
        HashSet<string> seenRunIds = new(StringComparer.OrdinalIgnoreCase);

        int contaminatedControlRuns = 0;
        int treatmentRunsWithoutRoslynOffered = 0;
        int treatmentRunsWithoutRoslynUsage = 0;
        int runsWithTokenCounts = 0;
        int runsMissingTokenCounts = 0;

        foreach (AgentEvalRun run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string runId = string.IsNullOrWhiteSpace(run.RunId) ? "<missing-run-id>" : run.RunId;
            string taskId = string.IsNullOrWhiteSpace(run.TaskId) ? "<missing-task-id>" : run.TaskId;
            string conditionId = string.IsNullOrWhiteSpace(run.ConditionId) ? "<missing-condition-id>" : run.ConditionId;

            if (string.IsNullOrWhiteSpace(run.RunId))
            {
                AddIssue(issues, "error", runId, taskId, conditionId, "run_id is required.");
            }
            else if (!seenRunIds.Add(run.RunId))
            {
                AddIssue(issues, "error", runId, taskId, conditionId, "Duplicate run_id detected.");
            }

            bool knownTask = tasksById.TryGetValue(run.TaskId, out AgentEvalTask? task);
            bool knownCondition = conditionsById.TryGetValue(run.ConditionId, out AgentEvalCondition? condition);

            if (!knownTask)
            {
                AddIssue(issues, "error", runId, taskId, conditionId, $"Unknown task_id '{run.TaskId}'.");
            }

            if (!knownCondition)
            {
                AddIssue(issues, "error", runId, taskId, conditionId, $"Unknown condition_id '{run.ConditionId}'.");
            }

            if (run.Replicate.HasValue && run.Replicate.Value <= 0)
            {
                AddIssue(issues, "error", runId, taskId, conditionId, "replicate must be greater than zero when provided.");
            }

            if (string.IsNullOrWhiteSpace(run.Agent))
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "agent is empty; runner provenance is unclear.");
            }

            if (string.IsNullOrWhiteSpace(run.Model))
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "model is empty; model-level analysis is incomplete.");
            }

            if (run.DurationSeconds < 0)
            {
                AddIssue(issues, "error", runId, taskId, conditionId, "duration_seconds cannot be negative.");
            }
            else if (run.DurationSeconds == 0)
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "duration_seconds is zero; timing metrics may be unreliable.");
            }

            if (run.ToolsOffered.Count == 0)
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "tools_offered is empty; tool availability analysis is incomplete.");
            }

            HashSet<string> offeredToolSet = run.ToolsOffered
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (AgentToolCall call in run.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(call.ToolName))
                {
                    AddIssue(issues, "error", runId, taskId, conditionId, "tool_calls includes an empty tool_name.");
                    continue;
                }

                if (!offeredToolSet.Contains(call.ToolName))
                {
                    AddIssue(issues, "warning", runId, taskId, conditionId, $"tool_calls contains '{call.ToolName}' which is not listed in tools_offered.");
                }
            }

            bool roslynOffered = run.ToolsOffered.Any(t => IsRoslynTool(t, manifest.RoslynToolPrefixes));
            bool roslynUsed = run.ToolCalls.Any(c => IsRoslynTool(c.ToolName, manifest.RoslynToolPrefixes));

            if (knownCondition)
            {
                if (!condition!.RoslynToolsEnabled)
                {
                    if (roslynOffered || roslynUsed)
                    {
                        contaminatedControlRuns++;
                        AddIssue(issues, "warning", runId, taskId, conditionId, "Control run shows Roslyn availability/usage; condition contamination detected.");
                    }

                    if (run.PostRunReflection?.RoslynHelpfulnessScore is not null)
                    {
                        AddIssue(issues, "warning", runId, taskId, conditionId, "Control run contains roslyn_helpfulness_score; expected null.");
                    }
                }
                else
                {
                    if (!roslynOffered)
                    {
                        treatmentRunsWithoutRoslynOffered++;
                        AddIssue(issues, "warning", runId, taskId, conditionId, "Treatment run does not offer Roslyn tools.");
                    }

                    if (!roslynUsed)
                    {
                        treatmentRunsWithoutRoslynUsage++;
                        AddIssue(issues, "warning", runId, taskId, conditionId, "Treatment run did not use any Roslyn tool.");
                    }
                }
            }

            int? roslynHelpfulness = run.PostRunReflection?.RoslynHelpfulnessScore;
            if (roslynHelpfulness.HasValue && (roslynHelpfulness.Value < 1 || roslynHelpfulness.Value > 5))
            {
                AddIssue(issues, "error", runId, taskId, conditionId, "roslyn_helpfulness_score must be in the range 1..5.");
            }

            if (knownCondition && condition!.RoslynToolsEnabled && roslynUsed && !roslynHelpfulness.HasValue)
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "Treatment run used Roslyn tool(s) but omitted roslyn_helpfulness_score.");
            }

            if (run.PostRunReflection is null)
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "post_run_reflection is missing.");
            }
            else if (string.IsNullOrWhiteSpace(run.PostRunReflection.Summary))
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "post_run_reflection.summary is empty.");
            }

            if (run.Succeeded && (!run.CompilePassed || !run.TestsPassed))
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "succeeded=true while compile_passed or tests_passed is false.");
            }

            if (!run.CompilePassed && run.TestsPassed)
            {
                AddIssue(issues, "warning", runId, taskId, conditionId, "tests_passed=true while compile_passed=false is inconsistent.");
            }

            if (knownTask)
            {
                ValidateContext(issues, run, task!, runId, taskId, conditionId);
            }

            if (TryGetTotalTokens(run).HasValue)
            {
                runsWithTokenCounts++;
            }
            else
            {
                runsMissingTokenCounts++;
            }
        }

        int expectedRuns = manifest.Tasks.Count * manifest.Conditions.Count * manifest.RunsPerCell;
        if (runs.Length < expectedRuns)
        {
            AddIssue(
                issues,
                "warning",
                "<summary>",
                "<summary>",
                "<summary>",
                $"Observed run count ({runs.Length}) is below expected count ({expectedRuns}).");
        }

        foreach (AgentEvalTask task in manifest.Tasks)
        {
            foreach (AgentEvalCondition condition in manifest.Conditions)
            {
                int cellCount = runs.Count(r =>
                    string.Equals(r.TaskId, task.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ConditionId, condition.Id, StringComparison.OrdinalIgnoreCase));

                if (cellCount > manifest.RunsPerCell)
                {
                    AddIssue(
                        issues,
                        "warning",
                        "<summary>",
                        task.Id,
                        condition.Id,
                        $"Observed {cellCount} runs but target runs_per_cell is {manifest.RunsPerCell}.");
                }
            }
        }

        int errorCount = issues.Count(i => string.Equals(i.severity, "error", StringComparison.OrdinalIgnoreCase));
        int warningCount = issues.Count - errorCount;
        bool valid = errorCount == 0;

        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-run-validation.json"));
        AgentEvalRunValidationReport report = new(
            experiment_id: manifest.ExperimentId,
            generated_utc: DateTimeOffset.UtcNow,
            valid: valid,
            total_runs: runs.Length,
            expected_runs: expectedRuns,
            issue_count: issues.Count,
            error_count: errorCount,
            warning_count: warningCount,
            contaminated_control_runs: contaminatedControlRuns,
            treatment_runs_without_roslyn_offered: treatmentRunsWithoutRoslynOffered,
            treatment_runs_without_roslyn_usage: treatmentRunsWithoutRoslynUsage,
            issues: issues,
            output_path: outputPath,
            runs_with_token_counts: runsWithTokenCounts,
            runs_missing_token_counts: runsMissingTokenCounts);

        AgentEvalStorage.WriteJson(outputPath, report);
        return Task.FromResult(report);
    }

    private static void ValidateContext(
        List<AgentEvalRunValidationIssue> issues,
        AgentEvalRun run,
        AgentEvalTask task,
        string runId,
        string taskId,
        string conditionId)
    {
        if (run.Context is null)
        {
            AddIssue(issues, "warning", runId, taskId, conditionId, "context is missing.");
            return;
        }

        if (!string.Equals(run.Context.Repo, task.Repo, StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(issues, "warning", runId, taskId, conditionId, $"context.repo '{run.Context.Repo}' differs from manifest repo '{task.Repo}'.");
        }

        if (!string.Equals(run.Context.Commit, task.Commit, StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(issues, "warning", runId, taskId, conditionId, $"context.commit '{run.Context.Commit}' differs from manifest commit '{task.Commit}'.");
        }

        if (run.Context.AcceptanceChecks.Count == 0)
        {
            AddIssue(issues, "warning", runId, taskId, conditionId, "context.acceptance_checks is empty.");
        }
    }

    private static bool IsRoslynTool(string toolName, IReadOnlyList<string> prefixes)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        foreach (string prefix in prefixes)
        {
            if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static void AddIssue(
        List<AgentEvalRunValidationIssue> issues,
        string severity,
        string runId,
        string taskId,
        string conditionId,
        string message)
    {
        issues.Add(new AgentEvalRunValidationIssue(
            severity: severity,
            run_id: runId,
            task_id: taskId,
            condition_id: conditionId,
            message: message));
    }
}

