using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class SessionCommitCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.commit",
        Summary: "Write current session content to disk and optionally keep session open.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "session_id", errors, out _);
        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "session_id", errors, out string sessionId))
        {
            return new CommandExecutionResult(null, errors);
        }

        bool keepSession = InputParsing.GetOptionalBool(input, "keep_session", defaultValue: true);
        bool requireDiskUnchanged = InputParsing.GetOptionalBool(input, "require_disk_unchanged", defaultValue: true);
        int? expectedGeneration = null;
        if (input.TryGetProperty("expected_generation", out JsonElement expectedGenerationProperty))
        {
            if (expectedGenerationProperty.ValueKind != JsonValueKind.Number ||
                !expectedGenerationProperty.TryGetInt32(out int expectedGenerationValue) ||
                expectedGenerationValue < 0)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_input", "Property 'expected_generation' must be a non-negative integer when provided.") });
            }

            expectedGeneration = expectedGenerationValue;
        }

        if (!RoslynSessionStore.TryGet(sessionId, out RoslynSession? session) || session is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("session_not_found", $"Session '{sessionId}' was not found.") });
        }

        int actualGeneration = session.CurrentGeneration;
        if (expectedGeneration.HasValue && expectedGeneration.Value != actualGeneration)
        {
            object conflictData = new
            {
                session_id = sessionId,
                expected_generation = expectedGeneration.Value,
                actual_generation = actualGeneration,
            };
            return new CommandExecutionResult(
                conflictData,
                new[] { new CommandError("generation_conflict", $"Session '{sessionId}' generation mismatch. Expected {expectedGeneration.Value}, actual {actualGeneration}.") });
        }

        SessionStatus statusBeforeCommit = session.GetStatus();
        if (requireDiskUnchanged && (!statusBeforeCommit.disk_exists || !statusBeforeCommit.disk_matches_open))
        {
            object driftData = new
            {
                session_id = sessionId,
                require_disk_unchanged = true,
                status = new
                {
                    sync_state = statusBeforeCommit.sync_state,
                    recommended_action = statusBeforeCommit.recommended_action,
                    disk_exists = statusBeforeCommit.disk_exists,
                    disk_matches_open = statusBeforeCommit.disk_matches_open,
                    disk_matches_current = statusBeforeCommit.disk_matches_current,
                    open_disk_hash = statusBeforeCommit.open_disk_hash,
                    current_content_hash = statusBeforeCommit.current_content_hash,
                    disk_hash = statusBeforeCommit.disk_hash,
                },
            };
            return new CommandExecutionResult(
                driftData,
                new[] { new CommandError("disk_conflict", $"Session '{sessionId}' is not safely in sync with the on-disk baseline. Commit blocked by 'require_disk_unchanged=true'.") });
        }

        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (!keepSession)
        {
            if (!RoslynSessionStore.TryRemove(sessionId, out _))
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("session_close_failed", $"Session '{sessionId}' was committed but could not be closed.") });
            }
        }
        else if (!RoslynSessionStore.Persist(session))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("session_persist_failed", $"Session '{sessionId}' could not be persisted after commit.") });
        }

        SessionStatus? statusAfterCommit = keepSession ? session.GetStatus() : null;
        object data = new
        {
            session_id = sessionId,
            file_path = session.FilePath,
            expected_generation = expectedGeneration,
            generation = actualGeneration,
            wrote_file = true,
            keep_session = keepSession,
            require_disk_unchanged = requireDiskUnchanged,
            status = statusAfterCommit is null
                ? null
                : new
                {
                    sync_state = statusAfterCommit.sync_state,
                    recommended_action = statusAfterCommit.recommended_action,
                    disk_exists = statusAfterCommit.disk_exists,
                    disk_matches_open = statusAfterCommit.disk_matches_open,
                    disk_matches_current = statusAfterCommit.disk_matches_current,
                    open_disk_hash = statusAfterCommit.open_disk_hash,
                    current_content_hash = statusAfterCommit.current_content_hash,
                    disk_hash = statusAfterCommit.disk_hash,
                },
            store_session_count = RoslynSessionStore.Count,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }
}

