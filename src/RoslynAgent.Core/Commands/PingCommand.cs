using System.Text.Json;
using RoslynAgent.Contracts;

namespace RoslynAgent.Core.Commands;

public sealed class PingCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "system.ping",
        Summary: "Health command for validating CLI and command execution plumbing.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
        => Array.Empty<CommandError>();

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        object data = new
        {
            utc = DateTimeOffset.UtcNow,
            process = Environment.ProcessId,
            framework = Environment.Version.ToString(),
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
