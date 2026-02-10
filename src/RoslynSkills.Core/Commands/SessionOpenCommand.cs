using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class SessionOpenCommand : IAgentCommand
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "session.open",
        Summary: "Open a persistent in-memory Roslyn session for a C# file.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }
        else
        {
            string extension = Path.GetExtension(filePath);
            if (!SupportedExtensions.Contains(extension))
            {
                errors.Add(new CommandError(
                    "unsupported_file_type",
                    $"Input file '{filePath}' is not a supported C# source file. session.open only supports .cs/.csx files."));
            }
        }

        if (input.TryGetProperty("session_id", out JsonElement sessionProperty) &&
            sessionProperty.ValueKind == JsonValueKind.String &&
            string.IsNullOrWhiteSpace(sessionProperty.GetString()))
        {
            errors.Add(new CommandError("invalid_input", "Property 'session_id' must not be empty when provided."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = Validate(input).ToList();
        if (errors.Count > 0)
        {
            return new CommandExecutionResult(null, errors);
        }

        string filePath = input.GetProperty("file_path").GetString()!;
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 100, minValue: 1, maxValue: 2_000);

        string sessionId = RoslynSessionStore.CreateSessionId();
        if (input.TryGetProperty("session_id", out JsonElement sessionProperty) &&
            sessionProperty.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sessionProperty.GetString()))
        {
            sessionId = sessionProperty.GetString()!;
        }

        RoslynSession session = await RoslynSession.CreateAsync(sessionId, filePath, cancellationToken).ConfigureAwait(false);
        if (!RoslynSessionStore.TryAdd(session))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("session_conflict", $"Session '{sessionId}' already exists.") });
        }

        SessionSnapshot snapshot = session.BuildSnapshot(maxDiagnostics, cancellationToken);
        SessionStatus status = session.GetStatus();
        object data = new
        {
            session_id = snapshot.session_id,
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
            store_session_count = RoslynSessionStore.Count,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }
}

