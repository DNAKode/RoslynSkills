using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class SessionSetContentCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.set_content",
        Summary: "Update session content non-destructively and return diagnostics from the updated snapshot.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "session_id", errors, out _);
        InputParsing.TryGetRequiredString(input, "new_content", errors, out _);
        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "session_id", errors, out string sessionId) ||
            !InputParsing.TryGetRequiredString(input, "new_content", errors, out string newContent))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 100, minValue: 1, maxValue: 2_000);
        int? expectedGeneration = null;
        if (input.TryGetProperty("expected_generation", out JsonElement expectedGenerationProperty))
        {
            if (expectedGenerationProperty.ValueKind != JsonValueKind.Number ||
                !expectedGenerationProperty.TryGetInt32(out int expectedGenerationValue) ||
                expectedGenerationValue < 0)
            {
                return Task.FromResult(new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_input", "Property 'expected_generation' must be a non-negative integer when provided.") }));
            }

            expectedGeneration = expectedGenerationValue;
        }

        if (!RoslynSessionStore.TryGet(sessionId, out RoslynSession? session) || session is null)
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("session_not_found", $"Session '{sessionId}' was not found.") }));
        }

        int currentGeneration = session.CurrentGeneration;
        if (expectedGeneration.HasValue && expectedGeneration.Value != currentGeneration)
        {
            object conflictData = new
            {
                session_id = sessionId,
                expected_generation = expectedGeneration.Value,
                actual_generation = currentGeneration,
            };
            return Task.FromResult(new CommandExecutionResult(
                conflictData,
                new[] { new CommandError("generation_conflict", $"Session '{sessionId}' generation mismatch. Expected {expectedGeneration.Value}, actual {currentGeneration}.") }));
        }

        SessionUpdateResult update = session.SetContent(newContent, maxDiagnostics, cancellationToken);
        if (!RoslynSessionStore.Persist(session))
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("session_persist_failed", $"Session '{sessionId}' could not be persisted.") }));
        }

        SessionStatus status = session.GetStatus();
        object data = new
        {
            session_id = sessionId,
            file_path = update.snapshot.file_path,
            expected_generation = expectedGeneration,
            previous_generation = update.previous_generation,
            generation = update.snapshot.generation,
            changed = update.changed,
            changed_line_count = update.changed_lines.Count,
            changed_lines = update.changed_lines,
            snapshot = new
            {
                generation = update.snapshot.generation,
                line_count = update.snapshot.line_count,
                character_count = update.snapshot.character_count,
                has_changes = update.snapshot.has_changes,
                diagnostics = new
                {
                    total = update.snapshot.total_diagnostics,
                    returned = update.snapshot.returned_diagnostics,
                    errors = update.snapshot.errors,
                    warnings = update.snapshot.warnings,
                    items = update.snapshot.diagnostics,
                },
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

