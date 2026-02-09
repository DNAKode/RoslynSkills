using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class SessionDiffCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.diff",
        Summary: "Return a bounded line-level diff between original and current session content.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

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

        int maxChanges = InputParsing.GetOptionalInt(input, "max_changes", defaultValue: 200, minValue: 1, maxValue: 10_000);
        if (!RoslynSessionStore.TryGet(sessionId, out RoslynSession? session) || session is null)
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("session_not_found", $"Session '{sessionId}' was not found.") }));
        }

        SessionDiffResult diff = session.BuildDiff(maxChanges);
        SessionStatus status = session.GetStatus();
        object data = new
        {
            session_id = diff.session_id,
            file_path = diff.file_path,
            generation = status.generation,
            total_changed_lines = diff.total_changed_lines,
            returned_changed_lines = diff.returned_changed_lines,
            truncated = diff.truncated,
            changes = diff.changes,
            status = new
            {
                sync_state = status.sync_state,
                recommended_action = status.recommended_action,
                disk_exists = status.disk_exists,
                disk_matches_open = status.disk_matches_open,
                disk_matches_current = status.disk_matches_current,
                open_disk_hash = status.open_disk_hash,
                current_content_hash = status.current_content_hash,
                disk_hash = status.disk_hash,
            },
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
