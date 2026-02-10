namespace RoslynSkills.Benchmark;

public sealed class DryRunRunner : IAgentRunner
{
    public string Id => "dry-run";

    public Task<RunRecord> ExecuteAsync(Scenario scenario, RunConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RunRecord record = new(
            ScenarioId: scenario.Id,
            Config: config,
            Duration: TimeSpan.Zero,
            Success: true,
            Invocations: Array.Empty<CommandInvocation>());

        return Task.FromResult(record);
    }
}

