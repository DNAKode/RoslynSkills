using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class SessionGetDiagnosticsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.get_diagnostics",
        Summary: "Return diagnostics for the current in-memory session snapshot.",
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

        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 100, minValue: 1, maxValue: 2_000);
        if (!RoslynSessionStore.TryGet(sessionId, out RoslynSession? session) || session is null)
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("session_not_found", $"Session '{sessionId}' was not found.") }));
        }

        SessionSnapshot snapshot = session.BuildSnapshot(maxDiagnostics, cancellationToken);
        SessionStatus status = session.GetStatus();
        object data = new
        {
            session_id = sessionId,
            file_path = snapshot.file_path,
            generation = snapshot.generation,
            line_count = snapshot.line_count,
            character_count = snapshot.character_count,
            has_changes = snapshot.has_changes,
            diagnostics = new
            {
                total = snapshot.total_diagnostics,
                returned = snapshot.returned_diagnostics,
                errors = snapshot.errors,
                warnings = snapshot.warnings,
                items = snapshot.diagnostics,
            },
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

