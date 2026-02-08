using System.Text.Json;

namespace RoslynAgent.Contracts;

public sealed record CommandDescriptor(
    string Id,
    string Summary,
    string InputSchemaVersion,
    string OutputSchemaVersion,
    bool MutatesState);

public sealed record CommandError(
    string Code,
    string Message,
    object? Details = null);

public sealed record CommandEnvelope(
    bool Ok,
    string CommandId,
    string Version,
    object? Data,
    IReadOnlyList<CommandError> Errors,
    string? TraceId);

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
