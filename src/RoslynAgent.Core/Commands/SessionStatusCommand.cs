using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class SessionStatusCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.status",
        Summary: "Return synchronization and reliability status for a session.",
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

        bool includeDiagnostics = InputParsing.GetOptionalBool(input, "include_diagnostics", defaultValue: false);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 25, minValue: 1, maxValue: 500);
        if (!RoslynSessionStore.TryGet(sessionId, out RoslynSession? session) || session is null)
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("session_not_found", $"Session '{sessionId}' was not found.") }));
        }

        SessionStatus status = session.GetStatus();
        object? diagnostics = null;
        if (includeDiagnostics)
        {
            SessionSnapshot snapshot = session.BuildSnapshot(maxDiagnostics, cancellationToken);
            diagnostics = new
            {
                total = snapshot.total_diagnostics,
                returned = snapshot.returned_diagnostics,
                errors = snapshot.errors,
                warnings = snapshot.warnings,
                items = snapshot.diagnostics,
            };
        }

        object data = new
        {
            session_id = sessionId,
            file_path = status.file_path,
            generation = status.generation,
            has_changes = status.has_changes,
            sync_state = status.sync_state,
            recommended_action = status.recommended_action,
            disk_exists = status.disk_exists,
            disk_matches_open = status.disk_matches_open,
            disk_matches_current = status.disk_matches_current,
            open_disk_hash = status.open_disk_hash,
            current_content_hash = status.current_content_hash,
            disk_hash = status.disk_hash,
            diagnostics,
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
