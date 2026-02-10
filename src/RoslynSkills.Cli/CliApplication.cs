using RoslynSkills.Contracts;
using RoslynSkills.Core;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RoslynSkills.Cli;

public sealed class CliApplication
{
    private const string EnvelopeVersion = "1.0";
    private readonly ICommandRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
        CancellationToken cancellationToken,
        TextReader? stdin = null)
    {
        stdin ??= Console.In;

        if (args.Length == 0 || IsHelp(args[0]))
        {
            await WriteHelpAsync(stdout).ConfigureAwait(false);
            return 0;
        }

        string verb = args[0];
        string[] remainder = args.Skip(1).ToArray();

        return verb switch
        {
            "list-commands" => await HandleListCommandsAsync(remainder, stdout).ConfigureAwait(false),
            "describe-command" => await HandleDescribeCommandAsync(remainder, stdout).ConfigureAwait(false),
            "validate-input" => await HandleValidateInputAsync(remainder, stdout, cancellationToken, stdin).ConfigureAwait(false),
            "run" => await HandleRunAsync(remainder, stdout, cancellationToken, stdin).ConfigureAwait(false),
            _ when _registry.TryGet(verb, out _) => await HandleRunDirectAsync(verb, remainder, stdout, cancellationToken, stdin).ConfigureAwait(false),
            _ => await HandleUnknownCommandAsync(verb, stdout, stderr).ConfigureAwait(false),
        };
    }

    private async Task<int> HandleListCommandsAsync(string[] args, TextWriter stdout)
    {
        bool compact = HasOption(args, "--compact");
        bool idsOnly = HasOption(args, "--ids-only");

        if (idsOnly)
        {
            compact = true;
        }

        if (args.Any(a => IsHelp(a)))
        {
            await WriteEnvelopeAsync(
                stdout,
                new CommandEnvelope(
                    Ok: true,
                    CommandId: "cli.list_commands",
                    Version: EnvelopeVersion,
                    Data: new
                    {
                        usage = "list-commands [--compact] [--ids-only]",
                        options = new[]
                        {
                            new { name = "--compact", summary = "Return compact descriptors only (id + mutates_state)." },
                            new { name = "--ids-only", summary = "Return command ids only." },
                        },
                    },
                    Errors: Array.Empty<CommandError>(),
                    TraceId: null)).ConfigureAwait(false);
            return 0;
        }

        IReadOnlyList<CommandDescriptor> commands = _registry.ListCommands();
        object data = idsOnly
            ? new
            {
                total = commands.Count,
                command_ids = commands
                    .Select(c => c.Id)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            }
            : compact
                ? new
                {
                    total = commands.Count,
                    commands = commands
                        .Select(c => new
                        {
                            c.Id,
                            c.MutatesState,
                        })
                        .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                }
                : new
                {
                    total = commands.Count,
                    commands,
                };
        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: true,
                CommandId: "cli.list_commands",
                Version: EnvelopeVersion,
                Data: data,
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
                Data: new
                {
                    command = command.Descriptor,
                    usage = BuildCommandUsageHints(commandId),
                },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);

        return 0;
    }

    private async Task<int> HandleValidateInputAsync(
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken,
        TextReader stdin)
    {
        (bool ok, string commandId, JsonElement input, CommandEnvelope? error) =
            await TryGetCommandAndInputAsync(args, stdin).ConfigureAwait(false);
        if (!ok)
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
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

    private async Task<int> HandleRunAsync(
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken,
        TextReader stdin)
    {
        (bool ok, string commandId, JsonElement input, CommandEnvelope? error) =
            await TryGetCommandAndInputAsync(args, stdin).ConfigureAwait(false);
        if (!ok)
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
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

    private async Task<int> HandleRunDirectAsync(
        string commandId,
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken,
        TextReader stdin)
    {
        if (args.Length == 1 && IsHelp(args[0]))
        {
            return await HandleDescribeCommandAsync(new[] { commandId }, stdout).ConfigureAwait(false);
        }

        if (args.Length == 0)
        {
            return await HandleRunAsync(new[] { commandId }, stdout, cancellationToken, stdin).ConfigureAwait(false);
        }

        if (HasOption(args, "--input-stdin") || TryGetOption(args, "--input", out _))
        {
            string[] runArgs = new[] { commandId }.Concat(args).ToArray();
            return await HandleRunAsync(runArgs, stdout, cancellationToken, stdin).ConfigureAwait(false);
        }

        if (!SupportsDirectShorthand(commandId))
        {
            string[] runArgs = new[] { commandId }.Concat(args).ToArray();
            return await HandleRunAsync(runArgs, stdout, cancellationToken, stdin).ConfigureAwait(false);
        }

        if (!TryBuildDirectShorthandInput(commandId, args, out string inputJson, out CommandEnvelope? error))
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        return await HandleRunAsync(
            new[] { commandId, "--input", inputJson },
            stdout,
            cancellationToken,
            stdin).ConfigureAwait(false);
    }

    private async Task<int> HandleUnknownCommandAsync(string verb, TextWriter stdout, TextWriter stderr)
    {
        await WriteEnvelopeAsync(stdout, ErrorEnvelope(
            commandId: "cli",
            code: "unknown_verb",
            message: $"Unknown command '{verb}'. Use '--help' or 'list-commands --ids-only' to view available commands.")).ConfigureAwait(false);
        await stderr.WriteLineAsync($"Unknown command '{verb}'.").ConfigureAwait(false);
        return 1;
    }

    private static async Task<(bool Ok, string CommandId, JsonElement Input, CommandEnvelope? Error)> TryGetCommandAndInputAsync(
        string[] args,
        TextReader stdin)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return (
                false,
                string.Empty,
                default,
                ErrorEnvelope(
                    commandId: "cli",
                    code: "invalid_args",
                    message: "A command id is required."));
        }

        string commandId = args[0];
        string inputJson = "{}";
        bool hasInputOption = TryGetOption(args, "--input", out string? inputRaw);
        bool useInputStdin = HasOption(args, "--input-stdin");
        if (hasInputOption && useInputStdin)
        {
            return (
                false,
                commandId,
                default,
                ErrorEnvelope(
                    commandId: "cli",
                    code: "invalid_args",
                    message: "Use either '--input' or '--input-stdin', not both."));
        }

        if (useInputStdin)
        {
            inputJson = await stdin.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(inputJson))
            {
                return (
                    false,
                    commandId,
                    default,
                    ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: "Input JSON is required when '--input-stdin' is used."));
            }
        }
        else if (hasInputOption && !string.IsNullOrWhiteSpace(inputRaw))
        {
            if (string.Equals(inputRaw, "-", StringComparison.Ordinal))
            {
                inputJson = await stdin.ReadToEndAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(inputJson))
                {
                    return (
                        false,
                        commandId,
                        default,
                        ErrorEnvelope(
                            commandId: "cli",
                            code: "invalid_args",
                            message: "Input JSON is required when '--input -' is used."));
                }
            }
            else if (inputRaw.StartsWith('@'))
            {
                string path = inputRaw[1..];
                if (!File.Exists(path))
                {
                    return (
                        false,
                        commandId,
                        default,
                        ErrorEnvelope(
                            commandId: "cli",
                            code: "input_file_not_found",
                            message: $"Input file '{path}' does not exist."));
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
            inputJson = NormalizeInputJson(inputJson);
            using JsonDocument doc = JsonDocument.Parse(inputJson);
            return (true, commandId, doc.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return (
                false,
                commandId,
                default,
                ErrorEnvelope(
                    commandId: "cli",
                    code: "invalid_json",
                    message: $"Input JSON is invalid: {ex.Message}"));
        }
    }

    private static bool TryGetOption(string[] args, string optionName, out string? value)
    {
        value = null;
        for (int i = 0; i < args.Length; i++)
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

    private static bool HasOption(string[] args, string optionName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHelp(string value)
        => string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildDirectShorthandInput(
        string commandId,
        string[] args,
        out string inputJson,
        out CommandEnvelope? error)
    {
        inputJson = "{}";

        if (!TryParseShorthandArguments(commandId, args, out string[] positionalArgs, out Dictionary<string, object?> options, out error))
        {
            return false;
        }

        if (string.Equals(commandId, "session.open", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "file_path", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "file", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "path", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "solution", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "session_id", ref positionalArgs, 1);
        }

        if (string.Equals(commandId, "nav.find_symbol", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "file_path", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "symbol_name", ref positionalArgs, 1);
        }

        Dictionary<string, object?> input = new(StringComparer.OrdinalIgnoreCase);

        switch (commandId)
        {
            case "ctx.file_outline":
            case "diag.get_file_diagnostics":
            case "repair.propose_from_diagnostics":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, $"{commandId} <file-path> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "diag.get_solution_snapshot":
                if (positionalArgs.Length > 1)
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "diag.get_solution_snapshot [directory-path] [--option value ...]"));
                    return false;
                }

                if (positionalArgs.Length == 1)
                {
                    input["directory_path"] = NormalizeCliPathValue(positionalArgs[0]);
                }
                break;

            case "ctx.member_source":
                if (positionalArgs.Length < 3 ||
                    positionalArgs.Length > 4 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int line) ||
                    !int.TryParse(positionalArgs[2], out int column))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "ctx.member_source <file-path> <line> <column> [member|body] [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["line"] = line;
                input["column"] = column;
                if (positionalArgs.Length == 4)
                {
                    input["mode"] = positionalArgs[3];
                }
                break;

            case "nav.find_symbol":
                if (positionalArgs.Length != 2 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    string.IsNullOrWhiteSpace(positionalArgs[1]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "nav.find_symbol <file-path> <symbol-name> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["symbol_name"] = positionalArgs[1];
                break;

            case "edit.rename_symbol":
                if (positionalArgs.Length != 4 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int renameLine) ||
                    !int.TryParse(positionalArgs[2], out int renameColumn) ||
                    string.IsNullOrWhiteSpace(positionalArgs[3]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "edit.rename_symbol <file-path> <line> <column> <new-name> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["line"] = renameLine;
                input["column"] = renameColumn;
                input["new_name"] = positionalArgs[3];
                break;

            case "edit.create_file":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "edit.create_file <file-path> [--content <text>] [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "session.open":
                if (positionalArgs.Length < 1 ||
                    positionalArgs.Length > 2 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(
                            commandId,
                            "session.open <file-path> [session-id] [--option value ...]",
                            "session.open supports only .cs/.csx files."));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                if (positionalArgs.Length == 2)
                {
                    input["session_id"] = positionalArgs[1];
                }
                break;

            case "session.get_diagnostics":
            case "session.status":
            case "session.diff":
            case "session.commit":
            case "session.close":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, $"{commandId} <session-id> [--option value ...]"));
                    return false;
                }

                input["session_id"] = positionalArgs[0];
                break;

            default:
                error = ErrorEnvelope(
                    commandId: "cli",
                    code: "invalid_args",
                    message: $"Command '{commandId}' does not support positional shorthand arguments. Use 'run {commandId} --input ...' or '--input-stdin'.");
                return false;
        }

        foreach ((string key, object? value) in options)
        {
            if (value is string pathValue && IsPathLikeOptionName(key))
            {
                input[key] = NormalizeCliPathValue(pathValue);
            }
            else
            {
                input[key] = value;
            }
        }

        error = null;
        inputJson = JsonSerializer.Serialize(input);
        return true;
    }

    private static bool SupportsDirectShorthand(string commandId)
        => commandId switch
        {
            "ctx.file_outline" => true,
            "ctx.member_source" => true,
            "diag.get_file_diagnostics" => true,
            "diag.get_solution_snapshot" => true,
            "repair.propose_from_diagnostics" => true,
            "nav.find_symbol" => true,
            "edit.rename_symbol" => true,
            "edit.create_file" => true,
            "session.open" => true,
            "session.get_diagnostics" => true,
            "session.status" => true,
            "session.diff" => true,
            "session.commit" => true,
            "session.close" => true,
            _ => false,
        };

    private static bool TryParseShorthandArguments(
        string commandId,
        string[] args,
        out string[] positionalArgs,
        out Dictionary<string, object?> options,
        out CommandEnvelope? error)
    {
        List<string> positional = new();
        options = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                for (int j = i + 1; j < args.Length; j++)
                {
                    positional.Add(args[j]);
                }

                break;
            }

            string token = arg[2..];
            if (string.IsNullOrWhiteSpace(token))
            {
                positionalArgs = Array.Empty<string>();
                error = ErrorEnvelope(
                    commandId: "cli",
                    code: "invalid_args",
                    message: $"Command '{commandId}' includes an empty option token.");
                return false;
            }

            string optionName;
            object? optionValue;
            int equalsIndex = token.IndexOf('=');
            if (equalsIndex >= 0)
            {
                optionName = token[..equalsIndex];
                string rawValue = token[(equalsIndex + 1)..];
                optionValue = ParseOptionValue(rawValue);
            }
            else if (token.StartsWith("no-", StringComparison.OrdinalIgnoreCase))
            {
                optionName = token[3..];
                optionValue = false;
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                optionName = token;
                optionValue = ParseOptionValue(args[++i]);
            }
            else
            {
                optionName = token;
                optionValue = true;
            }

            string normalizedOptionName = NormalizeOptionName(optionName);
            if (string.IsNullOrWhiteSpace(normalizedOptionName))
            {
                positionalArgs = Array.Empty<string>();
                error = ErrorEnvelope(
                    commandId: "cli",
                    code: "invalid_args",
                    message: $"Command '{commandId}' includes an invalid option name '{optionName}'.");
                return false;
            }

            if (options.TryGetValue(normalizedOptionName, out object? existing))
            {
                options[normalizedOptionName] = AppendOptionValue(existing, optionValue);
            }
            else
            {
                options[normalizedOptionName] = optionValue;
            }
        }

        positionalArgs = positional.ToArray();
        return true;
    }

    private static object? ParseOptionValue(string rawValue)
    {
        string value = rawValue.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            return parts.Select(ParseScalarOptionValue).ToArray();
        }

        return ParseScalarOptionValue(value);
    }

    private static object? ParseScalarOptionValue(string value)
    {
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (bool.TryParse(value, out bool boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
        {
            return intValue;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
        {
            return longValue;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
        {
            return doubleValue;
        }

        return value;
    }

    private static string NormalizeOptionName(string optionName)
        => optionName.Trim().Replace("-", "_", StringComparison.Ordinal);

    private static string BuildUsageMessage(string commandId, string usage, string? note = null)
    {
        string message = $"Usage: {usage} Tip: run 'describe-command {commandId}' for argument schema.";
        if (!string.IsNullOrWhiteSpace(note))
        {
            message += $" {note}";
        }

        return message;
    }

    private static bool IsPathLikeOptionName(string optionName)
        => optionName.EndsWith("_path", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(optionName, "directory_path", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCliPathValue(string value)
    {
        string trimmed = value.Trim();
        if (!OperatingSystem.IsWindows() || trimmed.Length < 3 || trimmed[0] != '/')
        {
            return trimmed;
        }

        char driveLetter = trimmed[1];
        char separator = trimmed[2];
        if (!char.IsLetter(driveLetter) || (separator != '/' && separator != '\\'))
        {
            return trimmed;
        }

        string remainder = trimmed[2..].Replace('/', '\\');
        return $"{char.ToUpperInvariant(driveLetter)}:{remainder}";
    }

    private static void TryPromoteOptionToPositional(
        Dictionary<string, object?> options,
        string optionName,
        ref string[] positionalArgs,
        int targetIndex)
    {
        if (targetIndex < 0)
        {
            return;
        }

        if (positionalArgs.Length > targetIndex && !string.IsNullOrWhiteSpace(positionalArgs[targetIndex]))
        {
            return;
        }

        string normalizedOptionName = NormalizeOptionName(optionName);
        if (!options.TryGetValue(normalizedOptionName, out object? rawOptionValue) ||
            !TryConvertOptionToSingleString(rawOptionValue, out string optionValue) ||
            string.IsNullOrWhiteSpace(optionValue))
        {
            return;
        }

        options.Remove(normalizedOptionName);

        if (positionalArgs.Length <= targetIndex)
        {
            Array.Resize(ref positionalArgs, targetIndex + 1);
        }

        positionalArgs[targetIndex] = optionValue;
    }

    private static bool TryConvertOptionToSingleString(object? optionValue, out string value)
    {
        switch (optionValue)
        {
            case string stringValue when !string.IsNullOrWhiteSpace(stringValue):
                value = stringValue;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            case List<object?> list:
                foreach (object? candidate in list)
                {
                    if (TryConvertOptionToSingleString(candidate, out value))
                    {
                        return true;
                    }
                }

                break;
        }

        value = string.Empty;
        return false;
    }

    private static object? AppendOptionValue(object? existing, object? incoming)
    {
        if (existing is List<object?> existingList)
        {
            existingList.Add(incoming);
            return existingList;
        }

        return new List<object?> { existing, incoming };
    }

    private static string NormalizeInputJson(string inputJson)
    {
        if (string.IsNullOrEmpty(inputJson))
        {
            return inputJson;
        }

        string normalized = inputJson.Trim();
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        return normalized;
    }

    private async Task WriteEnvelopeAsync(TextWriter writer, CommandEnvelope envelope)
    {
        (string preview, string summary) = BuildEnvelopeHints(envelope);
        CommandEnvelope envelopeWithHints = envelope with
        {
            Preview = preview,
            Summary = summary,
        };
        string json = JsonSerializer.Serialize(envelopeWithHints, _jsonOptions);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    private static (string Preview, string Summary) BuildEnvelopeHints(CommandEnvelope envelope)
    {
        string commandId = envelope.CommandId;
        if (!envelope.Ok)
        {
            string firstCode = envelope.Errors.FirstOrDefault()?.Code ?? "error";
            string preview = $"{commandId} failed";
            string summary = $"{commandId} failed: {firstCode} ({envelope.Errors.Count} error(s))";
            return (Truncate(preview, 120), Truncate(summary, 220));
        }

        string? dataSummary = BuildDataSummary(commandId, envelope.Data);
        if (string.IsNullOrWhiteSpace(dataSummary))
        {
            string fallback = $"{commandId} ok";
            return (fallback, fallback);
        }

        string detailed = $"{commandId} ok: {dataSummary}";
        return (Truncate(detailed, 120), Truncate(detailed, 220));
    }

    private static string? BuildDataSummary(string commandId, object? data)
    {
        if (data is null)
        {
            return null;
        }

        JsonElement element = JsonSerializer.SerializeToElement(data);

        if (string.Equals(commandId, "cli.list_commands", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetInt(element, "total", out int total))
            {
                if (TryGetArrayLength(element, "command_ids", out int idCount))
                {
                    return $"{idCount} command id(s)";
                }

                if (TryGetArrayLength(element, "commands", out int commandCount))
                {
                    return $"{commandCount}/{total} command descriptor(s)";
                }

                return $"{total} command(s)";
            }
        }

        if (string.Equals(commandId, "ctx.file_outline", StringComparison.OrdinalIgnoreCase))
        {
            string file = TryGetString(element, "file_path", out string filePath)
                ? Path.GetFileName(filePath)
                : "<unknown>";
            if (TryGetObject(element, "summary", out JsonElement summaryElement))
            {
                int typeCount = TryGetInt(summaryElement, "type_count", out int tc) ? tc : -1;
                int memberCount = TryGetInt(summaryElement, "member_count", out int mc) ? mc : -1;
                int usingCount = TryGetInt(summaryElement, "using_count", out int uc) ? uc : -1;
                if (typeCount >= 0 && memberCount >= 0)
                {
                    return $"{file}, types={typeCount}, members={memberCount}, usings={Math.Max(usingCount, 0)}";
                }
            }
        }

        if (string.Equals(commandId, "ctx.member_source", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "member", out JsonElement memberElement))
            {
                string memberName = TryGetString(memberElement, "member_name", out string name) ? name : "<member>";
                int lineCount = TryGetInt(memberElement, "source_line_count", out int lc) ? lc : -1;
                return lineCount > 0
                    ? $"{memberName}, lines={lineCount}"
                    : memberName;
            }
        }

        if (string.Equals(commandId, "nav.find_symbol", StringComparison.OrdinalIgnoreCase))
        {
            int totalMatches = TryGetInt(element, "total_matches", out int matches) ? matches : -1;
            if (totalMatches >= 0)
            {
                return $"matches={totalMatches}";
            }
        }

        if (string.Equals(commandId, "edit.create_file", StringComparison.OrdinalIgnoreCase))
        {
            string file = TryGetString(element, "file_path", out string filePath)
                ? Path.GetFileName(filePath)
                : "<unknown>";
            bool wrote = TryGetBool(element, "wrote_file", out bool wroteFile) && wroteFile;
            bool created = TryGetBool(element, "created", out bool createdFile) && createdFile;
            string action = wrote ? "written" : "dry-run";
            return created ? $"{file}, created, {action}" : $"{file}, updated, {action}";
        }

        if (string.Equals(commandId, "diag.get_solution_snapshot", StringComparison.OrdinalIgnoreCase))
        {
            int files = TryGetInt(element, "total_files", out int tf) ? tf : -1;
            int diagnostics = TryGetInt(element, "total_diagnostics", out int td) ? td : -1;
            int errors = TryGetInt(element, "errors", out int err) ? err : -1;
            int warnings = TryGetInt(element, "warnings", out int warn) ? warn : -1;
            if (files >= 0 || diagnostics >= 0)
            {
                return $"files={Math.Max(files, 0)}, diagnostics={Math.Max(diagnostics, 0)}, errors={Math.Max(errors, 0)}, warnings={Math.Max(warnings, 0)}";
            }
        }

        if (commandId.StartsWith("session.", StringComparison.OrdinalIgnoreCase))
        {
            string sessionId = TryGetString(element, "session_id", out string sid) ? sid : "<session>";
            string shortSessionId = sessionId.Length > 12 ? sessionId[..12] : sessionId;
            int generation = TryGetInt(element, "generation", out int gen) ? gen : -1;
            string suffix = generation >= 0 ? $"gen={generation}" : "ok";

            if (TryGetBool(element, "changed", out bool changed))
            {
                suffix += changed ? ", changed" : ", unchanged";
            }
            else if (TryGetBool(element, "wrote_file", out bool wrote))
            {
                suffix += wrote ? ", committed" : ", not-committed";
            }

            return $"{shortSessionId}, {suffix}";
        }

        if (TryGetInt(element, "total", out int totalValue))
        {
            return $"total={totalValue}";
        }

        if (TryGetInt(element, "count", out int countValue))
        {
            return $"count={countValue}";
        }

        return null;
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Object)
        {
            value = property;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out int parsed))
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetBool(JsonElement element, string propertyName, out bool value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryGetArrayLength(JsonElement element, string propertyName, out int count)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Array)
        {
            count = property.GetArrayLength();
            return true;
        }

        count = 0;
        return false;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= 3)
        {
            return text[..maxLength];
        }

        return text[..(maxLength - 3)] + "...";
    }

    private static object BuildCommandUsageHints(string commandId)
    {
        if (string.Equals(commandId, "session.open", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "session.open <file-path> [session-id] [--option value ...]",
                run = "run session.open --input '{\"file_path\":\"src/MyFile.cs\",\"session_id\":\"demo\"}'",
                required_properties = new[] { "file_path" },
                optional_properties = new[] { "session_id", "max_diagnostics" },
                notes = new[]
                {
                    "session.open supports only .cs/.csx files.",
                    "Use .sln/.slnx/.csproj with diag/nav commands, not session.open.",
                    "session diagnostics are file-scoped and may differ from full project build diagnostics.",
                },
            };
        }

        if (string.Equals(commandId, "edit.create_file", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "edit.create_file <file-path> [--content <text>] [--option value ...]",
                run = "run edit.create_file --input '{\"file_path\":\"src/NewType.cs\",\"content\":\"public class NewType { }\",\"overwrite\":false}'",
                required_properties = new[] { "file_path" },
                optional_properties = new[] { "content", "overwrite", "create_directories", "apply", "include_diagnostics", "max_diagnostics" },
                notes = new[]
                {
                    "Defaults: apply=true, overwrite=false, create_directories=true.",
                    "For multiline content, prefer --input-stdin JSON.",
                },
            };
        }

        if (string.Equals(commandId, "edit.rename_symbol", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "edit.rename_symbol <file-path> <line> <column> <new-name> [--option value ...]",
                run = "run edit.rename_symbol --input '{\"file_path\":\"src/MyFile.cs\",\"line\":12,\"column\":15,\"new_name\":\"Updated\",\"apply\":true}'",
                required_properties = new[] { "file_path", "line", "column", "new_name" },
                optional_properties = new[] { "apply", "max_diagnostics" },
            };
        }

        if (commandId.StartsWith("session.", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = $"{commandId} <session-id> [--option value ...]",
                run = $"run {commandId} --input '{{\"session_id\":\"demo\"}}'",
                required_properties = new[] { "session_id" },
            };
        }

        if (string.Equals(commandId, "nav.find_symbol", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "nav.find_symbol <file-path> <symbol-name> [--option value ...]",
                run = "run nav.find_symbol --input '{\"file_path\":\"src/MyFile.cs\",\"symbol_name\":\"Run\",\"brief\":true}'",
                required_properties = new[] { "file_path", "symbol_name" },
                optional_properties = new[] { "brief", "max_results", "context_lines" },
            };
        }

        return new
        {
            run = $"run {commandId} --input '{{...}}'",
            validation = $"validate-input {commandId} --input '{{...}}'",
            notes = new[]
            {
                "Use describe-command for command summary and schema versions.",
                "Use validate-input before run when argument shape is uncertain.",
            },
        };
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
            roscli CLI

            Commands:
              list-commands [--compact] [--ids-only]
              describe-command <command-id>
              validate-input <command-id> [--input <json>|@<file>|-] [--input-stdin]
              run <command-id> [--input <json>|@<file>|-] [--input-stdin]
              <command-id> [simple positional args]

            Notes:
              - You can run commands directly without 'run' for quick workflows.
              - Shorthand positional forms:
                ctx.file_outline <file-path>
                ctx.member_source <file-path> <line> <column> [member|body]
                diag.get_file_diagnostics <file-path>
                diag.get_solution_snapshot [directory-path]
                repair.propose_from_diagnostics <file-path>
                nav.find_symbol <file-path> <symbol-name>
                edit.rename_symbol <file-path> <line> <column> <new-name>
                edit.create_file <file-path> [--content <text>]
                session.open <file-path> [session-id]
                session.get_diagnostics <session-id>
                session.status <session-id>
                session.diff <session-id>
                session.commit <session-id>
                session.close <session-id>
              - Direct shorthand also accepts command options:
                ctx.file_outline <file-path> --include-members false --max-members 50
                edit.create_file src/NewType.cs --content "public class NewType { }" --overwrite false
                session.commit <session-id> --keep-session false --require-disk-unchanged true
              - list-commands supports compact response modes:
                list-commands --compact
                list-commands --ids-only
              - Use session.apply_text_edits with --input/--input-stdin for structured span edits.
              - Use session.apply_and_commit with --input/--input-stdin for one-shot edit+commit.
              - Use describe-command <command-id> when an agent is unsure about command arguments.
              - Use --input with raw JSON, @path-to-json-file, or '-' for stdin.
              - Use --input-stdin to read full JSON from stdin without temp files.
              - Output is always JSON envelopes.
            """);
    }
}

