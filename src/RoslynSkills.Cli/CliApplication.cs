using RoslynSkills.Contracts;
using RoslynSkills.Core;
using System.Reflection;
using System.Globalization;
using System.Diagnostics;
using System.Text;
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

        if (IsVersion(args[0]))
        {
            return await HandleVersionAsync(stdout).ConfigureAwait(false);
        }

        string verb = args[0];
        string[] remainder = args.Skip(1).ToArray();
        return verb switch
        {
            "version" => await HandleVersionAsync(stdout).ConfigureAwait(false),
            "list-commands" => await HandleListCommandsAsync(remainder, stdout).ConfigureAwait(false),
            "describe-command" => await HandleDescribeCommandAsync(remainder, stdout).ConfigureAwait(false),
            "quickstart" => await HandleQuickstartAsync(stdout).ConfigureAwait(false),
            "llmstxt" => await HandleLlmstxtAsync(remainder, stdout).ConfigureAwait(false),
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
        bool stableOnly = HasOption(args, "--stable-only");
        object pitOfSuccessHints = BuildPitOfSuccessHints();

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
                        usage = "list-commands [--compact] [--ids-only] [--stable-only]",
                        options = new[]
                        {
                            new { name = "--compact", summary = "Return compact descriptors only (id + mutates_state + maturity + traits)." },
                            new { name = "--ids-only", summary = "Return command ids only." },
                            new { name = "--stable-only", summary = "Filter to commands with maturity=stable." },
                        },
                    },
                    Errors: Array.Empty<CommandError>(),
                    TraceId: null)).ConfigureAwait(false);
            return 0;
        }

        IReadOnlyList<CommandDescriptor> allCommands = _registry.ListCommands();
        IReadOnlyList<CommandDescriptor> commands = stableOnly
            ? allCommands.Where(c => string.Equals(c.Maturity, CommandMaturity.Stable, StringComparison.OrdinalIgnoreCase)).ToArray()
            : allCommands;

        object metadata = new
        {
            filter = stableOnly ? "stable_only" : "all",
            maturity_counts = BuildMaturityCounts(allCommands),
        };

        object data = idsOnly
            ? new
            {
                total = commands.Count,
                command_ids = commands
                    .Select(c => c.Id)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                metadata,
                pit_of_success = pitOfSuccessHints,
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
                            c.Maturity,
                            traits = c.Traits ?? Array.Empty<string>(),
                        })
                        .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    metadata,
                    pit_of_success = pitOfSuccessHints,
                }
                : new
                {
                    total = commands.Count,
                    commands,
                    metadata,
                    pit_of_success = pitOfSuccessHints,
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

    private async Task<int> HandleVersionAsync(TextWriter stdout)
    {
        (string version, string informationalVersion) = GetCliVersions();
        await WriteEnvelopeAsync(
            stdout,
            new CommandEnvelope(
                Ok: true,
                CommandId: "cli.version",
                Version: EnvelopeVersion,
                Data: new
                {
                    cli_version = version,
                    informational_version = informationalVersion,
                    tool_command = "roscli",
                },
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
                    summary = "RoslynSkills pit-of-success brief for coding agents.",
                    core_principle = "semantic-first, brief-first, verify-before-finalize",
                    pit_of_success = new[]
                    {
                        "Start with: roscli list-commands --ids-only",
                        "Use roscli list-commands --stable-only --ids-only for strict/default-safe command selection.",
                        "If arguments are unclear: roscli describe-command <command-id>",
                        "If you need agent-facing defaults quickly: copy the prompt block below.",
                        "Use nav.* / ctx.* / diag.* before text fallback.",
                        "Keep payloads brief-first (for example --brief true) before expanding detail.",
                        "For file diagnostics/symbol queries, confirm workspace_context.mode is 'workspace'.",
                        "If workspace_context.mode is 'ad_hoc', rerun with --workspace-path <.csproj|.vbproj|.sln|.slnx|dir>.",
                        "For project-backed files, prefer --require-workspace true to fail closed instead of silently using ad_hoc.",
                        "Validate with diagnostics and build/tests before finalizing.",
                    },
                    first_minute_sequence = new[]
                    {
                        "roscli list-commands --ids-only",
                        "roscli list-commands --stable-only --ids-only",
                        "roscli describe-command session.open",
                        "roscli describe-command edit.create_file",
                        "roscli nav.find_symbol src/MyProject/Program.cs Process --brief true --max-results 20 --require-workspace true",
                    },
                    example_paths = new[]
                    {
                        "src/MyProject/Program.cs",
                        "src/MyProject/Services/OrderService.cs",
                    },
                    quick_recipes = new object[]
                    {
                        new
                        {
                            name = "rename_symbol_safely",
                            commands = new[]
                            {
                                "roscli nav.find_symbol src/MyProject/Program.cs Process --brief true --max-results 20 --require-workspace true",
                                "roscli edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true",
                                "roscli diag.get_file_diagnostics src/MyProject/Program.cs --require-workspace true",
                            },
                        },
                        new
                        {
                            name = "create_new_file_one_shot",
                            commands = new[]
                            {
                                "roscli edit.create_file src/MyProject/NewType.cs --content \"public class NewType { }\"",
                                "roscli diag.get_file_diagnostics src/MyProject/NewType.cs",
                            },
                        },
                        new
                        {
                            name = "session_edit_loop",
                            commands = new[]
                            {
                                "roscli session.open src/MyProject/Program.cs demo-session",
                                "roscli session.status demo-session",
                                "roscli session.diff demo-session",
                                "roscli session.commit demo-session --keep-session false --require-disk-unchanged true",
                            },
                        },
                    },
                    agent_intro_prompt = """
Use roscli for C# and VB.NET work in this session.
Workflow:
1) run "roscli list-commands --ids-only" once.
2) run "roscli list-commands --stable-only --ids-only" when task constraints are unclear.
3) run "roscli quickstart" and follow its recipes.
4) if argument shape is unclear, run "roscli describe-command <command-id>".
5) prefer nav.* / ctx.* / diag.* before text-only fallback.
6) run diagnostics/build/tests before finalizing.
""",
                    complementary_tools = new[]
                    {
                        "Default Rich Lander .NET companion for API/dependency intelligence: dotnet-inspect (<command>, or dnx dotnet-inspect -y -- <command> if not installed).",
                        "dotnet-skills packages assistant skills around dotnet-inspect; it is not the primary inspection CLI.",
                        "For in-repo semantic edits and diagnostics, use roscli.",
                    },
                    guardrails = new[]
                    {
                        "session.open only supports .cs/.csx files.",
                        "Do not use session.open on .sln/.slnx/.csproj files.",
                        "Maturity policy: default to stable commands; use advanced/experimental only when needed and after describe-command.",
                        "diag/nav file commands auto-resolve nearest workspace; check workspace_context.mode in responses.",
                        "If workspace_context.mode is ad_hoc for a project file, pass --workspace-path explicitly.",
                        "For project-backed files where ad_hoc is unacceptable, set --require-workspace true.",
                        "For complex JSON payloads, prefer --input-stdin over shell-escaped inline JSON.",
                        "If roscli cannot answer a C# query, state why before fallback.",
                    },
                    anti_patterns = new[]
                    {
                        "Do not start by opening .sln/.slnx/.csproj with session.open.",
                        "Do not run broad solution diagnostics repeatedly when a file-level check is enough.",
                        "Do not hand-edit complex multi-file refactors before trying semantic commands.",
                    },
                },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);

        return 0;
    }

    private async Task<int> HandleLlmstxtAsync(string[] args, TextWriter stdout)
    {
        if (args.Any(a => IsHelp(a)))
        {
            await stdout.WriteLineAsync("Usage: roscli llmstxt [--full]").ConfigureAwait(false);
            await stdout.WriteLineAsync("Emit one-shot markdown bootstrap guidance for coding agents.").ConfigureAwait(false);
            return 0;
        }

        bool full = HasOption(args, "--full");
        string guide = BuildLlmstxt(full);
        await stdout.WriteAsync(guide).ConfigureAwait(false);

        if (!guide.EndsWith('\n'))
        {
            await stdout.WriteLineAsync().ConfigureAwait(false);
        }

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
        Stopwatch totalTimer = Stopwatch.StartNew();
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
            message: $"Unknown command '{verb}'. Use '--help', 'llmstxt', 'quickstart', or 'list-commands --ids-only' to view available commands.")).ConfigureAwait(false);
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

    private static bool IsVersion(string value)
        => string.Equals(value, "--version", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "-v", StringComparison.OrdinalIgnoreCase);

    private static (string Version, string InformationalVersion) GetCliVersions()
    {
        Assembly assembly = typeof(CliApplication).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            string displayVersion = informational.Split('+', 2, StringSplitOptions.TrimEntries)[0];
            if (!string.IsNullOrWhiteSpace(displayVersion))
            {
                return (displayVersion, informational);
            }
        }

        string fallback = assembly.GetName().Version?.ToString() ?? "unknown";
        return (fallback, fallback);
    }

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
            TryPromoteOptionToPositional(options, "session_id", ref positionalArgs, 1);
        }

        if (string.Equals(commandId, "nav.find_symbol", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "file_path", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "symbol_name", ref positionalArgs, 1);
        }

        if (string.Equals(commandId, "nav.find_invocations", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "file_path", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "line", ref positionalArgs, 1);
            TryPromoteOptionToPositional(options, "column", ref positionalArgs, 2);
        }

        if (string.Equals(commandId, "nav.call_hierarchy", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "file_path", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "line", ref positionalArgs, 1);
            TryPromoteOptionToPositional(options, "column", ref positionalArgs, 2);
        }

        if (string.Equals(commandId, "nav.call_path", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "source_file_path", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "source_line", ref positionalArgs, 1);
            TryPromoteOptionToPositional(options, "source_column", ref positionalArgs, 2);
            TryPromoteOptionToPositional(options, "target_file_path", ref positionalArgs, 3);
            TryPromoteOptionToPositional(options, "target_line", ref positionalArgs, 4);
            TryPromoteOptionToPositional(options, "target_column", ref positionalArgs, 5);
        }

        if (string.Equals(commandId, "ctx.search_text", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "pattern", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "root", ref positionalArgs, 1);
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
            case "diag.get_workspace_snapshot":
                if (positionalArgs.Length > 1)
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, $"{commandId} [directory-path] [--option value ...]"));
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

            case "nav.find_symbol_batch":
                if (!TryResolveQueriesArgument(commandId, positionalArgs, options, out object? symbolBatchQueries, out error))
                {
                    return false;
                }

                input["queries"] = symbolBatchQueries;
                break;

            case "nav.find_invocations":
                if (positionalArgs.Length != 3 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int invocationLine) ||
                    !int.TryParse(positionalArgs[2], out int invocationColumn))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "nav.find_invocations <file-path> <line> <column> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["line"] = invocationLine;
                input["column"] = invocationColumn;
                break;

            case "nav.call_hierarchy":
                if (positionalArgs.Length != 3 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int callHierarchyLine) ||
                    !int.TryParse(positionalArgs[2], out int callHierarchyColumn))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, $"{commandId} <file-path> <line> <column> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["line"] = callHierarchyLine;
                input["column"] = callHierarchyColumn;
                break;

            case "nav.call_path":
                if (positionalArgs.Length != 6 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int sourceLine) ||
                    !int.TryParse(positionalArgs[2], out int sourceColumn) ||
                    string.IsNullOrWhiteSpace(positionalArgs[3]) ||
                    !int.TryParse(positionalArgs[4], out int targetLine) ||
                    !int.TryParse(positionalArgs[5], out int targetColumn))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "nav.call_path <source-file-path> <source-line> <source-column> <target-file-path> <target-line> <target-column> [--option value ...]"));
                    return false;
                }

                input["source_file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["source_line"] = sourceLine;
                input["source_column"] = sourceColumn;
                input["target_file_path"] = NormalizeCliPathValue(positionalArgs[3]);
                input["target_line"] = targetLine;
                input["target_column"] = targetColumn;
                break;

            case "analyze.unused_private_symbols":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "analyze.unused_private_symbols <workspace-path> [--option value ...]"));
                    return false;
                }

                input["workspace_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "analyze.control_flow_graph":
                if (positionalArgs.Length != 3 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int cfgLine) ||
                    !int.TryParse(positionalArgs[2], out int cfgColumn))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "analyze.control_flow_graph <file-path> <line> <column> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["line"] = cfgLine;
                input["column"] = cfgColumn;
                break;

            case "analyze.dataflow_slice":
                if (positionalArgs.Length != 3 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int dataflowLine) ||
                    !int.TryParse(positionalArgs[2], out int dataflowColumn))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "analyze.dataflow_slice <file-path> <line> <column> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["line"] = dataflowLine;
                input["column"] = dataflowColumn;
                break;

            case "analyze.dependency_violations":
                if (positionalArgs.Length < 3 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "analyze.dependency_violations <workspace-path> <layer1> <layer2> [layerN ...] [--option value ...]"));
                    return false;
                }

                input["workspace_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["layers"] = positionalArgs.Skip(1).ToArray();
                break;

            case "analyze.impact_slice":
                if (positionalArgs.Length != 3 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]) ||
                    !int.TryParse(positionalArgs[1], out int impactLine) ||
                    !int.TryParse(positionalArgs[2], out int impactColumn))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "analyze.impact_slice <file-path> <line> <column> [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                input["line"] = impactLine;
                input["column"] = impactColumn;
                break;

            case "analyze.override_coverage":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "analyze.override_coverage <workspace-path> [--option value ...]"));
                    return false;
                }

                input["workspace_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "analyze.async_risk_scan":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "analyze.async_risk_scan <workspace-path> [--option value ...]"));
                    return false;
                }

                input["workspace_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "ctx.search_text":
                if (positionalArgs.Length < 1 ||
                    positionalArgs.Length > 2 ||
                    string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "ctx.search_text <pattern> [root-or-file] [--option value ...]"));
                    return false;
                }

                input["patterns"] = new[] { positionalArgs[0] };
                if (positionalArgs.Length == 2 && !string.IsNullOrWhiteSpace(positionalArgs[1]))
                {
                    string normalizedPath = NormalizeCliPathValue(positionalArgs[1]);
                    if (File.Exists(normalizedPath))
                    {
                        input["file_path"] = normalizedPath;
                    }
                    else
                    {
                        input["roots"] = new[] { normalizedPath };
                    }
                }
                break;

            case "query.batch":
                if (!TryResolveQueriesArgument(commandId, positionalArgs, options, out object? queryBatchQueries, out error))
                {
                    return false;
                }

                input["queries"] = queryBatchQueries;
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
            if (string.Equals(key, "roots", StringComparison.OrdinalIgnoreCase))
            {
                if (TryConvertOptionToStringArray(value, out string[] roots))
                {
                    input[key] = roots.Select(NormalizeCliPathValue).ToArray();
                }
                else
                {
                    input[key] = value;
                }

                continue;
            }

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
            "diag.get_workspace_snapshot" => true,
            "repair.propose_from_diagnostics" => true,
            "nav.find_symbol" => true,
            "nav.find_symbol_batch" => true,
            "nav.find_invocations" => true,
            "nav.call_hierarchy" => true,
            "nav.call_path" => true,
            "analyze.unused_private_symbols" => true,
            "analyze.control_flow_graph" => true,
            "analyze.dataflow_slice" => true,
            "analyze.dependency_violations" => true,
            "analyze.impact_slice" => true,
            "analyze.override_coverage" => true,
            "analyze.async_risk_scan" => true,
            "ctx.search_text" => true,
            "query.batch" => true,
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

        if (value.StartsWith("{", StringComparison.Ordinal) || value.StartsWith("[", StringComparison.Ordinal))
        {
            return value;
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

    private static bool TryConvertOptionToStringArray(object? optionValue, out string[] values)
    {
        switch (optionValue)
        {
            case string stringValue when !string.IsNullOrWhiteSpace(stringValue):
                values = [stringValue];
                return true;

            case string[] stringArray:
                values = stringArray.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                return values.Length > 0;

            case List<object?> list:
                List<string> collected = new();
                foreach (object? item in list)
                {
                    if (TryConvertOptionToSingleString(item, out string candidate) &&
                        !string.IsNullOrWhiteSpace(candidate))
                    {
                        collected.Add(candidate);
                    }
                }

                values = collected.ToArray();
                return values.Length > 0;

            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                values = element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
                return values.Length > 0;
        }

        values = Array.Empty<string>();
        return false;
    }

    private static bool TryResolveQueriesArgument(
        string commandId,
        string[] positionalArgs,
        Dictionary<string, object?> options,
        out object? queriesValue,
        out CommandEnvelope? error)
    {
        queriesValue = null;
        error = null;

        if (positionalArgs.Length > 1)
        {
            error = ErrorEnvelope(
                commandId: "cli",
                code: "invalid_args",
                message: BuildUsageMessage(commandId, $"{commandId} [queries-json-or-file] [--queries @file.json] [--option value ...]"));
            return false;
        }

        if (positionalArgs.Length == 1 && options.ContainsKey("queries"))
        {
            error = ErrorEnvelope(
                commandId: "cli",
                code: "invalid_args",
                message: "Provide either positional queries payload/file or --queries, not both.");
            return false;
        }

        object? rawQueries = null;
        if (options.TryGetValue("queries", out object? optionsQueries))
        {
            rawQueries = optionsQueries;
            options.Remove("queries");
        }
        else if (positionalArgs.Length == 1)
        {
            rawQueries = positionalArgs[0];
        }

        if (rawQueries is null)
        {
            error = ErrorEnvelope(
                commandId: "cli",
                code: "invalid_args",
                message: BuildUsageMessage(commandId, $"{commandId} [queries-json-or-file] [--queries @file.json] [--option value ...]"));
            return false;
        }

        if (!TryParseQueriesPayload(rawQueries, out queriesValue, out string payloadError))
        {
            error = ErrorEnvelope(
                commandId: "cli",
                code: "invalid_args",
                message: payloadError);
            return false;
        }

        return true;
    }

    private static bool TryParseQueriesPayload(object rawQueries, out object queriesValue, out string error)
    {
        error = string.Empty;
        queriesValue = Array.Empty<object>();

        if (!TryNormalizeQueriesPayload(rawQueries, out string payloadText, out error))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadText);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                queriesValue = JsonSerializer.Deserialize<object>(root.GetRawText()) ?? Array.Empty<object>();
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("queries", out JsonElement queriesProperty) &&
                queriesProperty.ValueKind == JsonValueKind.Array)
            {
                queriesValue = JsonSerializer.Deserialize<object>(queriesProperty.GetRawText()) ?? Array.Empty<object>();
                return true;
            }

            error = "Queries payload must be a JSON array or an object with a 'queries' array property.";
            return false;
        }
        catch (JsonException ex)
        {
            error = $"Queries payload is invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryNormalizeQueriesPayload(object rawQueries, out string payloadText, out string error)
    {
        payloadText = string.Empty;
        error = string.Empty;

        if (rawQueries is List<object?> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (TryNormalizeQueriesPayload(list[i]!, out payloadText, out error))
                {
                    return true;
                }
            }

            error = "Queries payload was provided multiple times but none were valid.";
            return false;
        }

        if (rawQueries is not string stringValue || string.IsNullOrWhiteSpace(stringValue))
        {
            error = "Queries payload must be a non-empty JSON string or @file path.";
            return false;
        }

        string candidate = stringValue.Trim();
        bool explicitFile = candidate.StartsWith('@');
        if (explicitFile)
        {
            candidate = candidate[1..].Trim();
        }

        if (explicitFile || File.Exists(candidate))
        {
            if (!File.Exists(candidate))
            {
                error = $"Queries input file '{candidate}' does not exist.";
                return false;
            }

            payloadText = File.ReadAllText(candidate);
            return true;
        }

        payloadText = candidate;
        return true;
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
            string workspaceMode = ResolveWorkspaceMode(element);
            if (totalMatches >= 0)
            {
                return string.IsNullOrWhiteSpace(workspaceMode)
                    ? $"matches={totalMatches}"
                    : $"matches={totalMatches}, workspace={workspaceMode}";
            }
        }

        if (string.Equals(commandId, "nav.find_invocations", StringComparison.OrdinalIgnoreCase))
        {
            int totalMatches = TryGetInt(element, "total_matches", out int matches) ? matches : -1;
            string workspaceMode = ResolveWorkspaceMode(element);
            if (totalMatches >= 0)
            {
                return string.IsNullOrWhiteSpace(workspaceMode)
                    ? $"matches={totalMatches}"
                    : $"matches={totalMatches}, workspace={workspaceMode}";
            }
        }

        if (string.Equals(commandId, "nav.call_hierarchy", StringComparison.OrdinalIgnoreCase))
        {
            int totalNodes = TryGetInt(element, "total_nodes", out int nodes) ? nodes : -1;
            int totalEdges = TryGetInt(element, "total_edges", out int edges) ? edges : -1;
            string workspaceMode = ResolveWorkspaceMode(element);
            if (totalNodes >= 0 || totalEdges >= 0)
            {
                string summary = $"nodes={Math.Max(totalNodes, 0)}, edges={Math.Max(totalEdges, 0)}";
                return string.IsNullOrWhiteSpace(workspaceMode)
                    ? summary
                    : $"{summary}, workspace={workspaceMode}";
            }
        }

        if (string.Equals(commandId, "nav.call_path", StringComparison.OrdinalIgnoreCase))
        {
            bool pathFound = TryGetBool(element, "path_found", out bool found) && found;
            int pathEdgeLength = TryGetInt(element, "path_edge_length", out int edgeLength) ? edgeLength : -1;
            string workspaceMode = ResolveWorkspaceMode(element);
            if (pathEdgeLength >= 0 || pathFound)
            {
                string summary = $"path_found={pathFound.ToString().ToLowerInvariant()}, edges={Math.Max(pathEdgeLength, 0)}";
                return string.IsNullOrWhiteSpace(workspaceMode)
                    ? summary
                    : $"{summary}, workspace={workspaceMode}";
            }
        }

        if (string.Equals(commandId, "analyze.unused_private_symbols", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "analysis_scope", out JsonElement scope) &&
                TryGetInt(scope, "unused_candidates", out int unused) &&
                TryGetInt(scope, "total_candidates", out int total))
            {
                return $"unused={unused}, candidates={total}";
            }
        }

        if (string.Equals(commandId, "analyze.control_flow_graph", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "cfg_summary", out JsonElement cfgSummary) &&
                TryGetInt(cfgSummary, "total_blocks", out int blocks) &&
                TryGetInt(cfgSummary, "total_edges", out int edges))
            {
                return $"blocks={blocks}, edges={edges}";
            }
        }

        if (string.Equals(commandId, "analyze.dataflow_slice", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "counts", out JsonElement counts) &&
                TryGetInt(counts, "read_inside", out int readInside) &&
                TryGetInt(counts, "written_inside", out int writtenInside))
            {
                return $"read_inside={readInside}, written_inside={writtenInside}";
            }
        }

        if (string.Equals(commandId, "analyze.dependency_violations", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "analysis_scope", out JsonElement scope) &&
                TryGetInt(scope, "total_violations", out int violations))
            {
                return $"violations={violations}";
            }
        }

        if (string.Equals(commandId, "analyze.impact_slice", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "impact_counts", out JsonElement counts) &&
                TryGetInt(counts, "total", out int total))
            {
                return $"impact_total={total}";
            }
        }

        if (string.Equals(commandId, "analyze.override_coverage", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "analysis_scope", out JsonElement scope) &&
                TryGetInt(scope, "findings", out int findings))
            {
                return $"findings={findings}";
            }
        }

        if (string.Equals(commandId, "analyze.async_risk_scan", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetObject(element, "summary", out JsonElement summaryElement) &&
                TryGetInt(summaryElement, "total_findings", out int findings))
            {
                return $"findings={findings}";
            }
        }

        if (string.Equals(commandId, "ctx.search_text", StringComparison.OrdinalIgnoreCase))
        {
            int totalMatches = TryGetInt(element, "total_matches", out int matches) ? matches : -1;
            int filesScanned = TryGetInt(element, "files_scanned", out int scanned) ? scanned : -1;
            if (totalMatches >= 0)
            {
                return filesScanned >= 0
                    ? $"matches={totalMatches}, files={filesScanned}"
                    : $"matches={totalMatches}";
            }
        }

        if (string.Equals(commandId, "query.batch", StringComparison.OrdinalIgnoreCase))
        {
            int totalExecuted = TryGetInt(element, "total_executed", out int executed) ? executed : -1;
            int succeeded = TryGetInt(element, "succeeded", out int ok) ? ok : -1;
            int failed = TryGetInt(element, "failed", out int fail) ? fail : -1;
            if (totalExecuted >= 0)
            {
                return $"executed={totalExecuted}, ok={Math.Max(succeeded, 0)}, failed={Math.Max(failed, 0)}";
            }
        }

        if (string.Equals(commandId, "nav.find_symbol_batch", StringComparison.OrdinalIgnoreCase))
        {
            int totalExecuted = TryGetInt(element, "total_executed", out int executed) ? executed : -1;
            int succeeded = TryGetInt(element, "succeeded", out int ok) ? ok : -1;
            int failed = TryGetInt(element, "failed", out int fail) ? fail : -1;
            if (totalExecuted >= 0)
            {
                return $"executed={totalExecuted}, ok={Math.Max(succeeded, 0)}, failed={Math.Max(failed, 0)}";
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

        if (string.Equals(commandId, "diag.get_file_diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            int total = TryGetInt(element, "total", out int totalDiagnostics) ? totalDiagnostics : -1;
            int errors = TryGetInt(element, "errors", out int errorCount) ? errorCount : -1;
            int warnings = TryGetInt(element, "warnings", out int warningCount) ? warningCount : -1;
            string workspaceMode = ResolveWorkspaceMode(element);
            if (total >= 0)
            {
                string summary = $"total={total}, errors={Math.Max(errors, 0)}, warnings={Math.Max(warnings, 0)}";
                return string.IsNullOrWhiteSpace(workspaceMode)
                    ? summary
                    : $"{summary}, workspace={workspaceMode}";
            }
        }

        if (string.Equals(commandId, "diag.get_solution_snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandId, "diag.get_workspace_snapshot", StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(commandId, "cli.version", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(element, "cli_version", out string version) && !string.IsNullOrWhiteSpace(version))
            {
                return $"roscli {version}";
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

    private static string ResolveWorkspaceMode(JsonElement element)
    {
        if (TryGetObject(element, "workspace_context", out JsonElement workspaceContext) &&
            TryGetString(workspaceContext, "mode", out string mode) &&
            !string.IsNullOrWhiteSpace(mode))
        {
            return mode;
        }

        if (TryGetObject(element, "query", out JsonElement query) &&
            TryGetObject(query, "workspace_context", out JsonElement queryWorkspaceContext) &&
            TryGetString(queryWorkspaceContext, "mode", out string queryMode) &&
            !string.IsNullOrWhiteSpace(queryMode))
        {
            return queryMode;
        }

        return string.Empty;
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
                optional_properties = new[] { "brief", "max_results", "context_lines", "declarations_only", "first_declaration", "snippet_single_line", "max_snippet_chars", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "Use declarations_only=true when you only want declaration anchors.",
                    "Use first_declaration=true to prefer declaration match and fallback to first match when no declaration exists.",
                    "By default, roscli auto-resolves nearest .csproj/.vbproj/.sln/.slnx from file path.",
                    "If workspace_context.mode is 'ad_hoc', pass workspace_path explicitly.",
                    "Set require_workspace=true for project-backed files when ad_hoc fallback should fail closed.",
                },
            };
        }

        if (string.Equals(commandId, "nav.find_symbol_batch", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "nav.find_symbol_batch [queries-json-or-file] [--queries @file.json] [--option value ...]",
                run = "run nav.find_symbol_batch --input '{\"queries\":[{\"file_path\":\"src/A.cs\",\"symbol_name\":\"Run\"},{\"file_path\":\"src/B.cs\",\"symbol_name\":\"Run\"}],\"brief\":true,\"first_declaration\":true,\"continue_on_error\":true}'",
                required_properties = new[] { "queries" },
                optional_properties = new[] { "continue_on_error", "brief", "max_results", "context_lines", "declarations_only", "first_declaration", "snippet_single_line", "max_snippet_chars", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "Top-level options are defaults for all queries; per-query properties override defaults.",
                    "Each query item requires file_path and symbol_name and may include optional label.",
                    "For shorthand, pass --queries @file.json or positional file path containing a JSON array.",
                },
            };
        }

        if (string.Equals(commandId, "nav.find_invocations", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "nav.find_invocations <file-path> <line> <column> [--option value ...]",
                run = "run nav.find_invocations --input '{\"file_path\":\"src/MyFile.cs\",\"line\":12,\"column\":15,\"brief\":true}'",
                required_properties = new[] { "file_path", "line", "column" },
                optional_properties = new[] { "brief", "max_results", "context_lines", "include_object_creations", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "Use line/column anchored on a method declaration or method reference token.",
                    "For project-backed files, set require_workspace=true to fail closed if context falls back to ad_hoc.",
                },
            };
        }

        if (string.Equals(commandId, "nav.call_hierarchy", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "nav.call_hierarchy <file-path> <line> <column> [--option value ...]",
                run = "run nav.call_hierarchy --input '{\"file_path\":\"src/MyFile.cs\",\"line\":12,\"column\":15,\"direction\":\"both\",\"max_depth\":2,\"brief\":true}'",
                required_properties = new[] { "file_path", "line", "column" },
                optional_properties = new[] { "direction", "max_depth", "max_nodes", "max_edges", "context_lines", "brief", "include_object_creations", "include_external", "include_generated", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "nav.call_hierarchy is the canonical Roslyn-style call graph command.",
                    "direction accepts incoming, outgoing, or both.",
                    "Call hierarchy is recursive by depth and returns nodes+edges, unlike flat call-site queries.",
                    "Advanced/heuristic command: may omit dynamic/reflection/DI dispatch paths.",
                },
            };
        }

        if (string.Equals(commandId, "nav.call_path", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "nav.call_path <source-file-path> <source-line> <source-column> <target-file-path> <target-line> <target-column> [--option value ...]",
                run = "run nav.call_path --input '{\"source_file_path\":\"src/Source.cs\",\"source_line\":12,\"source_column\":15,\"target_file_path\":\"src/Target.cs\",\"target_line\":40,\"target_column\":18,\"max_depth\":8,\"brief\":true}'",
                required_properties = new[] { "source_file_path", "source_line", "source_column", "target_file_path", "target_line", "target_column" },
                optional_properties = new[] { "max_depth", "max_nodes", "max_graph_edges", "context_lines", "brief", "include_object_creations", "include_external", "include_generated", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "Finds a shortest outgoing call path from source method to target method.",
                    "Experimental/heuristic command: dynamic dispatch, reflection, or DI-only edges may be missed.",
                    "For project-backed files, set require_workspace=true to fail closed if context falls back to ad_hoc.",
                },
            };
        }

        if (string.Equals(commandId, "analyze.unused_private_symbols", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "analyze.unused_private_symbols <workspace-path> [--option value ...]",
                run = "run analyze.unused_private_symbols --input '{\"workspace_path\":\"src\",\"brief\":true,\"max_symbols\":200}'",
                required_properties = new[] { "workspace_path" },
                optional_properties = new[] { "include_generated", "max_files", "max_symbols", "brief" },
                notes = new[]
                {
                    "Advanced/heuristic command: reflection and source-generated usage can be missed.",
                    "Use max_symbols to keep output bounded in large repositories.",
                },
            };
        }

        if (string.Equals(commandId, "analyze.control_flow_graph", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "analyze.control_flow_graph <file-path> <line> <column> [--option value ...]",
                run = "run analyze.control_flow_graph --input '{\"file_path\":\"src/MyFile.cs\",\"line\":12,\"column\":15,\"brief\":true,\"max_blocks\":200,\"max_edges\":500}'",
                required_properties = new[] { "file_path", "line", "column" },
                optional_properties = new[] { "brief", "max_blocks", "max_edges", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "Stable flow-analysis command backed by Roslyn ControlFlowGraph APIs.",
                    "For project-backed files, set require_workspace=true to fail closed if context falls back to ad_hoc.",
                },
            };
        }

        if (string.Equals(commandId, "analyze.dataflow_slice", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "analyze.dataflow_slice <file-path> <line> <column> [--option value ...]",
                run = "run analyze.dataflow_slice --input '{\"file_path\":\"src/MyFile.cs\",\"line\":12,\"column\":15,\"brief\":true,\"max_symbols\":200}'",
                required_properties = new[] { "file_path", "line", "column" },
                optional_properties = new[] { "brief", "max_symbols", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "Advanced flow-analysis command backed by Roslyn AnalyzeDataFlow APIs.",
                    "Region selection is anchor-based and may expand to an enclosing executable node.",
                },
            };
        }

        if (string.Equals(commandId, "analyze.dependency_violations", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "analyze.dependency_violations <workspace-path> <layer1> <layer2> [layerN ...] [--option value ...]",
                run = "run analyze.dependency_violations --input '{\"workspace_path\":\"src\",\"layers\":[\"MyApp.Web\",\"MyApp.Application\",\"MyApp.Domain\"],\"direction\":\"toward_end\",\"brief\":true}'",
                required_properties = new[] { "workspace_path", "layers" },
                optional_properties = new[] { "direction", "ignore_same_namespace", "include_generated", "max_files", "max_violations", "brief" },
                notes = new[]
                {
                    "Experimental command: layer matching is namespace-prefix based.",
                    "direction=toward_end means earlier layers cannot depend on later-disallowed direction (clean-architecture style ordering).",
                },
            };
        }

        if (string.Equals(commandId, "analyze.impact_slice", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "analyze.impact_slice <file-path> <line> <column> [--option value ...]",
                run = "run analyze.impact_slice --input '{\"file_path\":\"src/MyFile.cs\",\"line\":42,\"column\":17,\"include_references\":true,\"include_callers\":true,\"include_callees\":true,\"brief\":true}'",
                required_properties = new[] { "file_path", "line", "column" },
                optional_properties = new[] { "workspace_path", "require_workspace", "include_references", "include_callers", "include_callees", "include_overrides", "include_implementations", "max_references", "max_callers", "max_callees", "max_related", "brief" },
                notes = new[]
                {
                    "Impact slice is bounded and heuristic; dynamic dispatch/reflection edges can be missed.",
                    "For project-backed files, set require_workspace=true to fail closed if context falls back to ad_hoc.",
                },
            };
        }

        if (string.Equals(commandId, "analyze.override_coverage", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "analyze.override_coverage <workspace-path> [--option value ...]",
                run = "run analyze.override_coverage --input '{\"workspace_path\":\"src\",\"coverage_threshold\":0.6,\"min_derived_types\":1,\"brief\":true}'",
                required_properties = new[] { "workspace_path" },
                optional_properties = new[] { "coverage_threshold", "min_derived_types", "include_generated", "max_files", "max_members", "brief" },
                notes = new[]
                {
                    "Coverage is source-only and intended for hotspot triage.",
                },
            };
        }

        if (string.Equals(commandId, "analyze.async_risk_scan", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "analyze.async_risk_scan <workspace-path> [--option value ...]",
                run = "run analyze.async_risk_scan --input '{\"workspace_path\":\"src\",\"max_findings\":300,\"severity_filter\":[\"warning\",\"info\"],\"brief\":true}'",
                required_properties = new[] { "workspace_path" },
                optional_properties = new[] { "severity_filter", "include_generated", "max_files", "max_findings", "brief" },
                notes = new[]
                {
                    "Experimental/heuristic command: review findings before changing behavior.",
                },
            };
        }

        if (string.Equals(commandId, "ctx.search_text", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "ctx.search_text <pattern> [root-or-file] [--option value ...]",
                run = "run ctx.search_text --input '{\"patterns\":[\"RemoteUserAction\",\"ReplicationUpdate\"],\"mode\":\"literal\",\"roots\":[\"src\"],\"max_results\":200}'",
                required_properties = new[] { "pattern|patterns", "file_path|roots|workspace_path" },
                optional_properties = new[] { "mode", "case_sensitive", "include_globs", "exclude_globs", "max_results", "max_files", "context_lines", "brief" },
                notes = new[]
                {
                    "Scope is mandatory: set file_path, roots, or workspace_path.",
                    "Use mode=regex for advanced matching; invalid regex patterns fail fast.",
                },
            };
        }

        if (string.Equals(commandId, "query.batch", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "query.batch [queries-json-or-file] [--queries @file.json] [--option value ...]",
                run = "run query.batch --input '{\"queries\":[{\"command_id\":\"ctx.search_text\",\"input\":{\"patterns\":[\"RemoteUserAction\"],\"roots\":[\"src\"]}},{\"command_id\":\"nav.find_invocations\",\"input\":{\"file_path\":\"src/MyFile.cs\",\"line\":42,\"column\":15}}],\"continue_on_error\":true}'",
                required_properties = new[] { "queries" },
                optional_properties = new[] { "continue_on_error" },
                notes = new[]
                {
                    "query.batch supports read-only investigative commands only.",
                    "Each query item must provide command_id and input.",
                    "For shorthand, pass --queries @file.json or positional file path containing a JSON array.",
                },
            };
        }

        if (string.Equals(commandId, "diag.get_file_diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "diag.get_file_diagnostics <file-path> [--workspace-path <path>] [--option value ...]",
                run = "run diag.get_file_diagnostics --input '{\"file_path\":\"src/MyFile.cs\",\"workspace_path\":\"src/MyProject/MyProject.csproj\",\"require_workspace\":true}'",
                required_properties = new[] { "file_path" },
                optional_properties = new[] { "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "By default, roscli auto-resolves nearest .csproj/.vbproj/.sln/.slnx from file path.",
                    "Response includes workspace_context.mode = workspace|ad_hoc.",
                    "Set require_workspace=true to fail closed when workspace resolution falls back to ad_hoc.",
                },
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

    private static object BuildPitOfSuccessHints()
    {
        return new
        {
            quickstart = "Run 'quickstart' for a compact pit-of-success workflow brief.",
            first_steps = new[]
            {
                "list-commands --ids-only",
                "describe-command session.open",
                "describe-command edit.create_file",
            },
            guardrails = new[]
            {
                "session.open supports only .cs/.csx files.",
                "Check workspace_context.mode on nav/diag file commands.",
                "Use --workspace-path when auto workspace resolution falls back to ad_hoc.",
                "Use --require-workspace true when ad_hoc fallback is unacceptable.",
                "Prefer --input-stdin for complex JSON payloads.",
                "Prefer stable commands by default; use advanced/experimental commands intentionally.",
            },
            maturity = new
            {
                stable = "Default path: expected deterministic behavior and primary support.",
                advanced = "Useful for deeper analysis; may be slower and/or partially heuristic.",
                experimental = "Evolving contract; useful signals but lower stability guarantees.",
            },
        };
    }

    private string BuildLlmstxt(bool full)
    {
        IReadOnlyList<CommandDescriptor> allCommands = _registry.ListCommands()
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<CommandDescriptor> visibleCommands = full
            ? allCommands
            : allCommands.Where(c => string.Equals(c.Maturity, CommandMaturity.Stable, StringComparison.OrdinalIgnoreCase)).ToArray();

        int stable = allCommands.Count(c => string.Equals(c.Maturity, CommandMaturity.Stable, StringComparison.OrdinalIgnoreCase));
        int advanced = allCommands.Count(c => string.Equals(c.Maturity, CommandMaturity.Advanced, StringComparison.OrdinalIgnoreCase));
        int experimental = allCommands.Count(c => string.Equals(c.Maturity, CommandMaturity.Experimental, StringComparison.OrdinalIgnoreCase));
        (string version, _) = GetCliVersions();

        StringBuilder sb = new();
        sb.AppendLine("# roscli llmstxt");
        sb.AppendLine();
        sb.AppendLine("One-shot bootstrap guide for coding agents using RoslynSkills.");
        sb.AppendLine();
        sb.AppendLine("## Scope");
        sb.AppendLine($"- cli version: `{version}`");
        sb.AppendLine($"- catalog mode: `{(full ? "all" : "stable-only")}` ({visibleCommands.Count}/{allCommands.Count} commands shown)");
        sb.AppendLine($"- maturity totals: stable={stable}, advanced={advanced}, experimental={experimental}");
        if (!full && visibleCommands.Count < allCommands.Count)
        {
            sb.AppendLine($"- omitted {allCommands.Count - visibleCommands.Count} advanced/experimental commands; run `roscli llmstxt --full` for complete coverage.");
        }

        sb.AppendLine();
        sb.AppendLine("## Fast Start (Low Round-Trips)");
        sb.AppendLine("1. Pick a command from the catalog below and run it directly.");
        sb.AppendLine("2. Call `roscli describe-command <command-id>` only when argument shape is unclear.");
        sb.AppendLine("3. Use `diag.get_file_diagnostics` (or build/tests) before finalizing edits.");
        sb.AppendLine();
        sb.AppendLine("## Guardrails");
        sb.AppendLine("- `session.open` supports only `.cs/.csx` files.");
        sb.AppendLine("- For `nav.*` and `diag.*` file commands, check `workspace_context.mode`.");
        sb.AppendLine("- If `workspace_context.mode=ad_hoc` and project context exists, rerun with `--workspace-path`.");
        sb.AppendLine("- For fail-closed project semantics, set `--require-workspace true`.");
        sb.AppendLine("- Prefer `--input-stdin` for complex JSON payloads.");
        sb.AppendLine();
        sb.AppendLine("## Quick Recipes");
        sb.AppendLine("```text");
        sb.AppendLine("roscli nav.find_symbol src/MyProject/Program.cs Process --first-declaration true --brief true --max-results 20 --require-workspace true");
        sb.AppendLine("roscli nav.find_symbol_batch --queries @symbol-queries.json --brief true --first-declaration true --require-workspace true");
        sb.AppendLine("roscli ctx.member_source src/MyProject/Program.cs 42 17 body --brief true");
        sb.AppendLine("roscli edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true");
        sb.AppendLine("roscli diag.get_file_diagnostics src/MyProject/Program.cs --require-workspace true");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Command Catalog");
        sb.AppendLine("Format: `command-id` (`maturity`, `read|write`) - summary");
        sb.AppendLine();
        AppendLlmstxtCatalog(sb, visibleCommands, includeTraits: full);
        sb.AppendLine("## Complements");
        sb.AppendLine("- Default Rich Lander companion for .NET API/dependency intelligence: `dotnet-inspect <command>` (or `dnx dotnet-inspect -y -- <command>` if not installed).");
        sb.AppendLine("- `dotnet-skills` packages assistant skills around `dotnet-inspect`; it is not the primary inspection CLI.");
        sb.AppendLine("- Use `roscli` for in-repo semantic navigation, edits, diagnostics, and repair.");
        sb.AppendLine("- `roscli` is not a package index/version-diff tool; treat dotnet tools as complementary helpers.");

        return sb.ToString();
    }

    private static void AppendLlmstxtCatalog(StringBuilder sb, IReadOnlyList<CommandDescriptor> commands, bool includeTraits)
    {
        foreach (IGrouping<string, CommandDescriptor> group in commands
            .GroupBy(c => GetCommandCategory(c.Id))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### {group.Key}.*");
            foreach (CommandDescriptor command in group.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
            {
                string access = command.MutatesState ? "write" : "read";
                sb.Append($"- `{command.Id}` (`{command.Maturity}`, `{access}`) - {command.Summary}");
                if (includeTraits && command.Traits is { Count: > 0 })
                {
                    sb.Append($" | traits: {string.Join(", ", command.Traits)}");
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }
    }

    private static string GetCommandCategory(string commandId)
    {
        int separatorIndex = commandId.IndexOf('.');
        return separatorIndex <= 0 ? "misc" : commandId[..separatorIndex];
    }

    private static object BuildMaturityCounts(IReadOnlyList<CommandDescriptor> commands)
    {
        int stable = commands.Count(c => string.Equals(c.Maturity, CommandMaturity.Stable, StringComparison.OrdinalIgnoreCase));
        int advanced = commands.Count(c => string.Equals(c.Maturity, CommandMaturity.Advanced, StringComparison.OrdinalIgnoreCase));
        int experimental = commands.Count(c => string.Equals(c.Maturity, CommandMaturity.Experimental, StringComparison.OrdinalIgnoreCase));

        return new
        {
            stable,
            advanced,
            experimental,
            unknown = commands.Count - stable - advanced - experimental,
        };
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
                binary_launch_mode = IsPublishedModeEnabled("ROSCLI_USE_PUBLISHED")
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
              version
              list-commands [--compact] [--ids-only] [--stable-only]
              describe-command <command-id>
              quickstart
              llmstxt [--full]
              validate-input <command-id> [--input <json>|@<file>|-] [--input-stdin]
              run <command-id> [--input <json>|@<file>|-] [--input-stdin]
              <command-id> [simple positional args]

            Notes:
              - Use --version, -v, or version to print the installed roscli version.
              - Start with quickstart for an agent-ready pit-of-success workflow brief.
              - Use llmstxt for one-shot markdown bootstrap guidance (stable-first by default).
              - Recommended first minute:
                roscli llmstxt
                roscli list-commands --ids-only
                roscli quickstart
                roscli describe-command session.open
                roscli describe-command edit.create_file
              - You can run commands directly without 'run' for quick workflows.
              - Shorthand positional forms:
                ctx.file_outline <file-path>
                ctx.member_source <file-path> <line> <column> [member|body]
                diag.get_file_diagnostics <file-path>
                diag.get_solution_snapshot [directory-path]
                diag.get_workspace_snapshot [directory-path]
                repair.propose_from_diagnostics <file-path>
                nav.find_symbol <file-path> <symbol-name>
                nav.find_symbol_batch [queries-json-or-file]
                nav.find_invocations <file-path> <line> <column>
                nav.call_hierarchy <file-path> <line> <column>
                nav.call_path <source-file-path> <source-line> <source-column> <target-file-path> <target-line> <target-column>
                analyze.unused_private_symbols <workspace-path>
                analyze.control_flow_graph <file-path> <line> <column>
                analyze.dataflow_slice <file-path> <line> <column>
                analyze.dependency_violations <workspace-path> <layer1> <layer2> [layerN ...]
                analyze.impact_slice <file-path> <line> <column>
                analyze.override_coverage <workspace-path>
                analyze.async_risk_scan <workspace-path>
                ctx.search_text <pattern> [root-or-file]
                query.batch [queries-json-or-file]
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
                diag.get_file_diagnostics <file-path> --workspace-path src/MyProject/MyProject.csproj --require-workspace true
                ctx.search_text "RemoteUserAction" src --mode literal --max-results 100
                nav.find_symbol src/MyFile.cs Run --first-declaration true --brief true
                nav.find_symbol_batch --queries @symbol-queries.json --brief true --first-declaration true
                nav.find_invocations <file-path> <line> <column> --brief true --require-workspace true
                nav.call_hierarchy <file-path> <line> <column> --direction both --max-depth 2 --brief true
                nav.call_path <source-file> <source-line> <source-column> <target-file> <target-line> <target-column> --max-depth 8 --brief true
                analyze.unused_private_symbols src --brief true --max-symbols 100
                analyze.control_flow_graph <file-path> <line> <column> --brief true --max-blocks 120 --max-edges 260
                analyze.dataflow_slice <file-path> <line> <column> --brief true --max-symbols 120
                analyze.dependency_violations src MyApp.Web MyApp.Application MyApp.Domain --direction toward_end --brief true
                analyze.impact_slice <file-path> <line> <column> --brief true --include-callers true --include-callees true
                analyze.override_coverage src --coverage-threshold 0.6 --brief true
                analyze.async_risk_scan src --max-findings 200 --severity-filter warning --severity-filter info
                query.batch --queries @batch-queries.json --continue-on-error true
                edit.create_file src/NewType.cs --content "public class NewType { }" --overwrite false
                session.commit <session-id> --keep-session false --require-disk-unchanged true
              - For nav/diag file commands, check response workspace_context.mode.
                If mode=ad_hoc and project context exists, rerun with --workspace-path. For fail-closed behavior, add --require-workspace true.
              - list-commands supports compact response modes:
                list-commands --compact
                list-commands --ids-only
                list-commands --stable-only --ids-only
              - Command maturity: stable (default-safe), advanced (deeper/slower or partially heuristic), experimental (evolving contract).
              - Use session.apply_text_edits with --input/--input-stdin for structured span edits.
              - Use session.apply_and_commit with --input/--input-stdin for one-shot edit+commit.
              - Use describe-command <command-id> when an agent is unsure about command arguments.
              - Use --input with raw JSON, @path-to-json-file, or '-' for stdin.
              - Use --input-stdin to read full JSON from stdin without temp files.
              - Most commands output JSON envelopes. llmstxt outputs markdown text.
            """);
    }
}

