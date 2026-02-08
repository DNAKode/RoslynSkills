using System.Text.Json;

namespace RoslynAgent.Benchmark.AgentEval;

public sealed class AgentEvalRunTemplateGenerator
{
    public async Task<AgentEvalTemplateGenerationReport> GenerateAsync(
        string manifestPath,
        string runsDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        AgentEvalManifest manifest = AgentEvalStorage.LoadManifest(manifestPath);
        AgentEvalWorklistBuilder worklistBuilder = new();
        AgentEvalWorklistReport worklist = await worklistBuilder.BuildAsync(
            manifestPath,
            runsDirectory,
            outputDirectory,
            cancellationToken).ConfigureAwait(false);

        string templatesDirectory = Path.GetFullPath(Path.Combine(outputDirectory, "pending-run-templates"));
        Directory.CreateDirectory(templatesDirectory);

        int created = 0;
        foreach (AgentEvalPendingRun pending in worklist.pending_runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AgentEvalCondition condition = manifest.Conditions.First(c =>
                string.Equals(c.Id, pending.condition_id, StringComparison.OrdinalIgnoreCase));

            AgentEvalRun template = new(
                RunId: pending.suggested_run_id,
                TaskId: pending.task_id,
                ConditionId: pending.condition_id,
                Replicate: pending.replicate,
                Agent: "TODO-agent-id",
                Model: "TODO-model-id",
                Succeeded: false,
                CompilePassed: false,
                TestsPassed: false,
                DurationSeconds: 0,
                ToolsOffered: BuildSuggestedTools(condition),
                ToolCalls: Array.Empty<AgentToolCall>(),
                PostRunReflection: new AgentPostRunReflection(
                    Summary: "TODO concise run summary.",
                    HelpfulTools: Array.Empty<string>(),
                    UnhelpfulTools: Array.Empty<string>(),
                    RoslynHelpfulnessScore: condition.RoslynToolsEnabled ? 3 : null));

            string json = JsonSerializer.Serialize(template, AgentEvalStorage.SerializerOptions);
            string fileName = $"{pending.suggested_run_id}.json";
            string fullPath = Path.Combine(templatesDirectory, fileName);
            await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
            created++;
        }

        return new AgentEvalTemplateGenerationReport(
            experiment_id: manifest.ExperimentId,
            generated_utc: DateTimeOffset.UtcNow,
            pending_count: worklist.pending_runs.Count,
            template_files_created: created,
            templates_directory: templatesDirectory,
            worklist_path: worklist.output_path);
    }

    private static IReadOnlyList<string> BuildSuggestedTools(AgentEvalCondition condition)
    {
        if (condition.RoslynToolsEnabled)
        {
            return new[] { "read_file", "run_shell", "search", "roslyn-agent.run" };
        }

        return new[] { "read_file", "run_shell", "search" };
    }
}

public sealed record AgentEvalTemplateGenerationReport(
    string experiment_id,
    DateTimeOffset generated_utc,
    int pending_count,
    int template_files_created,
    string templates_directory,
    string worklist_path);
