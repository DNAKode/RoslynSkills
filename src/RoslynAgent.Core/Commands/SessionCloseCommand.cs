using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class SessionCloseCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.close",
        Summary: "Close and discard an in-memory session.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "session_id", errors, out _);
        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "session_id", errors, out string sessionId))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        if (!RoslynSessionStore.TryRemove(sessionId, out _))
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("session_not_found", $"Session '{sessionId}' was not found.") }));
        }

        object data = new
        {
            session_id = sessionId,
            closed = true,
            store_session_count = RoslynSessionStore.Count,
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
