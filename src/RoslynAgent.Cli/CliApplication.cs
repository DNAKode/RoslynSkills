using RoslynAgent.Contracts;
using RoslynAgent.Core;
using System.Text.Json;

namespace RoslynAgent.Cli;

public sealed class CliApplication
{
    private const string EnvelopeVersion = "1.0";
    private readonly ICommandRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    public CliApplication(ICommandRegistry registry)
    {
        _registry = registry;
    }

    public async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            await WriteHelpAsync(stdout).ConfigureAwait(false);
            return 0;
        }

        string verb = args[0];
        string[] remainder = args.Skip(1).ToArray();

        return verb switch
        {
            "list-commands" => await HandleListCommandsAsync(stdout).ConfigureAwait(false),
            "describe-command" => await HandleDescribeCommandAsync(remainder, stdout).ConfigureAwait(false),
            "validate-input" => await HandleValidateInputAsync(remainder, stdout, cancellationToken).ConfigureAwait(false),
            "run" => await HandleRunAsync(remainder, stdout, cancellationToken).ConfigureAwait(false),
            _ => await HandleUnknownCommandAsync(verb, stdout, stderr).ConfigureAwait(false),
        };
    }

    private async Task<int> HandleListCommandsAsync(TextWriter stdout)
    {
        IReadOnlyList<CommandDescriptor> commands = _registry.ListCommands();
        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: true,
                CommandId: "cli.list_commands",
                Version: EnvelopeVersion,
                Data: new { commands },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HandleDescribeCommandAsync(string[] args, TextWriter stdout)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope(
                commandId: "cli.describe_command",
                code: "invalid_args",
                message: "Usage: describe-command <command-id>")).ConfigureAwait(false);
            return 1;
        }

        string commandId = args[0];
        if (!_registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope(
                commandId: "cli.describe_command",
                code: "command_not_found",
                message: $"Command '{commandId}' was not found.")).ConfigureAwait(false);
            return 1;
        }

        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: true,
                CommandId: "cli.describe_command",
                Version: EnvelopeVersion,
                Data: new { command = command.Descriptor },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);

        return 0;
    }

    private async Task<int> HandleValidateInputAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (!TryGetCommandAndInput(args, out string commandId, out JsonElement input, out CommandEnvelope? parseError))
        {
            await WriteEnvelopeAsync(stdout, parseError!).ConfigureAwait(false);
            return 1;
        }

        if (!_registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope(
                commandId: "cli.validate_input",
                code: "command_not_found",
                message: $"Command '{commandId}' was not found.")).ConfigureAwait(false);
            return 1;
        }

        IReadOnlyList<CommandError> errors = command.Validate(input);
        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: errors.Count == 0,
                CommandId: "cli.validate_input",
                Version: EnvelopeVersion,
                Data: new { target_command = commandId, valid = errors.Count == 0 },
                Errors: errors,
                TraceId: null)).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return errors.Count == 0 ? 0 : 1;
    }

    private async Task<int> HandleRunAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (!TryGetCommandAndInput(args, out string commandId, out JsonElement input, out CommandEnvelope? parseError))
        {
            await WriteEnvelopeAsync(stdout, parseError!).ConfigureAwait(false);
            return 1;
        }

        if (!_registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope(
                commandId: "cli.run",
                code: "command_not_found",
                message: $"Command '{commandId}' was not found.")).ConfigureAwait(false);
            return 1;
        }

        IReadOnlyList<CommandError> validationErrors = command.Validate(input);
        if (validationErrors.Count > 0)
        {
            await WriteEnvelopeAsync(
                stdout,
                new CommandEnvelope(
                    Ok: false,
                    CommandId: commandId,
                    Version: EnvelopeVersion,
                    Data: null,
                    Errors: validationErrors,
                    TraceId: null)).ConfigureAwait(false);
            return 1;
        }

        CommandExecutionResult result = await command.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: result.Ok,
                CommandId: commandId,
                Version: EnvelopeVersion,
                Data: result.Data,
                Errors: result.Errors,
                TraceId: null)).ConfigureAwait(false);

        return result.Ok ? 0 : 1;
    }

    private async Task<int> HandleUnknownCommandAsync(string verb, TextWriter stdout, TextWriter stderr)
    {
        await WriteEnvelopeAsync(stdout, ErrorEnvelope(
            commandId: "cli",
            code: "unknown_verb",
            message: $"Unknown command '{verb}'. Use '--help' to view available commands.")).ConfigureAwait(false);
        await stderr.WriteLineAsync($"Unknown command '{verb}'.").ConfigureAwait(false);
        return 1;
    }

    private static bool TryGetCommandAndInput(
        string[] args,
        out string commandId,
        out JsonElement input,
        out CommandEnvelope? parseError)
    {
        commandId = string.Empty;
        input = default;
        parseError = null;

        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            parseError = ErrorEnvelope(
                commandId: "cli",
                code: "invalid_args",
                message: "A command id is required.");
            return false;
        }

        commandId = args[0];
        string inputJson = "{}";
        if (TryGetOption(args, "--input", out string? inputRaw) && !string.IsNullOrWhiteSpace(inputRaw))
        {
            if (inputRaw.StartsWith('@'))
            {
                string path = inputRaw[1..];
                if (!File.Exists(path))
                {
                    parseError = ErrorEnvelope(
                        commandId: "cli",
                        code: "input_file_not_found",
                        message: $"Input file '{path}' does not exist.");
                    return false;
                }

                inputJson = File.ReadAllText(path);
            }
            else
            {
                inputJson = inputRaw;
            }
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(inputJson);
            input = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            parseError = ErrorEnvelope(
                commandId: "cli",
                code: "invalid_json",
                message: $"Input JSON is invalid: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetOption(string[] args, string optionName, out string? value)
    {
        value = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    value = args[i + 1];
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    private static bool IsHelp(string value)
        => string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private async Task WriteEnvelopeAsync(TextWriter writer, CommandEnvelope envelope)
    {
        string json = JsonSerializer.Serialize(envelope, _jsonOptions);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    private static CommandEnvelope ErrorEnvelope(string commandId, string code, string message)
        => new(
            Ok: false,
            CommandId: commandId,
            Version: EnvelopeVersion,
            Data: null,
            Errors: new[]
            {
                new CommandError(code, message),
            },
            TraceId: null);

    private static Task WriteHelpAsync(TextWriter writer)
    {
        return writer.WriteLineAsync(
            """
            roslyn-agent CLI

            Commands:
              list-commands
              describe-command <command-id>
              validate-input <command-id> [--input <json>|@<file>]
              run <command-id> [--input <json>|@<file>]

            Notes:
              - Use --input with raw JSON or @path-to-json-file.
              - Output is always JSON envelopes.
            """);
    }
}
