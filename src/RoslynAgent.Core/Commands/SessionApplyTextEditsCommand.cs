using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class SessionApplyTextEditsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.apply_text_edits",
        Summary: "Apply structured line/column text edits to a session with immediate diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "session_id", errors, out _);

        if (!input.TryGetProperty("edits", out JsonElement editsProperty) || editsProperty.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'edits' is required and must be an array."));
        }

        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = Validate(input).ToList();
        if (errors.Count > 0)
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        string sessionId = input.GetProperty("session_id").GetString()!;
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

        if (!TryParseEdits(input.GetProperty("edits"), out SessionTextEdit[] parsedEdits, out CommandError? parseError))
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { parseError! }));
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

        SessionUpdateResult update;
        try
        {
            update = session.ApplyTextEdits(parsedEdits, maxDiagnostics, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_edits", ex.Message) }));
        }

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
            edit_count = parsedEdits.Length,
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

    private static bool TryParseEdits(JsonElement editsProperty, out SessionTextEdit[] edits, out CommandError? error)
    {
        List<SessionTextEdit> parsed = new();
        int index = 0;
        foreach (JsonElement editElement in editsProperty.EnumerateArray())
        {
            if (editElement.ValueKind != JsonValueKind.Object)
            {
                error = new CommandError("invalid_input", $"Edit at index {index} must be an object.");
                edits = Array.Empty<SessionTextEdit>();
                return false;
            }

            if (!TryGetRequiredEditInt(editElement, "start_line", index, out int startLine, out error) ||
                !TryGetRequiredEditInt(editElement, "start_column", index, out int startColumn, out error) ||
                !TryGetRequiredEditInt(editElement, "end_line", index, out int endLine, out error) ||
                !TryGetRequiredEditInt(editElement, "end_column", index, out int endColumn, out error))
            {
                edits = Array.Empty<SessionTextEdit>();
                return false;
            }

            if (!editElement.TryGetProperty("new_text", out JsonElement newTextProperty) ||
                newTextProperty.ValueKind != JsonValueKind.String)
            {
                error = new CommandError("invalid_input", $"Edit at index {index} requires string property 'new_text'.");
                edits = Array.Empty<SessionTextEdit>();
                return false;
            }

            parsed.Add(new SessionTextEdit(
                start_line: startLine,
                start_column: startColumn,
                end_line: endLine,
                end_column: endColumn,
                new_text: newTextProperty.GetString() ?? string.Empty));

            index++;
        }

        error = null;
        edits = parsed.ToArray();
        return true;
    }

    private static bool TryGetRequiredEditInt(
        JsonElement editElement,
        string propertyName,
        int index,
        out int value,
        out CommandError? error)
    {
        value = 0;
        if (!editElement.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out int parsed) ||
            parsed < 1)
        {
            error = new CommandError("invalid_input", $"Edit at index {index} requires integer property '{propertyName}' >= 1.");
            return false;
        }

        value = parsed;
        error = null;
        return true;
    }
}
