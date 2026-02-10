namespace RoslynSkills.Benchmark.AgentEval;

public sealed class AgentEvalRunRegistrar
{
    public Task<string> RegisterAsync(
        string manifestPath,
        string runsDirectory,
        AgentEvalRunRegistration registration,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentEvalManifest manifest = AgentEvalStorage.LoadManifest(manifestPath);
        AgentEvalValidation.ValidateRuns(manifest, Array.Empty<AgentEvalRun>());

        AgentEvalTask task = manifest.Tasks.FirstOrDefault(t =>
            string.Equals(t.Id, registration.TaskId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown task id '{registration.TaskId}'.");

        AgentEvalCondition condition = manifest.Conditions.FirstOrDefault(c =>
            string.Equals(c.Id, registration.ConditionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown condition id '{registration.ConditionId}'.");

        if (!Directory.Exists(runsDirectory))
        {
            Directory.CreateDirectory(runsDirectory);
        }

        string runId = string.IsNullOrWhiteSpace(registration.RunId)
            ? $"{registration.TaskId}__{registration.ConditionId}__r{registration.Replicate:00}"
            : registration.RunId;

        string outputPath = Path.Combine(runsDirectory, $"{runId}.json");
        if (File.Exists(outputPath) && !overwrite)
        {
            throw new InvalidOperationException(
                $"Run output '{outputPath}' already exists. Use overwrite=true to replace.");
        }

        AgentEvalRun run = new(
            RunId: runId,
            TaskId: registration.TaskId,
            ConditionId: registration.ConditionId,
            Replicate: registration.Replicate,
            Agent: registration.Agent,
            Model: registration.Model,
            Succeeded: registration.Succeeded,
            CompilePassed: registration.CompilePassed,
            TestsPassed: registration.TestsPassed,
            DurationSeconds: registration.DurationSeconds,
            PromptTokens: registration.PromptTokens,
            CompletionTokens: registration.CompletionTokens,
            TotalTokens: registration.TotalTokens,
            ToolsOffered: registration.ToolsOffered,
            ToolCalls: registration.ToolCalls,
            Context: new AgentEvalRunContext(
                TaskTitle: task.Title,
                Repo: task.Repo,
                RepoUrl: task.RepoUrl,
                Commit: task.Commit,
                AcceptanceChecks: task.AcceptanceChecks,
                TaskPromptFile: task.TaskPromptFile),
            PostRunReflection: new AgentPostRunReflection(
                Summary: registration.ReflectionSummary ?? string.Empty,
                HelpfulTools: registration.ReflectionHelpfulTools,
                UnhelpfulTools: registration.ReflectionUnhelpfulTools,
                RoslynHelpfulnessScore: condition.RoslynToolsEnabled
                    ? registration.RoslynHelpfulnessScore
                    : null));

        AgentEvalStorage.WriteJson(outputPath, run);
        return Task.FromResult(Path.GetFullPath(outputPath));
    }
}

public sealed record AgentEvalRunRegistration(
    string RunId,
    string TaskId,
    string ConditionId,
    int Replicate,
    string Agent,
    string Model,
    bool Succeeded,
    bool CompilePassed,
    bool TestsPassed,
    double DurationSeconds,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    IReadOnlyList<string> ToolsOffered,
    IReadOnlyList<AgentToolCall> ToolCalls,
    string? ReflectionSummary,
    IReadOnlyList<string> ReflectionHelpfulTools,
    IReadOnlyList<string> ReflectionUnhelpfulTools,
    int? RoslynHelpfulnessScore);

