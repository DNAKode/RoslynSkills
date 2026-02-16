using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSkills.Contracts;

public sealed record CommandDescriptor(
    string Id,
    string Summary,
    string InputSchemaVersion,
    string OutputSchemaVersion,
    bool MutatesState,
    string Maturity = CommandMaturity.Stable,
    IReadOnlyList<string>? Traits = null);

public static class CommandMaturity
{
    public const string Stable = "stable";
    public const string Advanced = "advanced";
    public const string Experimental = "experimental";
}

public static class CommandTrait
{
    public const string Heuristic = "heuristic";
    public const string PotentiallySlow = "potentially_slow";
    public const string DerivedAnalysis = "derived_analysis";
    public const string BatchOrchestration = "batch_orchestration";
}

public sealed record CommandError(
    string Code,
    string Message,
    object? Details = null);

public sealed record CommandEnvelope(
    [property: JsonPropertyOrder(-100)] bool Ok,
    [property: JsonPropertyOrder(-90)] string CommandId,
    [property: JsonPropertyOrder(-80)] string Version,
    [property: JsonPropertyOrder(-60)] object? Data,
    [property: JsonPropertyOrder(0)] IReadOnlyList<CommandError> Errors,
    [property: JsonPropertyOrder(90)] string? TraceId,
    [property: JsonPropertyOrder(-70)] string? Preview = null,
    [property: JsonPropertyOrder(100)] string? Summary = null);

public sealed record CommandExecutionResult(
    object? Data,
    IReadOnlyList<CommandError> Errors)
{
    public bool Ok => Errors.Count == 0;
}

public interface IAgentCommand
{
    CommandDescriptor Descriptor { get; }

    IReadOnlyList<CommandError> Validate(JsonElement input);

    Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken);
}

