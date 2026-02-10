namespace RoslynSkills.Benchmark;

public sealed record Scenario(
    string Id,
    string Description,
    string FixturePath,
    IReadOnlyList<string> AcceptanceChecks);

public sealed record RunConfig(
    string RunnerId,
    string ModelId,
    string InterfaceMode,
    int Seed);

public sealed record CommandInvocation(
    DateTimeOffset TimestampUtc,
    string CommandId,
    string InputJson,
    string OutputJson,
    bool Ok);

public sealed record RunRecord(
    string ScenarioId,
    RunConfig Config,
    TimeSpan Duration,
    bool Success,
    IReadOnlyList<CommandInvocation> Invocations);

public interface IAgentRunner
{
    string Id { get; }

    Task<RunRecord> ExecuteAsync(Scenario scenario, RunConfig config, CancellationToken cancellationToken);
}

