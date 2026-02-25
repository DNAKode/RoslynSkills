using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using XmlSkills.Contracts;
using XmlSkills.Core;

namespace XmlSkills.Cli;

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

        if (IsVersion(args[0]) || string.Equals(args[0], "version", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleVersionAsync(stdout).ConfigureAwait(false);
        }

        string verb = args[0];
        string[] remainder = args.Skip(1).ToArray();
        if (string.Equals(verb, "list-commands", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleListCommandsAsync(remainder, stdout).ConfigureAwait(false);
        }

        if (string.Equals(verb, "describe-command", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleDescribeCommandAsync(remainder, stdout).ConfigureAwait(false);
        }

        if (string.Equals(verb, "quickstart", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleQuickstartAsync(stdout).ConfigureAwait(false);
        }

        if (string.Equals(verb, "llmstxt", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleLlmstxtAsync(stdout).ConfigureAwait(false);
        }

        if (string.Equals(verb, "run", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleRunAsync(remainder, stdout, cancellationToken, stdin).ConfigureAwait(false);
        }

        if (string.Equals(verb, "validate-input", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleValidateInputAsync(remainder, stdout).ConfigureAwait(false);
        }

        if (_registry.TryGet(verb, out _))
        {
            return await HandleDirectCommandAsync(verb, remainder, stdout, cancellationToken).ConfigureAwait(false);
        }

        _ = stderr;
        await WriteEnvelopeAsync(stdout, ErrorEnvelope("cli.unknown", "unknown_verb", $"Unknown command '{verb}'. Run 'xmlcli llmstxt'.")).ConfigureAwait(false);
        return 1;
    }

    private async Task<int> HandleVersionAsync(TextWriter stdout)
    {
        (string version, string informationalVersion) = GetCliVersions();
        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: true,
                CommandId: "cli.version",
                Version: EnvelopeVersion,
                Data: new { cli_version = version, informational_version = informationalVersion, tool_command = "xmlcli" },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HandleListCommandsAsync(string[] args, TextWriter stdout)
    {
        bool idsOnly = HasOption(args, "--ids-only");
        IReadOnlyList<CommandDescriptor> commands = _registry.ListCommands();
        object data = idsOnly
            ? new { total = commands.Count, command_ids = commands.Select(c => c.Id).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray() }
            : new { total = commands.Count, commands = commands };

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
        if (args.Length < 1)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope("cli.describe_command", "invalid_args", "Usage: describe-command <command-id>")).ConfigureAwait(false);
            return 1;
        }

        string commandId = args[0];
        if (!_registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope("cli.describe_command", "command_not_found", $"Command '{commandId}' was not found.")).ConfigureAwait(false);
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
                    usage = BuildUsage(commandId),
                },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HandleQuickstartAsync(TextWriter stdout)
    {
        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: true,
                CommandId: "cli.quickstart",
                Version: EnvelopeVersion,
                Data: new
                {
                    first_minute_sequence = new[]
                    {
                        "xmlcli llmstxt",
                        "xmlcli list-commands --ids-only",
                        "xmlcli xml.backend_capabilities",
                        "xmlcli xml.validate_document App.xaml",
                        "xmlcli xml.find_elements App.xaml Grid --max-results 20",
                        "xmlcli xml.parse_compare App.xaml",
                    },
                    guardrails = new[]
                    {
                        "Prefer read-only commands before edit commands.",
                        "Use dry-run replace before --apply true.",
                        "Keep responses bounded with max flags.",
                        "language_xml backend is experimental and feature-gated by XMLCLI_ENABLE_LANGUAGE_XML=1.",
                        "xmlcli is XML/XAML structure tooling; use Roslyn/dotnet diagnostics for .xaml.cs semantic correctness.",
                    },
                    intent_recipes = new
                    {
                        triage = new[]
                        {
                            "xmlcli xml.validate_document <file-path>",
                            "xmlcli xml.file_outline <file-path> --brief true --max-nodes 120",
                        },
                        locate_targets = new[]
                        {
                            "xmlcli xml.find_elements <file-path> <element-name> --max-results 40",
                            "xmlcli xml.file_outline <file-path> --brief false --max-nodes 200",
                        },
                        safe_edit = new[]
                        {
                            "xmlcli xml.replace_element_text <file-path> <element-name> <new-text> --apply false",
                            "xmlcli xml.replace_element_text <file-path> <element-name> <new-text> --apply true",
                            "xmlcli xml.validate_document <file-path>",
                        },
                        malformed_analysis = new[]
                        {
                            "xmlcli xml.backend_capabilities",
                            "xmlcli xml.parse_compare <file-path>",
                            "xmlcli xml.file_outline <file-path> --backend language_xml --brief false --max-nodes 120",
                        },
                    },
                    output_hints = new[]
                    {
                        "xml.file_outline with --brief true returns summary counts and omits node list.",
                        "Use --brief false when you need node path/line/column details.",
                        "xml.replace_element_text edits leaf elements only; inspect skipped_non_leaf_count and replaced_count.",
                        "backend=language_xml is read/simulate only and rejects apply=true writes.",
                    },
                },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HandleLlmstxtAsync(TextWriter stdout)
    {
        StringBuilder sb = new();
        sb.AppendLine("# xmlcli llmstxt");
        sb.AppendLine();
        sb.AppendLine("One-shot bootstrap guide for XML/XAML coding workflows.");
        sb.AppendLine();
        sb.AppendLine("## Fast Start");
        sb.AppendLine("1. xmlcli xml.validate_document <file-path>");
        sb.AppendLine("2. xmlcli xml.file_outline <file-path> --brief true");
        sb.AppendLine("3. xmlcli xml.find_elements <file-path> <element-name>");
        sb.AppendLine("4. xmlcli xml.replace_element_text <file-path> <element-name> <new-text> --apply false");
        sb.AppendLine("5. xmlcli xml.parse_compare <file-path>");
        sb.AppendLine("6. xmlcli xml.backend_capabilities");
        sb.AppendLine();
        sb.AppendLine("## Intent Recipes");
        sb.AppendLine("- Triage unknown file:");
        sb.AppendLine("  1) xmlcli xml.validate_document <file-path>");
        sb.AppendLine("  2) xmlcli xml.file_outline <file-path> --brief true --max-nodes 120");
        sb.AppendLine("- Locate concrete edit targets:");
        sb.AppendLine("  1) xmlcli xml.find_elements <file-path> <element-name> --max-results 40");
        sb.AppendLine("  2) xmlcli xml.file_outline <file-path> --brief false --max-nodes 200");
        sb.AppendLine("- Safe edit transaction:");
        sb.AppendLine("  1) xmlcli xml.replace_element_text <file-path> <element-name> <new-text> --apply false");
        sb.AppendLine("  2) review changes_preview / replaced_count / skipped_non_leaf_count");
        sb.AppendLine("  3) rerun with --apply true");
        sb.AppendLine("  4) xmlcli xml.validate_document <file-path>");
        sb.AppendLine("- Malformed XML/XAML investigation:");
        sb.AppendLine("  1) xmlcli xml.backend_capabilities");
        sb.AppendLine("  2) xmlcli xml.parse_compare <file-path>");
        sb.AppendLine("  3) xmlcli xml.file_outline <file-path> --backend language_xml --brief false --max-nodes 120");
        sb.AppendLine();
        sb.AppendLine("## Output Interpretation");
        sb.AppendLine("- `xml.file_outline --brief true` returns summary counts and omits detailed nodes.");
        sb.AppendLine("- Use `--brief false` when you need path/line/column node details.");
        sb.AppendLine("- `xml.replace_element_text` edits leaf elements only.");
        sb.AppendLine("- Check `replaced_count`, `skipped_non_leaf_count`, and `changes_preview` before apply.");
        sb.AppendLine();
        sb.AppendLine("## Scope Boundary");
        sb.AppendLine("- `xmlcli` validates XML/XAML structure, not Roslyn semantic correctness.");
        sb.AppendLine("- For `.xaml.cs` compile/diagnostic truth, run Roslyn/dotnet diagnostics in addition to xmlcli checks.");
        sb.AppendLine();
        sb.AppendLine("## Experimental Backend Mode");
        sb.AppendLine("- Enable with: `XMLCLI_ENABLE_LANGUAGE_XML=1`");
        sb.AppendLine("- Backend values: `xdocument` (default), `language_xml` (experimental)");
        sb.AppendLine("- `language_xml` is read/simulate only. Writes require backend `xdocument`.");
        sb.AppendLine("- Compare strict vs tolerant parse behavior: `xml.parse_compare`");
        sb.AppendLine();
        sb.AppendLine("## Commands");
        foreach (CommandDescriptor descriptor in _registry.ListCommands())
        {
            string access = descriptor.MutatesState ? "write" : "read";
            sb.AppendLine($"- `{descriptor.Id}` (`{access}`): {descriptor.Summary}");
        }

        await stdout.WriteAsync(sb.ToString()).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HandleValidateInputAsync(string[] args, TextWriter stdout)
    {
        (bool ok, string commandId, JsonElement input, CommandEnvelope? error) = TryParseRunLikeArgs(args, "cli.validate_input");
        if (!ok)
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        if (!_registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope("cli.validate_input", "command_not_found", $"Command '{commandId}' was not found.")).ConfigureAwait(false);
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
        return errors.Count == 0 ? 0 : 1;
    }

    private async Task<int> HandleRunAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken, TextReader stdin)
    {
        (bool ok, string commandId, JsonElement input, CommandEnvelope? error) = await TryParseRunLikeArgsAsync(args, "cli.run", stdin).ConfigureAwait(false);
        if (!ok)
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        return await ExecuteCommandAsync(commandId, input, stdout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> HandleDirectCommandAsync(string commandId, string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (!TryBuildDirectInput(commandId, args, out JsonElement input, out CommandEnvelope? error))
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        return await ExecuteCommandAsync(commandId, input, stdout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteCommandAsync(string commandId, JsonElement input, TextWriter stdout, CancellationToken cancellationToken)
    {
        Stopwatch totalTimer = Stopwatch.StartNew();
        if (!_registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope("cli.run", "command_not_found", $"Command '{commandId}' was not found.")).ConfigureAwait(false);
            return 1;
        }

        Stopwatch validateTimer = Stopwatch.StartNew();
        IReadOnlyList<CommandError> validationErrors = command.Validate(input);
        validateTimer.Stop();
        if (validationErrors.Count > 0)
        {
            totalTimer.Stop();
            await WriteEnvelopeAsync(
                stdout,
                new CommandEnvelope(
                    Ok: false,
                    CommandId: commandId,
                    Version: EnvelopeVersion,
                    Data: null,
                    Errors: validationErrors,
                    TraceId: null,
                    Telemetry: BuildCliTelemetry(
                        validateMs: (int)validateTimer.ElapsedMilliseconds,
                        executeMs: null,
                        totalMs: (int)totalTimer.ElapsedMilliseconds,
                        commandTelemetry: null))).ConfigureAwait(false);
            return 1;
        }

        Stopwatch executeTimer = Stopwatch.StartNew();
        CommandExecutionResult result = await command.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        executeTimer.Stop();
        totalTimer.Stop();

        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: result.Ok,
                CommandId: commandId,
                Version: EnvelopeVersion,
                Data: result.Data,
                Errors: result.Errors,
                TraceId: null,
                Telemetry: BuildCliTelemetry(
                    validateMs: (int)validateTimer.ElapsedMilliseconds,
                    executeMs: (int)executeTimer.ElapsedMilliseconds,
                    totalMs: (int)totalTimer.ElapsedMilliseconds,
                    commandTelemetry: result.Telemetry))).ConfigureAwait(false);
        return result.Ok ? 0 : 1;
    }

    private (bool Ok, string CommandId, JsonElement Input, CommandEnvelope? Error) TryParseRunLikeArgs(string[] args, string caller)
    {
        return TryParseRunLikeArgsAsync(args, caller, new StringReader(string.Empty)).GetAwaiter().GetResult();
    }

    private async Task<(bool Ok, string CommandId, JsonElement Input, CommandEnvelope? Error)> TryParseRunLikeArgsAsync(string[] args, string caller, TextReader stdin)
    {
        if (args.Length < 1)
        {
            return (false, string.Empty, default, ErrorEnvelope(caller, "invalid_args", "Usage: run <command-id> [--input <json>|@<file>|-] [--input-stdin]"));
        }

        string commandId = args[0];
        if (args.Length == 1)
        {
            return (true, commandId, ParseJson("{}"), null);
        }

        if (args.Length == 2 && args[1].StartsWith("--input=", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseInputSpec(commandId, args[1]["--input=".Length..], caller);
        }

        if (args.Length == 3 && string.Equals(args[1], "--input", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseInputSpec(commandId, args[2], caller);
        }

        if (args.Length == 2 && string.Equals(args[1], "--input-stdin", StringComparison.OrdinalIgnoreCase))
        {
            string stdinJson = await stdin.ReadToEndAsync().ConfigureAwait(false);
            return TryParseInputSpec(commandId, stdinJson, caller);
        }

        return (false, commandId, default, ErrorEnvelope(caller, "invalid_args", "Unsupported run options."));
    }

    private static (bool Ok, string CommandId, JsonElement Input, CommandEnvelope? Error) TryParseInputSpec(string commandId, string inputSpec, string caller)
    {
        string json = inputSpec;
        if (inputSpec.StartsWith('@'))
        {
            string path = inputSpec[1..];
            if (!File.Exists(path))
            {
                return (false, commandId, default, ErrorEnvelope(caller, "input_file_not_found", $"Input file '{path}' was not found."));
            }

            json = File.ReadAllText(path);
        }

        try
        {
            return (true, commandId, ParseJson(json), null);
        }
        catch (JsonException ex)
        {
            return (false, commandId, default, ErrorEnvelope(caller, "invalid_json", $"Input JSON could not be parsed: {ex.Message}"));
        }
    }

    private static JsonElement ParseJson(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private bool TryBuildDirectInput(string commandId, string[] args, out JsonElement input, out CommandEnvelope? error)
    {
        if (string.Equals(commandId, "system.ping", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 0)
            {
                input = default;
                error = ErrorEnvelope(commandId, "invalid_args", "Usage: system.ping");
                return false;
            }

            input = ParseJson("{}");
            error = null;
            return true;
        }

        if (string.Equals(commandId, "xml.validate_document", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 1)
            {
                input = default;
                error = ErrorEnvelope(commandId, "invalid_args", "Usage: xml.validate_document <file-path> [--backend xdocument|language_xml]");
                return false;
            }

            string filePath = args[0];
            Dictionary<string, object?> payload = new() { ["file_path"] = filePath };
            if (!TryApplyNamedOptions(payload, args.Skip(1).ToArray(), out error))
            {
                input = default;
                return false;
            }

            input = ParseJson(JsonSerializer.Serialize(payload));
            error = null;
            return true;
        }

        if (string.Equals(commandId, "xml.backend_capabilities", StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, object?> payload = new();
            if (!TryApplyNamedOptions(payload, args, out error))
            {
                input = default;
                return false;
            }

            input = ParseJson(JsonSerializer.Serialize(payload));
            return true;
        }

        if (string.Equals(commandId, "xml.file_outline", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 1)
            {
                input = default;
                error = ErrorEnvelope(commandId, "invalid_args", "Usage: xml.file_outline <file-path> [--max-nodes N] [--include-attributes true|false] [--brief true|false] [--backend xdocument|language_xml]");
                return false;
            }

            string filePath = args[0];
            Dictionary<string, object?> payload = new() { ["file_path"] = filePath };
            if (!TryApplyNamedOptions(payload, args.Skip(1).ToArray(), out error))
            {
                input = default;
                return false;
            }

            input = ParseJson(JsonSerializer.Serialize(payload));
            return true;
        }

        if (string.Equals(commandId, "xml.find_elements", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                input = default;
                error = ErrorEnvelope(commandId, "invalid_args", "Usage: xml.find_elements <file-path> <element-name> [--max-results N] [--backend xdocument|language_xml]");
                return false;
            }

            Dictionary<string, object?> payload = new()
            {
                ["file_path"] = args[0],
                ["element_name"] = args[1],
            };
            if (!TryApplyNamedOptions(payload, args.Skip(2).ToArray(), out error))
            {
                input = default;
                return false;
            }

            input = ParseJson(JsonSerializer.Serialize(payload));
            return true;
        }

        if (string.Equals(commandId, "xml.replace_element_text", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3)
            {
                input = default;
                error = ErrorEnvelope(commandId, "invalid_args", "Usage: xml.replace_element_text <file-path> <element-name> <new-text> [--apply true|false] [--backend xdocument]");
                return false;
            }

            Dictionary<string, object?> payload = new()
            {
                ["file_path"] = args[0],
                ["element_name"] = args[1],
                ["new_text"] = args[2],
            };
            if (!TryApplyNamedOptions(payload, args.Skip(3).ToArray(), out error))
            {
                input = default;
                return false;
            }

            input = ParseJson(JsonSerializer.Serialize(payload));
            return true;
        }

        if (string.Equals(commandId, "xml.parse_compare", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 1)
            {
                input = default;
                error = ErrorEnvelope(commandId, "invalid_args", "Usage: xml.parse_compare <file-path> [--require-language-xml true|false]");
                return false;
            }

            Dictionary<string, object?> payload = new()
            {
                ["file_path"] = args[0],
            };
            if (!TryApplyNamedOptions(payload, args.Skip(1).ToArray(), out error))
            {
                input = default;
                return false;
            }

            input = ParseJson(JsonSerializer.Serialize(payload));
            return true;
        }

        input = default;
        error = ErrorEnvelope(commandId, "invalid_args", "Use run <command-id> --input '{...}' for this command.");
        return false;
    }

    private static bool TryApplyNamedOptions(Dictionary<string, object?> payload, string[] args, out CommandEnvelope? error)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                error = ErrorEnvelope("cli.run", "invalid_args", $"Unexpected token '{token}'.");
                return false;
            }

            if (i + 1 >= args.Length)
            {
                error = ErrorEnvelope("cli.run", "invalid_args", $"Option '{token}' requires a value.");
                return false;
            }

            string value = args[++i];
            string name = token.TrimStart('-').Replace("-", "_", StringComparison.Ordinal);
            if (bool.TryParse(value, out bool boolValue))
            {
                payload[name] = boolValue;
                continue;
            }

            if (int.TryParse(value, out int intValue))
            {
                payload[name] = intValue;
                continue;
            }

            payload[name] = value;
        }

        error = null;
        return true;
    }

    private async Task WriteEnvelopeAsync(TextWriter writer, CommandEnvelope envelope)
    {
        CommandEnvelope withHints = envelope with
        {
            Preview = envelope.Preview ?? (envelope.Ok ? $"{envelope.CommandId} ok" : $"{envelope.CommandId} failed"),
            Summary = envelope.Summary ?? (envelope.Ok ? $"{envelope.CommandId} ok" : $"{envelope.CommandId} failed"),
        };

        string json = JsonSerializer.Serialize(withHints, _jsonOptions);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    private static bool IsHelp(string token)
        => token is "-h" or "--help" or "help" or "/?";

    private static bool IsVersion(string token)
        => token is "-v" or "--version";

    private static bool HasOption(string[] args, string option)
        => args.Any(a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));

    private static object BuildUsage(string commandId)
    {
        return commandId switch
        {
            "xml.validate_document" => new { direct = "xml.validate_document <file-path> [--backend xdocument|language_xml]", run = "run xml.validate_document --input '{\"file_path\":\"App.xaml\",\"backend\":\"xdocument\"}'" },
            "xml.backend_capabilities" => new { direct = "xml.backend_capabilities [--include-experimental true|false]", run = "run xml.backend_capabilities --input '{\"include_experimental\":true}'" },
            "xml.file_outline" => new { direct = "xml.file_outline <file-path> [--max-nodes N] [--brief true|false] [--backend xdocument|language_xml]", run = "run xml.file_outline --input '{\"file_path\":\"App.xaml\",\"max_nodes\":200,\"brief\":false,\"backend\":\"xdocument\"}'", hint = "brief=true returns summary counts only; use brief=false for node-level details." },
            "xml.find_elements" => new { direct = "xml.find_elements <file-path> <element-name> [--max-results N] [--backend xdocument|language_xml]", run = "run xml.find_elements --input '{\"file_path\":\"App.xaml\",\"element_name\":\"Grid\",\"backend\":\"xdocument\"}'" },
            "xml.replace_element_text" => new { direct = "xml.replace_element_text <file-path> <element-name> <new-text> [--apply true|false] [--backend xdocument]", run = "run xml.replace_element_text --input '{\"file_path\":\"App.xaml\",\"element_name\":\"Title\",\"new_text\":\"New\",\"apply\":false,\"backend\":\"xdocument\"}'", hint = "leaf elements only; check replaced_count/skipped_non_leaf_count before apply=true." },
            "xml.parse_compare" => new { direct = "xml.parse_compare <file-path> [--require-language-xml true|false]", run = "run xml.parse_compare --input '{\"file_path\":\"App.xaml\"}'", hint = "enable XMLCLI_ENABLE_LANGUAGE_XML=1 to compare strict vs tolerant parsing." },
            _ => new { run = $"run {commandId} --input '{{...}}'" },
        };
    }

    private static (string version, string informationalVersion) GetCliVersions()
    {
        Assembly assembly = typeof(CliApplication).Assembly;
        Version? assemblyVersion = assembly.GetName().Version;
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        string version = assemblyVersion is null
            ? informationalVersion
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        return (version, informationalVersion);
    }

    private static object BuildCliTelemetry(
        int validateMs,
        int? executeMs,
        int totalMs,
        object? commandTelemetry)
    {
        return new
        {
            timing = new
            {
                validate_ms = validateMs,
                execute_ms = executeMs,
                total_ms = totalMs,
            },
            cache_context = new
            {
                binary_launch_mode = IsPublishedModeEnabled("XMLCLI_USE_PUBLISHED")
                    ? "published_cache"
                    : "dotnet_run",
            },
            command_telemetry = commandTelemetry,
        };
    }

    private static bool IsPublishedModeEnabled(string envVarName)
    {
        string? raw = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            _ => false,
        };
    }

    private static CommandEnvelope ErrorEnvelope(string commandId, string code, string message)
        => new(
            Ok: false,
            CommandId: commandId,
            Version: EnvelopeVersion,
            Data: null,
            Errors: new[] { new CommandError(code, message) },
            TraceId: null);

    private static Task WriteHelpAsync(TextWriter writer)
    {
        return writer.WriteLineAsync(
            """
            xmlcli CLI

            Commands:
              version
              list-commands [--ids-only]
              describe-command <command-id>
              quickstart
              llmstxt
              validate-input <command-id> [--input <json>|@<file>]
              run <command-id> [--input <json>|@<file>]
              <command-id> [simple positional args]

            Shorthand:
              xml.backend_capabilities
              xml.validate_document <file-path>
              xml.file_outline <file-path>
              xml.find_elements <file-path> <element-name>
              xml.replace_element_text <file-path> <element-name> <new-text>
              xml.parse_compare <file-path>

            Common workflows:
              triage: xml.validate_document -> xml.file_outline --brief true
              locate: xml.find_elements -> xml.file_outline --brief false
              safe edit: xml.replace_element_text --apply false -> review -> --apply true -> xml.validate_document
              malformed analysis: xml.backend_capabilities -> xml.parse_compare
            """);
    }
}
