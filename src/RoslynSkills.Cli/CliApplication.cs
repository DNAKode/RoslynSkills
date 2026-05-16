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

        bool noDaemon = StripFlag(args, "--no-daemon", out string[] effectiveArgs);
        args = effectiveArgs;

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
            "csharp-start" => await HandleCSharpStartAsync(remainder, stdout).ConfigureAwait(false),
            "llmstxt" => await HandleLlmstxtAsync(remainder, stdout).ConfigureAwait(false),
            "daemon.start" => await HandleDaemonStartAsync(remainder, stdout, cancellationToken).ConfigureAwait(false),
            "daemon.status" => await HandleDaemonStatusAsync(remainder, stdout, cancellationToken).ConfigureAwait(false),
            "daemon.stop" => await HandleDaemonStopAsync(remainder, stdout, cancellationToken).ConfigureAwait(false),
            "daemon.restart" => await HandleDaemonRestartAsync(remainder, stdout, cancellationToken).ConfigureAwait(false),
            "workspace.use" when noDaemon => await HandleRunDirectAsync(verb, remainder, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false),
            "workspace.use" => await HandleDaemonWorkspaceAsync(verb, remainder, stdout, cancellationToken).ConfigureAwait(false),
            "workspace.preload" when noDaemon => await HandleRunDirectAsync(verb, remainder, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false),
            "workspace.preload" => await HandleDaemonWorkspaceAsync(verb, remainder, stdout, cancellationToken).ConfigureAwait(false),
            "workspace.status" when noDaemon => await HandleRunDirectAsync(verb, remainder, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false),
            "workspace.status" => await HandleDaemonWorkspaceAsync(verb, remainder, stdout, cancellationToken).ConfigureAwait(false),
            "workspace.refresh" when noDaemon => await HandleRunDirectAsync(verb, remainder, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false),
            "workspace.refresh" => await HandleDaemonWorkspaceAsync(verb, remainder, stdout, cancellationToken).ConfigureAwait(false),
            "workspace.close" => await HandleDaemonWorkspaceAsync(verb, remainder, stdout, cancellationToken).ConfigureAwait(false),
            "workspace.list" => await HandleDaemonWorkspaceAsync(verb, remainder, stdout, cancellationToken).ConfigureAwait(false),
            "validate-input" => await HandleValidateInputAsync(remainder, stdout, cancellationToken, stdin).ConfigureAwait(false),
            "run" => await HandleRunAsync(remainder, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false),
            _ when _registry.TryGet(verb, out _) => await HandleRunDirectAsync(verb, remainder, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false),
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

        IReadOnlyList<CommandDescriptor> allCommands = ListDiscoverableCommands();
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
            CommandDescriptor? cliDescriptor = GetCliOnlyCommandDescriptor(commandId);
            if (cliDescriptor is null)
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
                        command = cliDescriptor,
                        usage = BuildCommandUsageHints(commandId),
                    },
                    Errors: Array.Empty<CommandError>(),
                    TraceId: null)).ConfigureAwait(false);
            return 0;
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
                        "For fresh C# sessions, run the csharp_fresh_session sequence below before reading or editing .cs files.",
                        "For a smaller copyable C# workflow, run: roscli csharp-start",
                        "Start with: roscli list-commands --ids-only",
                        "Use roscli list-commands --stable-only --ids-only for strict/default-safe command selection.",
                        "If arguments are unclear: roscli describe-command <command-id>",
                        "If you need agent-facing defaults quickly: copy the prompt block below.",
                        "Use nav.* / ctx.* / diag.* before text fallback.",
                        "Keep payloads brief-first (for example --brief true) before expanding detail.",
                        "For file diagnostics/symbol queries, confirm workspace_context.mode is 'workspace'.",
                        "Prefer --workspace-path <.sln|.slnx> for repo-wide or hot-workspace context; use .csproj/.vbproj only when intentionally project-scoped.",
                        "If workspace_context.mode is 'ad_hoc', rerun with --workspace-path <.sln|.slnx|.csproj|.vbproj|dir>.",
                        "For project-backed files, prefer --require-workspace true to fail closed instead of silently using ad_hoc.",
                        "Check workspace_context.resolved_workspace_path/workspace_kind/project_count to verify solution vs project binding.",
                        "Validate with diagnostics and build/tests before finalizing.",
                        "For whole-member/body edits, call ctx.member_source with include_edit_target_text=true and use edit_target.exact_span_text.text as the replace_span new_text base.",
                        "For small assertion/property edits inside one known member, prefer edit.replace_in_member after edit.claim.",
                        "Do not build replace_span new_text from line-oriented source.text unless you intentionally account for edit_target.trivia.preserved_line_prefix_text.",
                    },
                    first_minute_sequence = new[]
                    {
                        "roscli --version",
                        "roscli csharp-start --supervised",
                        "roscli csharp-start",
                        "roscli workspace.preload MySolution.slnx --alias default --require-solution true",
                        "roscli ctx.file_outline tests/MyTests.cs --member-name-contains Target --max-members 20",
                        "roscli ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text \"ExpectedLiteral\" --context-lines-before 3 --context-lines-after 8",
                        "roscli describe-command edit.replace_in_member",
                        "roscli list-commands --ids-only",
                        "roscli list-commands --stable-only --ids-only",
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
                            name = "csharp_fresh_session",
                            commands = new[]
                            {
                                "roscli --version",
                                "roscli csharp-start --supervised",
                                "roscli csharp-start",
                                "roscli workspace.preload MySolution.slnx --alias default --require-solution true",
                                "roscli ctx.file_outline tests/MyTests.cs --member-name-contains Target --max-members 20",
                                "roscli ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text \"ExpectedLiteral\" --context-lines-before 3 --context-lines-after 8",
                                "roscli edit.claim claim tests/MyTests.cs --reason narrow-csharp-slice",
                                "roscli edit.replace_in_member tests/MyTests.cs --member-name TargetTest --old-text \"Assert.Equal(1, value);\" --new-text \"Assert.Equal(2, value);\" --preview-chars 256",
                                "dotnet test tests/MyTests.csproj --no-restore --filter FullyQualifiedName~TargetTest",
                                "roscli edit.claim release <claim_id>",
                            },
                            rule = "For .cs orientation, prefer ctx.file_outline/ctx.member_source before git diff, rg, Get-Content, sed, cat, or patch-editor reads. If fallback is required, say which roscli command was insufficient.",
                        },
                        new
                        {
                            name = "rename_symbol_safely",
                            commands = new[]
                            {
                                "roscli nav.find_symbol src/MyProject/Program.cs Process --brief true --max-results 20 --workspace-path MySolution.slnx --require-workspace true",
                                "roscli edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true --workspace-path MySolution.slnx --require-workspace true",
                                "roscli diag.get_file_diagnostics src/MyProject/Program.cs --workspace-path MySolution.slnx --require-workspace true",
                            },
                        },
                        new
                        {
                            name = "span_member_edit_without_double_indent",
                            commands = new[]
                            {
                                "roscli ctx.member_source src/MyProject/Program.cs 42 17 member --include-edit-target-text true",
                                "roscli edit.claim claim src/MyProject/Program.cs --reason span-member-edit",
                                "roscli run edit.batch_exact --input-stdin",
                                "roscli diag.get_file_diagnostics src/MyProject/Program.cs --workspace-path MySolution.slnx --require-workspace true",
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
1) before reading or editing .cs files, run "roscli --version" and "roscli csharp-start".
2) preload the solution with "roscli workspace.preload <solution.sln|.slnx> --alias default --require-solution true".
3) orient with "roscli ctx.file_outline" and "roscli ctx.member_source"; avoid git diff/rg/Get-Content/sed/cat for .cs orientation unless roscli cannot answer.
4) if argument shape is unclear, run "roscli describe-command <command-id>".
5) claim before .cs mutation with "roscli edit.claim claim <file> --reason <reason>".
6) for small member-local edits, prefer "roscli edit.replace_in_member"; for large member edits, use ctx.member_source include_edit_target_text=true then edit.batch_exact replace_span from edit_target.exact_span_text.text.
7) run diagnostics/build/tests and release claims before finalizing.
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
                        "For repo-wide context and future hot workspace hosts, prefer explicit solution paths (.sln/.slnx) over loose project paths.",
                        "If workspace_context.mode is ad_hoc for a project file, pass --workspace-path explicitly.",
                        "If workspace_context.resolved_workspace_path is a .csproj when solution scope was expected, rerun with the .sln/.slnx path.",
                        "For project-backed files where ad_hoc is unacceptable, set --require-workspace true.",
                        "For complex JSON payloads, prefer --input-stdin over shell-escaped inline JSON.",
                        "Do not use git diff, rg, Get-Content, sed, cat, or patch-editor reads for .cs orientation until a roscli ctx/nav command has been tried.",
                        "If roscli cannot answer a C# query, state why before fallback.",
                        "For replace_span, do not duplicate edit_target.trivia.preserved_line_prefix_text in new_text.",
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

    private IReadOnlyList<CommandDescriptor> ListDiscoverableCommands()
        => _registry.ListCommands()
            .Concat(CliOnlyCommandDescriptors())
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<CommandDescriptor> CliOnlyCommandDescriptors()
        => new[]
        {
            new CommandDescriptor(
                Id: "csharp-start",
                Summary: "Emit the shortest C# agent workflow: workspace preload, ctx.member_source, claim-first edits, and multi-agent coordination.",
                InputSchemaVersion: "1.0",
                OutputSchemaVersion: "markdown",
                MutatesState: false),
            new CommandDescriptor(
                Id: "daemon.start",
                Summary: "Start or reuse the process-hot Roslyn workspace host daemon.",
                InputSchemaVersion: "1.0",
                OutputSchemaVersion: "1.0",
                MutatesState: true),
            new CommandDescriptor(
                Id: "daemon.stop",
                Summary: "Stop the process-hot Roslyn workspace host daemon.",
                InputSchemaVersion: "1.0",
                OutputSchemaVersion: "1.0",
                MutatesState: true),
            new CommandDescriptor(
                Id: "daemon.restart",
                Summary: "Restart the process-hot Roslyn workspace host daemon.",
                InputSchemaVersion: "1.0",
                OutputSchemaVersion: "1.0",
                MutatesState: true),
        };

    private static CommandDescriptor? GetCliOnlyCommandDescriptor(string commandId)
        => CliOnlyCommandDescriptors()
            .FirstOrDefault(c => string.Equals(c.Id, commandId, StringComparison.OrdinalIgnoreCase));

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

    private async Task<int> HandleCSharpStartAsync(string[] args, TextWriter stdout)
    {
        if (args.Any(a => IsHelp(a)))
        {
            await stdout.WriteLineAsync("Usage: roscli csharp-start [--supervised] [--solution <path.sln|path.slnx>]").ConfigureAwait(false);
            await stdout.WriteLineAsync("Emit the shortest C# agent workflow for semantic navigation and claim-first edits.").ConfigureAwait(false);
            await stdout.WriteLineAsync("Use --supervised for a two-turn operator protocol that verifies the command was actually run before assigning C# work.").ConfigureAwait(false);
            await stdout.WriteLineAsync("Use --solution to print concrete workspace.preload commands instead of placeholders.").ConfigureAwait(false);
            return 0;
        }

        bool supervised = HasOption(args, "--supervised");
        TryGetOption(args, "--solution", out string? solutionPath);
        await stdout.WriteAsync(BuildCSharpStartGuide(supervised, solutionPath)).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HandleDaemonStartAsync(
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        if (args.Any(a => IsHelp(a)))
        {
            await WriteEnvelopeAsync(stdout, new CommandEnvelope(
                Ok: true,
                CommandId: "daemon.start",
                Version: EnvelopeVersion,
                Data: new
                {
                    usage = "daemon.start [--repo-root <path>] [--host-path <RoslynSkills.WorkspaceHost.dll>]",
                    options = new[]
                    {
                        new { name = "--repo-root", summary = "Repo/workspace root used to derive the default daemon endpoint." },
                        new { name = "--host-path", summary = "Explicit RoslynSkills.WorkspaceHost.dll path." },
                    },
                },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
            return 0;
        }

        if (!TryGetDaemonOptions(args, allowHostPath: true, out string? repoRoot, out string? hostPath, out CommandEnvelope? error))
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        WorkspaceHostDaemonManager manager = new();
        CommandEnvelope envelope = await manager.StartAsync(repoRoot, hostPath, cancellationToken).ConfigureAwait(false);
        await WriteEnvelopeAsync(stdout, envelope).ConfigureAwait(false);
        return envelope.Ok ? 0 : 1;
    }

    private async Task<int> HandleDaemonStatusAsync(
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        if (args.Any(a => IsHelp(a)))
        {
            await WriteEnvelopeAsync(stdout, new CommandEnvelope(
                Ok: true,
                CommandId: "daemon.status",
                Version: EnvelopeVersion,
                Data: new
                {
                    usage = "daemon.status [--repo-root <path>]",
                    options = new[]
                    {
                        new { name = "--repo-root", summary = "Repo/workspace root used to derive the default daemon endpoint." },
                    },
                },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
            return 0;
        }

        if (!TryGetDaemonOptions(args, allowHostPath: false, out string? repoRoot, out _, out CommandEnvelope? error))
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        WorkspaceHostDaemonManager manager = new();
        CommandEnvelope envelope = await manager.StatusAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        await WriteEnvelopeAsync(stdout, envelope).ConfigureAwait(false);
        return envelope.Ok ? 0 : 1;
    }

    private async Task<int> HandleDaemonStopAsync(
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        if (args.Any(a => IsHelp(a)))
        {
            await WriteEnvelopeAsync(stdout, new CommandEnvelope(
                Ok: true,
                CommandId: "daemon.stop",
                Version: EnvelopeVersion,
                Data: new
                {
                    usage = "daemon.stop [--repo-root <path>]",
                    options = new[]
                    {
                        new { name = "--repo-root", summary = "Repo/workspace root used to derive the default daemon endpoint." },
                    },
                },
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
            return 0;
        }

        if (!TryGetDaemonOptions(args, allowHostPath: false, out string? repoRoot, out _, out CommandEnvelope? error))
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        WorkspaceHostDaemonManager manager = new();
        CommandEnvelope envelope = await manager.StopAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        await WriteEnvelopeAsync(stdout, envelope).ConfigureAwait(false);
        return envelope.Ok ? 0 : 1;
    }

    private async Task<int> HandleDaemonRestartAsync(
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        if (!TryGetDaemonOptions(args, allowHostPath: true, out string? repoRoot, out string? hostPath, out CommandEnvelope? error))
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        WorkspaceHostDaemonManager manager = new();
        CommandEnvelope stop = await manager.StopAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        CommandEnvelope start = await manager.StartAsync(repoRoot, hostPath, cancellationToken).ConfigureAwait(false);
        CommandEnvelope envelope = new(
            Ok: start.Ok,
            CommandId: "daemon.restart",
            Version: EnvelopeVersion,
            Data: new
            {
                stop = stop.Data,
                start = start.Data,
            },
            Errors: start.Errors,
            TraceId: null);
        await WriteEnvelopeAsync(stdout, envelope).ConfigureAwait(false);
        return envelope.Ok ? 0 : 1;
    }

    private async Task<int> HandleDaemonWorkspaceAsync(
        string verb,
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        if (args.Any(a => IsHelp(a)))
        {
            await WriteEnvelopeAsync(stdout, new CommandEnvelope(
                Ok: true,
                CommandId: verb,
                Version: EnvelopeVersion,
                Data: BuildWorkspaceDaemonUsage(verb),
                Errors: Array.Empty<CommandError>(),
                TraceId: null)).ConfigureAwait(false);
            return 0;
        }

        if (!TryBuildWorkspaceHostRequest(verb, args, out string? repoRoot, out WorkspaceHostRequest? request, out CommandEnvelope? error))
        {
            await WriteEnvelopeAsync(stdout, error!).ConfigureAwait(false);
            return 1;
        }

        WorkspaceHostDaemonManager manager = new();
        if (verb is "workspace.use" or "workspace.preload")
        {
            CommandEnvelope start = await manager.StartAsync(repoRoot, hostPath: null, cancellationToken).ConfigureAwait(false);
            if (!start.Ok)
            {
                await WriteEnvelopeAsync(stdout, start with { CommandId = verb }).ConfigureAwait(false);
                return 1;
            }
        }

        WorkspaceHostDaemonEndpoint endpoint = manager.GetDefaultEndpoint(repoRoot);
        request = ApplyPersistedWorkspaceAlias(verb, request!, endpoint.RepoRoot);
        TimeSpan requestTimeout = verb is "workspace.use" or "workspace.preload"
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(2);
        WorkspaceHostResponse? response = await manager.SendRequestAsync(
            repoRoot,
            request!,
            requestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            await WriteEnvelopeAsync(stdout, ErrorEnvelope(
                commandId: verb,
                code: WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                message: "Workspace daemon is unavailable. Run 'roscli daemon.start' or use workspace.use/preload to start it.")).ConfigureAwait(false);
            return 1;
        }

        UpdatePersistedWorkspaceAlias(verb, request!, response, manager, endpoint);
        IReadOnlyList<CommandError> errors = response.Errors ??
                                             response.Envelope?.Errors ??
                                             Array.Empty<CommandError>();
        await WriteEnvelopeAsync(stdout, new CommandEnvelope(
            Ok: response.Ok,
            CommandId: verb,
            Version: EnvelopeVersion,
            Data: response,
            Errors: errors,
            TraceId: null)).ConfigureAwait(false);
        return response.Ok ? 0 : 1;
    }

    private static WorkspaceHostRequest ApplyPersistedWorkspaceAlias(
        string verb,
        WorkspaceHostRequest request,
        string repoRoot)
    {
        if (verb is not ("workspace.status" or "workspace.refresh" or "workspace.close") ||
            !string.IsNullOrWhiteSpace(request.WorkspaceHandle) ||
            string.IsNullOrWhiteSpace(request.WorkspaceAlias))
        {
            return request;
        }

        WorkspaceAliasStore aliasStore = new(repoRoot);
        return aliasStore.TryGet(request.WorkspaceAlias, out WorkspaceAliasRecord? record) &&
               record is not null &&
               !string.IsNullOrWhiteSpace(record.WorkspaceHandle)
            ? request with { WorkspaceHandle = record.WorkspaceHandle }
            : request;
    }

    private static void UpdatePersistedWorkspaceAlias(
        string verb,
        WorkspaceHostRequest request,
        WorkspaceHostResponse response,
        WorkspaceHostDaemonManager manager,
        WorkspaceHostDaemonEndpoint endpoint)
    {
        if (!response.Ok || string.IsNullOrWhiteSpace(request.WorkspaceAlias))
        {
            return;
        }

        WorkspaceAliasStore aliasStore = new(endpoint.RepoRoot);
        if (verb is "workspace.close")
        {
            aliasStore.Remove(request.WorkspaceAlias);
            return;
        }

        if (verb is not ("workspace.use" or "workspace.preload" or "workspace.status" or "workspace.refresh"))
        {
            return;
        }

        string? workspaceHandle = response.Workspace?.WorkspaceHandle ?? TryGetStringProperty(response.Envelope?.Data, "workspace_handle");
        if (string.IsNullOrWhiteSpace(workspaceHandle))
        {
            return;
        }

        string? workspacePath =
            TryGetStringProperty(response.Envelope?.Data, "resolved_workspace_path") ??
            TryGetStringProperty(response.Envelope?.Data, "requested_workspace_path");
        string? fingerprint = response.Workspace?.WorkspaceFingerprint ?? TryGetStringProperty(response.Envelope?.Data, "workspace_fingerprint");
        WorkspaceAliasRecord record = new(
            WorkspacePath: workspacePath,
            WorkspaceHandle: workspaceHandle,
            DaemonEndpoint: WorkspaceHostDaemonManager.FormatEndpoint(endpoint),
            DaemonPid: manager.TryGetDaemonProcessId(endpoint.RepoRoot),
            WorkspaceFingerprint: fingerprint,
            LastSeenUtc: DateTimeOffset.UtcNow);
        aliasStore.Upsert(request.WorkspaceAlias, record);
    }

    private static string? TryGetStringProperty(object? data, string propertyName)
    {
        if (data is null)
        {
            return null;
        }

        JsonElement element = JsonSerializer.SerializeToElement(data);
        return TryGetString(element, propertyName, out string value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool IsDaemonCapableCommand(string commandId)
        => commandId is
            "workspace.status" or
            "workspace.refresh" or
            "workspace.close" or
            "nav.find_symbol" or
            "nav.find_symbol_batch" or
            "nav.find_references" or
            "nav.find_invocations" or
            "ctx.member_source" or
            "diag.get_file_diagnostics" or
            "query.batch" or
            "edit.replace_text" or
            "edit.replace_in_member" or
            "edit.insert_text" or
            "edit.batch_exact" or
            "edit.rename_symbol" or
            "edit.change_signature";

    private static string GetDaemonRoutingMode()
    {
        string? raw = Environment.GetEnvironmentVariable("ROSCLI_DAEMON");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "auto";
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "off" => "off",
            "0" => "off",
            "false" => "off",
            "required" => "required",
            "require" => "required",
            "on" => "required",
            "1" => "required",
            "true" => "required",
            "auto" => "auto",
            _ => "auto",
        };
    }

    private static bool StripFlag(string[] args, string flag, out string[] strippedArgs)
    {
        List<string>? stripped = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                stripped?.Add(args[i]);
                continue;
            }

            stripped ??= args.Take(i).ToList();
        }

        strippedArgs = stripped?.ToArray() ?? args;
        return stripped is not null;
    }

    private static bool TryResolveHotWorkspaceInput(
        JsonElement input,
        string repoRoot,
        bool requireHotWorkspace,
        out JsonElement routedInput,
        out bool resolvedHotWorkspace,
        out CommandEnvelope? error)
    {
        routedInput = input;
        resolvedHotWorkspace = false;
        error = null;
        if (TryGetString(input, "workspace_handle", out string existingHandle) &&
            !string.IsNullOrWhiteSpace(existingHandle))
        {
            resolvedHotWorkspace = true;
            return true;
        }

        string alias = Environment.GetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS") ?? "default";
        WorkspaceAliasStore aliasStore = new(repoRoot);
        if (!aliasStore.TryGet(alias, out WorkspaceAliasRecord? record) ||
            record is null ||
            string.IsNullOrWhiteSpace(record.WorkspaceHandle))
        {
            error = ErrorEnvelope(
                "cli.daemon_route",
                "hot_workspace_alias_not_found",
                $"Hot workspace alias '{alias}' was not found in '{aliasStore.Path}'. Run 'roscli workspace.use <solution> --alias {alias}' first.");
            return false;
        }

        Dictionary<string, object?> augmentedInput = new(StringComparer.Ordinal);
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in input.EnumerateObject())
            {
                augmentedInput[property.Name] = property.Value.Clone();
            }
        }

        augmentedInput["workspace_handle"] = record.WorkspaceHandle;
        resolvedHotWorkspace = true;
        if (requireHotWorkspace && !augmentedInput.ContainsKey("require_workspace"))
        {
            augmentedInput["require_workspace"] = true;
        }

        routedInput = JsonSerializer.SerializeToElement(augmentedInput);
        return true;
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
        TextReader stdin,
        bool noDaemon = false)
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

        int? daemonExitCode = await TryRunDaemonCapableCommandAsync(
            commandId,
            input,
            stdout,
            cancellationToken,
            noDaemon).ConfigureAwait(false);
        if (daemonExitCode.HasValue)
        {
            return daemonExitCode.Value;
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

    private async Task<int?> TryRunDaemonCapableCommandAsync(
        string commandId,
        JsonElement input,
        TextWriter stdout,
        CancellationToken cancellationToken,
        bool noDaemon)
    {
        if (noDaemon)
        {
            return null;
        }

        if (!IsDaemonCapableCommand(commandId))
        {
            return null;
        }

        string routingMode = GetDaemonRoutingMode();
        if (string.Equals(routingMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool requireHotWorkspace = string.Equals(routingMode, "required", StringComparison.OrdinalIgnoreCase) ||
                                   IsPublishedModeEnabled("ROSCLI_REQUIRE_HOT_WORKSPACE");
        WorkspaceHostDaemonManager manager = new();
        string? repoRoot = InferToolCallDaemonRepoRoot(input);
        WorkspaceHostDaemonEndpoint endpoint = manager.GetDefaultEndpoint(repoRoot);
        if (!TryResolveHotWorkspaceInput(
                input,
                endpoint.RepoRoot,
                requireHotWorkspace,
                out JsonElement routedInput,
                out bool resolvedHotWorkspace,
                out CommandEnvelope? aliasError))
        {
            if (requireHotWorkspace)
            {
                await WriteEnvelopeAsync(stdout, aliasError!).ConfigureAwait(false);
                return 1;
            }

            return null;
        }

        WorkspaceHostRequest request = new(
            Id: Guid.NewGuid().ToString("N"),
            Method: WorkspaceHostProtocol.Method.ToolCall,
            RefreshPolicy: Environment.GetEnvironmentVariable("ROSCLI_REFRESH_POLICY"),
            CommandId: commandId,
            Input: routedInput);
        WorkspaceHostResponse? response = await manager.SendRequestAsync(
            endpoint.RepoRoot,
            request,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            if (!requireHotWorkspace && !resolvedHotWorkspace)
            {
                return null;
            }

            await WriteEnvelopeAsync(stdout, ErrorEnvelope(
                commandId,
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                "Workspace daemon is unavailable for a required hot-workspace command. Run 'roscli workspace.use <solution>' first.")).ConfigureAwait(false);
            return 1;
        }

        IReadOnlyList<CommandError> errors = response.Errors ??
                                             response.Envelope?.Errors ??
                                             Array.Empty<CommandError>();
        if (!response.Ok &&
            !requireHotWorkspace &&
            HasErrorCode(errors, WorkspaceHostProtocol.ErrorCode.ProtocolMismatch))
        {
            return null;
        }

        await WriteEnvelopeAsync(stdout, new CommandEnvelope(
            Ok: response.Ok,
            CommandId: commandId,
            Version: EnvelopeVersion,
            Data: response,
            Errors: errors,
            TraceId: null)).ConfigureAwait(false);
        return response.Ok ? 0 : 1;
    }

    private static bool HasErrorCode(IEnumerable<CommandError> errors, string code)
        => errors.Any(error => string.Equals(error.Code, code, StringComparison.OrdinalIgnoreCase));

    private static string? InferToolCallDaemonRepoRoot(JsonElement input)
    {
        if (TryFindWorkspaceOrFilePath(input, out string targetPath))
        {
            return InferWorkspaceDaemonRepoRoot(explicitRepoRoot: null, targetPath);
        }

        return null;
    }

    private static bool TryFindWorkspaceOrFilePath(JsonElement element, out string targetPath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "workspace_path", out string workspacePath) &&
                !string.IsNullOrWhiteSpace(workspacePath))
            {
                targetPath = workspacePath;
                return true;
            }

            if (TryGetString(element, "file_path", out string filePath) &&
                !string.IsNullOrWhiteSpace(filePath))
            {
                targetPath = filePath;
                return true;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (TryFindWorkspaceOrFilePath(property.Value, out targetPath))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (TryFindWorkspaceOrFilePath(item, out targetPath))
                {
                    return true;
                }
            }
        }

        targetPath = string.Empty;
        return false;
    }

    private async Task<int> HandleRunDirectAsync(
        string commandId,
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken,
        TextReader stdin,
        bool noDaemon)
    {
        if (args.Length == 1 && IsHelp(args[0]))
        {
            return await HandleDescribeCommandAsync(new[] { commandId }, stdout).ConfigureAwait(false);
        }

        if (args.Length == 0)
        {
            return await HandleRunAsync(new[] { commandId }, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false);
        }

        if (HasOption(args, "--input-stdin") || TryGetOption(args, "--input", out _))
        {
            string[] runArgs = new[] { commandId }.Concat(args).ToArray();
            return await HandleRunAsync(runArgs, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false);
        }

        if (!SupportsDirectShorthand(commandId))
        {
            string[] runArgs = new[] { commandId }.Concat(args).ToArray();
            return await HandleRunAsync(runArgs, stdout, cancellationToken, stdin, noDaemon).ConfigureAwait(false);
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
            stdin,
            noDaemon).ConfigureAwait(false);
    }

    private async Task<int> HandleUnknownCommandAsync(string verb, TextWriter stdout, TextWriter stderr)
    {
        await WriteEnvelopeAsync(stdout, ErrorEnvelope(
            commandId: "cli",
            code: "unknown_verb",
            message: $"Unknown command '{verb}'. Use '--help', 'csharp-start', 'llmstxt', 'quickstart', or 'list-commands --ids-only' to view available commands.")).ConfigureAwait(false);
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

    private static bool TryGetDaemonOptions(
        string[] args,
        bool allowHostPath,
        out string? repoRoot,
        out string? hostPath,
        out CommandEnvelope? error)
    {
        repoRoot = null;
        hostPath = null;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? inlineValue = null;
            int equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex >= 0)
            {
                inlineValue = arg[(equalsIndex + 1)..];
                arg = arg[..equalsIndex];
            }

            if (string.Equals(arg, "--repo-root", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadCliOptionValue(args, ref i, inlineValue, out repoRoot))
                {
                    error = ErrorEnvelope("daemon", "invalid_args", "Option '--repo-root' requires a value.");
                    return false;
                }

                continue;
            }

            if (string.Equals(arg, "--host-path", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowHostPath)
                {
                    error = ErrorEnvelope("daemon", "invalid_args", "Option '--host-path' is only supported by daemon.start and daemon.restart.");
                    return false;
                }

                if (!TryReadCliOptionValue(args, ref i, inlineValue, out hostPath))
                {
                    error = ErrorEnvelope("daemon", "invalid_args", "Option '--host-path' requires a value.");
                    return false;
                }

                continue;
            }

            error = ErrorEnvelope("daemon", "invalid_args", $"Unknown daemon option '{arg}'.");
            return false;
        }

        return true;
    }

    private static bool TryBuildWorkspaceHostRequest(
        string verb,
        string[] args,
        out string? repoRoot,
        out WorkspaceHostRequest? request,
        out CommandEnvelope? error)
    {
        repoRoot = null;
        request = null;
        error = null;
        List<string> positional = new();
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            string? inlineValue = null;
            int equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex >= 0)
            {
                inlineValue = arg[(equalsIndex + 1)..];
                arg = arg[..equalsIndex];
            }

            if (!IsSupportedWorkspaceDaemonOption(verb, arg))
            {
                error = ErrorEnvelope(verb, "invalid_args", $"Unknown workspace option '{arg}'.");
                return false;
            }

            if (!TryReadCliOptionValue(args, ref i, inlineValue, out string? value))
            {
                error = ErrorEnvelope(verb, "invalid_args", $"Option '{arg}' requires a value.");
                return false;
            }

            options[arg] = value!;
        }

        options.TryGetValue("--repo-root", out repoRoot);
        string? alias = options.TryGetValue("--alias", out string? aliasValue)
            ? aliasValue
            : null;
        Dictionary<string, object?> input = new(StringComparer.Ordinal);
        string method;
        string? workspaceHandle = null;
        string? workspaceAlias = alias;

        switch (verb)
        {
            case "workspace.use":
                if (positional.Count != 1)
                {
                    error = ErrorEnvelope(verb, "invalid_args", "Usage: workspace.use <solution-or-project-path> [--alias default] [--require-solution true].");
                    return false;
                }

                method = WorkspaceHostProtocol.Method.WorkspacePreload;
                workspaceAlias ??= "default";
                string useWorkspacePath = NormalizeCliPathValue(positional[0]);
                repoRoot = InferWorkspaceDaemonRepoRoot(repoRoot, useWorkspacePath);
                input["workspace_path"] = useWorkspacePath;
                if (!TryGetBoolOption(options, "--require-solution", defaultValue: true, out bool useRequireSolution, out error, verb))
                {
                    return false;
                }

                input["require_solution"] = useRequireSolution;
                if (!TryAddWorkspacePreloadOptions(input, options, verb, out error))
                {
                    return false;
                }

                break;

            case "workspace.preload":
                if (positional.Count != 1)
                {
                    error = ErrorEnvelope(verb, "invalid_args", "Usage: workspace.preload <solution-or-project-path> [--alias name] [--require-solution true].");
                    return false;
                }

                method = WorkspaceHostProtocol.Method.WorkspacePreload;
                workspaceAlias ??= "default";
                string preloadWorkspacePath = NormalizeCliPathValue(positional[0]);
                repoRoot = InferWorkspaceDaemonRepoRoot(repoRoot, preloadWorkspacePath);
                input["workspace_path"] = preloadWorkspacePath;
                if (!TryGetBoolOption(options, "--require-solution", defaultValue: true, out bool preloadRequireSolution, out error, verb))
                {
                    return false;
                }

                input["require_solution"] = preloadRequireSolution;
                if (!TryAddWorkspacePreloadOptions(input, options, verb, out error))
                {
                    return false;
                }

                break;

            case "workspace.status":
                method = WorkspaceHostProtocol.Method.WorkspaceStatus;
                if (!TryGetWorkspaceTarget(positional, ref workspaceAlias, out workspaceHandle, out error, verb))
                {
                    return false;
                }

                AddWorkspaceTargetToInput(input, workspaceAlias, workspaceHandle);
                break;

            case "workspace.refresh":
                method = WorkspaceHostProtocol.Method.WorkspaceRefresh;
                if (!TryGetWorkspaceTarget(positional, ref workspaceAlias, out workspaceHandle, out error, verb))
                {
                    return false;
                }

                AddWorkspaceTargetToInput(input, workspaceAlias, workspaceHandle);
                if (options.TryGetValue("--mode", out string? refreshMode))
                {
                    input["mode"] = refreshMode;
                }

                break;

            case "workspace.close":
                method = WorkspaceHostProtocol.Method.WorkspaceClose;
                if (!TryGetWorkspaceTarget(positional, ref workspaceAlias, out workspaceHandle, out error, verb))
                {
                    return false;
                }

                AddWorkspaceTargetToInput(input, workspaceAlias, workspaceHandle);
                break;

            case "workspace.list":
                if (positional.Count != 0)
                {
                    error = ErrorEnvelope(verb, "invalid_args", "Usage: workspace.list [--repo-root <path>].");
                    return false;
                }

                method = WorkspaceHostProtocol.Method.WorkspaceList;
                break;

            default:
                error = ErrorEnvelope(verb, "invalid_args", $"Unsupported workspace verb '{verb}'.");
                return false;
        }

        JsonElement inputElement = JsonSerializer.SerializeToElement(input);
        request = new WorkspaceHostRequest(
            Id: Guid.NewGuid().ToString("N"),
            Method: method,
            WorkspaceAlias: workspaceAlias,
            WorkspaceHandle: workspaceHandle,
            RefreshPolicy: options.TryGetValue("--refresh-policy", out string? refreshPolicy) ? refreshPolicy : null,
            Input: inputElement);
        return true;
    }

    private static string? InferWorkspaceDaemonRepoRoot(string? explicitRepoRoot, string workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitRepoRoot))
        {
            return explicitRepoRoot;
        }

        string fullPath = Path.GetFullPath(workspacePath);
        string startDirectory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return startDirectory;
    }

    private static void AddWorkspaceTargetToInput(
        Dictionary<string, object?> input,
        string? workspaceAlias,
        string? workspaceHandle)
    {
        if (!string.IsNullOrWhiteSpace(workspaceHandle))
        {
            input["workspace_handle"] = workspaceHandle;
        }

        if (!string.IsNullOrWhiteSpace(workspaceAlias))
        {
            input["workspace_alias"] = workspaceAlias;
        }
    }

    private static bool IsSupportedWorkspaceDaemonOption(string verb, string option)
    {
        if (string.Equals(option, "--repo-root", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (verb is "workspace.use" or "workspace.preload")
        {
            return string.Equals(option, "--alias", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(option, "--require-solution", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(option, "--include-generated", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(option, "--max-files", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(option, "--mode", StringComparison.OrdinalIgnoreCase);
        }

        return verb is "workspace.status" or "workspace.refresh" or "workspace.close"
            ? string.Equals(option, "--alias", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(option, "--refresh-policy", StringComparison.OrdinalIgnoreCase) ||
              (string.Equals(verb, "workspace.refresh", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(option, "--mode", StringComparison.OrdinalIgnoreCase))
            : false;
    }

    private static bool TryGetWorkspaceTarget(
        List<string> positional,
        ref string? workspaceAlias,
        out string? workspaceHandle,
        out CommandEnvelope? error,
        string verb)
    {
        workspaceHandle = null;
        error = null;
        if (positional.Count > 1)
        {
            error = ErrorEnvelope(verb, "invalid_args", $"Usage: {verb} [alias|workspace-handle] [--alias name].");
            return false;
        }

        string? target = positional.Count == 1 ? positional[0] : null;
        if (string.IsNullOrWhiteSpace(target))
        {
            workspaceAlias ??= "default";
            return true;
        }

        if (target.StartsWith("ws_", StringComparison.Ordinal))
        {
            workspaceHandle = target;
        }
        else
        {
            workspaceAlias = target;
        }

        return true;
    }

    private static bool TryAddWorkspacePreloadOptions(
        Dictionary<string, object?> input,
        Dictionary<string, string> options,
        string verb,
        out CommandEnvelope? error)
    {
        error = null;
        if (options.TryGetValue("--mode", out string? mode))
        {
            input["mode"] = mode;
        }

        if (options.TryGetValue("--include-generated", out _))
        {
            if (!TryGetBoolOption(options, "--include-generated", defaultValue: false, out bool includeGenerated, out error, verb))
            {
                return false;
            }

            input["include_generated"] = includeGenerated;
        }

        if (options.TryGetValue("--max-files", out string? maxFiles))
        {
            if (!int.TryParse(maxFiles, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMaxFiles) ||
                parsedMaxFiles < 1)
            {
                error = ErrorEnvelope(verb, "invalid_args", "Option '--max-files' must be a positive integer.");
                return false;
            }

            input["max_files"] = parsedMaxFiles;
        }

        return true;
    }

    private static bool TryGetBoolOption(
        Dictionary<string, string> options,
        string key,
        bool defaultValue,
        out bool parsed,
        out CommandEnvelope? error,
        string verb)
    {
        error = null;
        if (!options.TryGetValue(key, out string? value))
        {
            parsed = defaultValue;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                parsed = true;
                return true;

            case "0":
            case "false":
            case "no":
            case "off":
                parsed = false;
                return true;

            default:
                parsed = defaultValue;
                error = ErrorEnvelope(verb, "invalid_args", $"Option '{key}' must be true or false.");
                return false;
        }
    }

    private static object BuildWorkspaceDaemonUsage(string verb)
        => verb switch
        {
            "workspace.use" => new
            {
                usage = "workspace.use <solution-or-project-path> [--alias default] [--require-solution true] [--repo-root <path>]",
                summary = "Start the daemon if needed, preload a full workspace, and bind an alias.",
            },
            "workspace.preload" => new
            {
                usage = "workspace.preload <solution-or-project-path> [--alias name] [--require-solution true] [--repo-root <path>]",
                summary = "Start the daemon if needed and preload a workspace.",
            },
            "workspace.status" => new { usage = "workspace.status [alias|workspace-handle] [--repo-root <path>]" },
            "workspace.refresh" => new { usage = "workspace.refresh [alias|workspace-handle] [--mode balanced|strict|reload|none] [--repo-root <path>]" },
            "workspace.close" => new { usage = "workspace.close [alias|workspace-handle] [--repo-root <path>]" },
            "workspace.list" => new { usage = "workspace.list [--repo-root <path>]" },
            _ => new { usage = $"{verb} [args]" },
        };

    private static bool TryReadCliOptionValue(
        string[] args,
        ref int index,
        string? inlineValue,
        out string? value)
    {
        if (!string.IsNullOrWhiteSpace(inlineValue))
        {
            value = inlineValue;
            return true;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        index++;
        value = args[index];
        return true;
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
            TryPromoteOptionToPositional(options, "text", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "query", ref positionalArgs, 0);
            TryPromoteOptionToPositional(options, "root", ref positionalArgs, 1);
            TryPromoteOptionToPositional(options, "file_path", ref positionalArgs, 1);
            TryPromoteOptionToPositional(options, "path", ref positionalArgs, 1);
        }

        if (string.Equals(commandId, "edit.claim", StringComparison.OrdinalIgnoreCase))
        {
            TryPromoteOptionToPositional(options, "operation", ref positionalArgs, 0);
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
                if (positionalArgs.Length == 1 && options.ContainsKey("member_name"))
                {
                    input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                    break;
                }

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

            case "edit.replace_text":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "edit.replace_text <file-path> --old-text <text> --new-text <text> [--apply true] [--replace-all true] [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "edit.replace_in_member":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "edit.replace_in_member <file-path> --member-name <name> --old-text <text> --new-text <text> [--mode member|body] [--apply true] [--replace-all true] [--preview-chars 256] [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "edit.insert_text":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "edit.insert_text <file-path> --anchor-text <text> --insert-text <text> [--position after|before] [--apply true] [--option value ...]"));
                    return false;
                }

                input["file_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "edit.claim":
                if (positionalArgs.Length < 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "edit.claim <status|list|claim|release> [path ...] [--owner name] [--reason text] [--ttl-minutes n] [--force true] [--option value ...]"));
                    return false;
                }

                input["operation"] = string.Equals(positionalArgs[0], "list", StringComparison.OrdinalIgnoreCase)
                    ? "status"
                    : positionalArgs[0];
                if (positionalArgs.Length > 1)
                {
                    string[] claimArgs = positionalArgs
                        .Skip(1)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .ToArray();
                    if (string.Equals(positionalArgs[0], "release", StringComparison.OrdinalIgnoreCase) &&
                        claimArgs.Length == 1 &&
                        claimArgs[0].StartsWith("claim_", StringComparison.Ordinal))
                    {
                        input["claim_id"] = claimArgs[0];
                    }
                    else
                    {
                        input["paths"] = claimArgs
                            .Select(NormalizeCliPathValue)
                            .ToArray();
                    }
                }
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

            case "workspace.preload":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, "workspace.preload <solution-or-project-path> [--require-solution true] [--option value ...]"));
                    return false;
                }

                input["workspace_path"] = NormalizeCliPathValue(positionalArgs[0]);
                break;

            case "workspace.status":
            case "workspace.close":
                if (positionalArgs.Length != 1 || string.IsNullOrWhiteSpace(positionalArgs[0]))
                {
                    error = ErrorEnvelope(
                        commandId: "cli",
                        code: "invalid_args",
                        message: BuildUsageMessage(commandId, $"{commandId} <workspace-handle> [--option value ...]"));
                    return false;
                }

                input["workspace_handle"] = positionalArgs[0];
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
            "edit.replace_text" => true,
            "edit.replace_in_member" => true,
            "edit.insert_text" => true,
            "edit.claim" => true,
            "session.open" => true,
            "session.get_diagnostics" => true,
            "session.status" => true,
            "session.diff" => true,
            "session.commit" => true,
            "session.close" => true,
            "workspace.preload" => true,
            "workspace.status" => true,
            "workspace.close" => true,
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
        if (TryGetObject(element, "envelope", out JsonElement hostEnvelope) &&
            (TryGetObject(hostEnvelope, "Data", out JsonElement nestedData) ||
             TryGetObject(hostEnvelope, "data", out nestedData)))
        {
            element = nestedData;
        }

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
                string focusSuffix = BuildMemberSourceFocusPreview(element);
                return lineCount > 0
                    ? $"{memberName}, lines={lineCount}{focusSuffix}"
                    : $"{memberName}{focusSuffix}";
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
                string guidanceSuffix = TryGetObject(element, "result_guidance", out JsonElement guidanceElement) &&
                                        guidanceElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                    ? ", guidance=narrow"
                    : string.Empty;
                return filesScanned >= 0
                    ? $"matches={totalMatches}, files={filesScanned}{guidanceSuffix}"
                    : $"matches={totalMatches}{guidanceSuffix}";
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

        if (string.Equals(commandId, "edit.replace_text", StringComparison.OrdinalIgnoreCase))
        {
            string file = TryGetString(element, "file_path", out string filePath)
                ? Path.GetFileName(filePath)
                : "<unknown>";
            int matchCount = TryGetInt(element, "match_count", out int matches) ? matches : -1;
            bool wrote = TryGetBool(element, "wrote_file", out bool wroteFile) && wroteFile;
            string action = wrote ? "written" : "dry-run";
            string claimSuffix = BuildClaimStatusSuffix(element, wrote);
            return matchCount >= 0
                ? $"{file}, matches={matchCount}, {action}{claimSuffix}"
                : $"{file}, {action}{claimSuffix}";
        }

        if (string.Equals(commandId, "edit.replace_in_member", StringComparison.OrdinalIgnoreCase))
        {
            string file = TryGetString(element, "file_path", out string filePath)
                ? Path.GetFileName(filePath)
                : "<unknown>";
            string member = TryGetObject(element, "member", out JsonElement memberObject) &&
                            TryGetString(memberObject, "member_name", out string memberName)
                ? memberName
                : "<member>";
            int matchCount = TryGetInt(element, "match_count", out int matches) ? matches : -1;
            bool wrote = TryGetBool(element, "wrote_file", out bool wroteFile) && wroteFile;
            string action = wrote ? "written" : "dry-run";
            string lineSuffix = TryGetFirstMatchLine(element, out int line)
                ? $", line={line}"
                : string.Empty;
            string claimSuffix = BuildClaimStatusSuffix(element, wrote);
            return matchCount >= 0
                ? $"{file}:{member}, matches={matchCount}{lineSuffix}, {action}{claimSuffix}"
                : $"{file}:{member}, {action}{claimSuffix}";
        }

        if (string.Equals(commandId, "edit.insert_text", StringComparison.OrdinalIgnoreCase))
        {
            string file = TryGetString(element, "file_path", out string filePath)
                ? Path.GetFileName(filePath)
                : "<unknown>";
            string position = TryGetString(element, "position", out string insertPosition)
                ? insertPosition
                : "after";
            int matchCount = TryGetInt(element, "match_count", out int matches) ? matches : -1;
            bool wrote = TryGetBool(element, "wrote_file", out bool wroteFile) && wroteFile;
            string action = wrote ? "written" : "dry-run";
            return matchCount >= 0
                ? $"{file}, {position}, matches={matchCount}, {action}"
                : $"{file}, {position}, {action}";
        }

        if (string.Equals(commandId, "edit.batch_exact", StringComparison.OrdinalIgnoreCase))
        {
            int total = TryGetInt(element, "total_operations", out int totalOperations) ? totalOperations : -1;
            int executed = TryGetInt(element, "executed_operations", out int executedOperations) ? executedOperations : -1;
            int succeeded = TryGetInt(element, "succeeded_operations", out int succeededOperations) ? succeededOperations : -1;
            int failed = TryGetInt(element, "failed_operations", out int failedOperations) ? failedOperations : -1;
            int wroteFiles = TryGetInt(element, "wrote_file_count", out int wroteFileCount) ? wroteFileCount : -1;
            bool skippedApply = TryGetBool(element, "skipped_apply_due_to_errors", out bool skipped) && skipped;
            if (total >= 0)
            {
                string summary = $"operations={Math.Max(executed, 0)}/{total}, ok={Math.Max(succeeded, 0)}, failed={Math.Max(failed, 0)}, wrote_files={Math.Max(wroteFiles, 0)}";
                return skippedApply ? $"{summary}, atomic-skip" : summary;
            }
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
            string analysisMode = TryGetObject(element, "analysis_scope", out JsonElement analysisScope) &&
                                  TryGetString(analysisScope, "analysis_mode", out string mode)
                ? mode
                : string.Empty;
            if (files >= 0 || diagnostics >= 0)
            {
                string summary = $"files={Math.Max(files, 0)}, diagnostics={Math.Max(diagnostics, 0)}, errors={Math.Max(errors, 0)}, warnings={Math.Max(warnings, 0)}";
                return string.IsNullOrWhiteSpace(analysisMode)
                    ? summary
                    : $"{summary}, analysis={analysisMode}";
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

    private static string BuildMemberSourceFocusPreview(JsonElement element)
    {
        if (!TryGetObject(element, "source", out JsonElement source) ||
            !TryGetObject(source, "focus", out JsonElement focus) ||
            !TryGetString(focus, "text", out string focusText) ||
            string.IsNullOrWhiteSpace(focusText))
        {
            return string.Empty;
        }

        string shortText = focusText.Length > 32 ? string.Concat(focusText.AsSpan(0, 29), "...") : focusText;
        if (TryGetBool(focus, "matched", out bool matched) && matched)
        {
            return TryGetInt(focus, "line", out int line) && line > 0
                ? $", focus=matched:{line}"
                : ", focus=matched";
        }

        return $", focus=not-found:{shortText}";
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

    private static bool TryGetFirstMatchLine(JsonElement element, out int line)
    {
        line = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("matches", out JsonElement matches) ||
            matches.ValueKind != JsonValueKind.Array ||
            matches.GetArrayLength() == 0)
        {
            return false;
        }

        JsonElement first = matches[0];
        return TryGetInt(first, "line", out line) && line > 0;
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

    private static string BuildClaimStatusSuffix(JsonElement element, bool wroteFile)
    {
        if (!wroteFile ||
            !TryGetObject(element, "claim_status", out JsonElement claimStatus) ||
            (TryGetBool(claimStatus, "claimed", out bool claimed) && claimed))
        {
            return string.Empty;
        }

        return ", unclaimed";
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
                    "For repo-wide or hot-workspace scope, prefer the .sln/.slnx path; use .csproj only for intentional project scope.",
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

        if (string.Equals(commandId, "edit.replace_text", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "edit.replace_text <file-path> --old-text <exact text> --new-text <replacement text> [--apply true] [--replace-all true]",
                run = "run edit.replace_text --input '{\"file_path\":\"src/MyFile.cs\",\"old_text\":\"Title = \\\"Help\\\"\",\"new_text\":\"Title = BuildHelpTitle(state.HelpOverlayScroll)\",\"apply\":true}'",
                required_properties = new[] { "file_path", "old_text", "new_text" },
                optional_properties = new[] { "apply", "replace_all", "include_diagnostics", "max_diagnostics", "workspace_path", "workspace_handle" },
                notes = new[]
                {
                    "Use after edit.claim for small exact snippet changes when edit.rename_symbol or edit.replace_member_body do not fit.",
                    "Defaults: apply=true, replace_all=false, include_diagnostics=true.",
                    "When a solution/project is preloaded, diagnostics_after_replace uses the hot workspace by default. Pass workspace_path/workspace_handle explicitly when needed.",
                    "When apply=true writes a tracked source file, hot_workspace_refresh reports whether preloaded workspaces were incrementally updated.",
                    "replace_all=false fails if old_text is ambiguous; make old_text more specific instead of falling back to patching.",
                    "For multiline old_text/new_text, prefer --input-stdin JSON to avoid shell quoting issues.",
                    "This is a structured roscli mutation bridge, not a semantic refactor. Prefer semantic edit commands when available.",
                },
            };
        }

        if (string.Equals(commandId, "edit.replace_in_member", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "edit.replace_in_member <file-path> --member-name <unique member name> --old-text <exact text> --new-text <replacement text> [--mode member|body] [--apply true] [--replace-all true] [--preview-chars 256]",
                run = "run edit.replace_in_member --input '{\"file_path\":\"tests/MyTests.cs\",\"member_name\":\"TargetTest\",\"old_text\":\"Assert.Equal(1, value);\",\"new_text\":\"Assert.Equal(2, value);\",\"apply\":true}'",
                required_properties = new[] { "file_path", "old_text", "new_text", "member_name or line+column" },
                optional_properties = new[] { "member_name", "line", "column", "mode", "apply", "replace_all", "preview_chars", "include_diagnostics", "max_diagnostics", "workspace_path", "workspace_handle" },
                notes = new[]
                {
                    "Use after edit.claim for small exact edits that should be scoped to a single member rather than the whole file.",
                    "Prefer member_name after ctx.file_outline/ctx.member_source identifies a unique member; use line+column only when names are ambiguous.",
                    "Defaults: mode=member, apply=true, replace_all=false, include_diagnostics=true, preview_chars=96.",
                    "Matching is confined to the selected member/body and tolerates LF snippets against CRLF files.",
                    "Successful responses include matches[] with line/column/offset/length/first_changed_offset/first_changed_line_delta/first_changed_column_delta/old_change_line/old_change_column/new_change_line/new_change_column/old_change_preview/new_change_preview/text_preview/new_text_preview; increase preview_chars when auditing long assertion insertions. If truncation is still required, previews bias toward the first changed character.",
                    "If old_text is missing or ambiguous inside the member, re-read with ctx.member_source --member-name <name> --focus-text <nearby text> before retrying.",
                    "For whole-member replacement, keep using ctx.member_source include_edit_target_text=true plus edit.batch_exact replace_span with expected_text.",
                },
            };
        }

        if (string.Equals(commandId, "edit.insert_text", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "edit.insert_text <file-path> --anchor-text <exact anchor> --insert-text <text to insert> [--position after|before] [--apply true]",
                run = "run edit.insert_text --input '{\"file_path\":\"src/MyFile.cs\",\"anchor_text\":\"[\\\"help_visible\\\"] = state.HelpVisible,\",\"insert_text\":\"\\n            [\\\"help_overlay_title\\\"] = BuildHelpTitle(...),\",\"position\":\"after\",\"apply\":true}'",
                required_properties = new[] { "file_path", "anchor_text", "insert_text" },
                optional_properties = new[] { "position", "apply", "include_diagnostics", "max_diagnostics", "workspace_path", "workspace_handle" },
                notes = new[]
                {
                    "Use after edit.claim for small insertions when you know an exact nearby anchor line/snippet.",
                    "Default position=after, apply=true, include_diagnostics=true.",
                    "When a solution/project is preloaded, diagnostics_after_insert uses the hot workspace by default. Pass workspace_path/workspace_handle explicitly when needed.",
                    "When apply=true writes a tracked source file, hot_workspace_refresh reports whether preloaded workspaces were incrementally updated.",
                    "Fails if anchor_text is missing or ambiguous; make anchor_text more specific instead of falling back to patching.",
                    "For multiline insert_text, prefer --input-stdin JSON to avoid shell quoting issues.",
                    "This command exists because agents often need to add one evidence field or assertion after a known line.",
                },
            };
        }

        if (string.Equals(commandId, "edit.batch_exact", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "run edit.batch_exact --input-stdin",
                run = "run edit.batch_exact --input '{\"file_path\":\"src/MyFile.cs\",\"operations\":[{\"kind\":\"replace_span\",\"span_start\":120,\"span_length\":18,\"new_text\":\"replacement\"},{\"kind\":\"insert_text\",\"anchor_text\":\"replacement\",\"insert_text\":\" suffix\",\"position\":\"after\"}],\"atomic\":true,\"apply\":true}'",
                required_properties = new[] { "operations" },
                optional_properties = new[] { "file_path", "apply", "atomic", "continue_on_error", "include_diagnostics", "max_diagnostics", "workspace_path", "workspace_handle" },
                notes = new[]
                {
                    "Use after edit.claim when you need multiple exact text/span edits and want one per-operation report.",
                    "Defaults: apply=true, atomic=true, continue_on_error=false, include_diagnostics=true.",
                    "Each operation may specify file_path, or use top-level file_path for all operations.",
                    "Operation kinds: replace_span uses span_start plus span_length or span_end, new_text, and optional expected_text; replace_text uses old_text/new_text/replace_all; insert_text uses anchor_text/insert_text/position.",
                    "Prefer replace_span with ctx.member_source Data.edit_target spans for large member replacements to avoid copying fragile multiline old_text.",
                    "When building replace_span new_text from ctx.member_source, start from edit_target.exact_span_text.text and do not duplicate edit_target.trivia.preserved_line_prefix_text.",
                    "Atomic apply means any operation failure prevents all file writes; response still reports the failed operation.",
                    "If old_text or anchor_text is ambiguous, inspect operation_results[].recovery_hint before retrying; it points to ctx.member_source/replace_span when span anchoring is safer.",
                    "When a preloaded hot workspace tracks changed files, file_results[].hot_workspace_refresh reports incremental updates.",
                    "Prefer this over chaining several edit.replace_text commands in one shell block.",
                },
            };
        }

        if (string.Equals(commandId, "edit.rename_symbol", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "edit.rename_symbol <file-path> <line> <column> <new-name> [--option value ...]",
                run = "run edit.rename_symbol --input '{\"file_path\":\"src/MyFile.cs\",\"line\":12,\"column\":15,\"new_name\":\"Updated\",\"apply\":true,\"workspace_path\":\"MySolution.slnx\",\"require_workspace\":true}'",
                required_properties = new[] { "file_path", "line", "column", "new_name" },
                optional_properties = new[] { "apply", "max_diagnostics", "workspace_path", "require_workspace" },
                notes = new[]
                {
                    "For repo-wide rename context, prefer workspace_path=.sln/.slnx; use .csproj only for intentional project scope.",
                    "Set require_workspace=true for project-backed files when ad_hoc fallback should fail closed.",
                },
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
                optional_properties = new[] { "brief", "max_results", "context_lines", "declarations_only", "first_declaration", "snippet_single_line", "max_snippet_chars", "workspace_path", "workspace_handle", "require_workspace" },
                notes = new[]
                {
                    "Use declarations_only=true when you only want declaration anchors.",
                    "Use first_declaration=true to prefer declaration match and fallback to first match when no declaration exists.",
                    "By default, roscli auto-resolves a workspace from the file path and prefers discovered .sln/.slnx before loose projects.",
                    "For repo-wide or hot-workspace scope, pass workspace_path=.sln/.slnx explicitly.",
                    "For repeated calls after workspace.preload, pass workspace_handle to reuse process-hot semantic state.",
                    "If workspace_context.mode is 'ad_hoc', pass workspace_path explicitly.",
                    "Check resolved_workspace_path/workspace_kind/project_count to verify solution vs project binding.",
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
                optional_properties = new[] { "continue_on_error", "brief", "max_results", "context_lines", "declarations_only", "first_declaration", "snippet_single_line", "max_snippet_chars", "workspace_path", "workspace_handle", "require_workspace" },
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
                optional_properties = new[] { "brief", "max_results", "context_lines", "include_object_creations", "workspace_path", "workspace_handle", "require_workspace" },
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

        if (string.Equals(commandId, "ctx.file_outline", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "ctx.file_outline <file-path> [--option value ...]",
                run = "run ctx.file_outline --input '{\"file_path\":\"tests/MyTests.cs\",\"member_name_contains\":\"EvidenceLedger\",\"include_members\":true,\"max_members\":40}'",
                required_properties = new[] { "file_path" },
                optional_properties = new[] { "include_usings", "include_members", "max_types", "max_members", "type_name_contains", "member_name_contains" },
                notes = new[]
                {
                    "For huge test files, pass member_name_contains to return only matching member outlines and their containing type.",
                    "Use ctx.member_source --member-name when the returned member name is unique; use returned line/column anchors only when names are ambiguous.",
                    "Keep max_members low during orientation; if the outline is still large, narrow member_name_contains before reading source.",
                },
            };
        }

        if (string.Equals(commandId, "ctx.search_text", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "ctx.search_text <pattern> [root-or-file] [--option value ...] OR ctx.search_text --file-path <file> --text <pattern>",
                run = "run ctx.search_text --input '{\"patterns\":[\"RemoteUserAction\",\"ReplicationUpdate\"],\"mode\":\"literal\",\"roots\":[\"src\"],\"max_results\":200}'",
                required_properties = new[] { "pattern|patterns", "file_path|roots|workspace_path" },
                optional_properties = new[] { "text", "query", "root", "path", "mode", "case_sensitive", "include_globs", "exclude_globs", "max_results", "max_files", "context_lines", "brief" },
                notes = new[]
                {
                    "Scope is mandatory: set file_path, roots, or workspace_path.",
                    "Direct CLI aliases: --text/--query map to pattern; --file-path/--path map to file scope; --root maps to root scope.",
                    "Use mode=regex for advanced matching; invalid regex patterns fail fast.",
                    "For orientation, start with --max-results 20 --context-lines 0. If matches are numerous, switch to ctx.file_outline or ctx.member_source focus_text instead of repeating broad searches.",
                },
            };
        }

        if (string.Equals(commandId, "ctx.member_source", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "ctx.member_source <file-path> <line> <column> [member|body] [--option value ...] OR ctx.member_source <file-path> --member-name <name> [--option value ...]",
                run = "run ctx.member_source --input '{\"file_path\":\"src/MyFile.cs\",\"member_name\":\"HandleInput\",\"mode\":\"member\",\"include_edit_target_text\":true,\"max_chars\":12000,\"workspace_handle\":\"ws_...\"}'",
                required_properties = new[] { "file_path plus line+column OR member_name" },
                optional_properties = new[] { "line", "column", "member_name", "mode", "brief", "include_source_text", "include_edit_target_text", "include_line_numbers", "include_trivia", "focus_text", "context_lines_before", "context_lines_after", "max_chars", "workspace_path", "workspace_handle", "require_workspace" },
                notes = new[]
                {
                    "Use line/column from ctx.file_outline or nav.find_symbol, or use member_name when the name is unique in the file.",
                    "If member_name is ambiguous, rerun with a line/column anchor from ctx.file_outline.",
                    "mode=member returns the whole declaration; mode=body returns only the body when available.",
                    "For huge members, pass focus_text with context_lines_before/context_lines_after to return a small window around the first literal match while edit_target still describes the anchored target.",
                    "When focus_text is supplied, include_edit_target_text defaults to false. Set it true only for whole-target replacement, and do not use exact_span_text when truncated=true.",
                    "For replace_span edits, use edit_target.exact_span_text.text as the replacement base and follow edit_target.trivia.new_text_first_line_rule to avoid double indentation.",
                    "When present, preserve edit_target.replace_span_operation.expected_text in the batch operation so concurrent edits fail closed instead of overwriting drifted spans.",
                    "If edit_target.trivia.prefix_edit_rule says the preserved line prefix is wrong, rerun with include_trivia=true before replacing indentation or attributes.",
                    "After workspace.preload, file/workspace-path commands infer the daemon root from file_path/workspace_path and can reuse alias=default across supervising cwd boundaries.",
                    "Pass workspace_handle explicitly when using a non-default alias or when the input has no file_path/workspace_path to route from.",
                    "Check query.workspace_context.workspace_cache_mode. process_hot means the daemon workspace was reused; process_balanced means a fresh CLI workspace was loaded.",
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
                optional_properties = new[] { "continue_on_error", "workspace_handle" },
                notes = new[]
                {
                    "query.batch supports read-only investigative commands only.",
                    "Each query item must provide command_id and input.",
                    "Top-level workspace_handle is applied to query inputs that do not specify their own handle.",
                    "For shorthand, pass --queries @file.json or positional file path containing a JSON array.",
                },
            };
        }

        if (string.Equals(commandId, "edit.claim", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "edit.claim <status|list|claim|release> [path ...] [--owner name] [--reason text] [--ttl-minutes n] [--force true]",
                run = "run edit.claim --input '{\"operation\":\"claim\",\"paths\":[\"src/MyFile.cs\"],\"owner\":\"agent-a\",\"reason\":\"implement focused change\",\"ttl_minutes\":90}'",
                required_properties = new[] { "operation" },
                optional_properties = new[] { "paths", "owner", "reason", "claim_id", "repo_root", "ttl_minutes", "force" },
                notes = new[]
                {
                    "Use before C# edits when multiple agents/subagents may touch the same repo.",
                    "claim creates .roslynskills/edit-claims.json; status/list shows active non-expired claims; release removes owned claims by path or claim_id.",
                    "Claims are advisory but machine-readable. Treat conflicts as stop-and-coordinate unless force=true is explicitly authorized.",
                    "Pair with workspace.preload for hot semantic reads, then use edit.transaction/session/apply_and_commit for the claimed files.",
                },
            };
        }

        if (string.Equals(commandId, "diag.get_file_diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "diag.get_file_diagnostics <file-path> [--workspace-path <path>] [--option value ...]",
                run = "run diag.get_file_diagnostics --input '{\"file_path\":\"src/MyFile.cs\",\"workspace_path\":\"MySolution.slnx\",\"require_workspace\":true}'",
                required_properties = new[] { "file_path" },
                optional_properties = new[] { "workspace_path", "workspace_handle", "require_workspace" },
                notes = new[]
                {
                    "By default, roscli auto-resolves a workspace from the file path and prefers discovered .sln/.slnx before loose projects.",
                    "For repo-wide or hot-workspace diagnostics, pass workspace_path=.sln/.slnx explicitly.",
                    "For repeated calls after workspace.preload, pass workspace_handle to reuse process-hot semantic state.",
                    "Response includes workspace_context.mode = workspace|ad_hoc.",
                    "Check resolved_workspace_path/workspace_kind/project_count to verify solution vs project binding.",
                    "Set require_workspace=true to fail closed when workspace resolution falls back to ad_hoc.",
                },
            };
        }

        if (string.Equals(commandId, "workspace.preload", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "workspace.preload <solution-or-project-path> [--alias default] [--require-solution true] [--option value ...]",
                run = "run workspace.preload --input '{\"workspace_path\":\"MySolution.slnx\",\"require_solution\":true,\"mode\":\"balanced\"}'",
                required_properties = new[] { "workspace_path" },
                optional_properties = new[] { "mode", "include_generated", "require_solution", "max_files", "alias" },
                notes = new[]
                {
                    "Prefer .sln/.slnx for hot workspace hosts.",
                    "When --repo-root is omitted, workspace.use/preload infer the daemon root from the target solution/project path, not the caller's current directory.",
                    "Set require_solution=true in benchmark/promotion runs to fail closed if a loose project is resolved.",
                    "Response includes workspace_handle for repeated semantic commands.",
                    "Direct workspace.preload now persists alias=default unless --alias is provided; later daemon-capable commands can auto-route to that hot workspace.",
                    "Daemon-capable commands with file_path/workspace_path infer the same repo root from that path, so cross-repo supervisors do not need to cd into the target repo.",
                },
            };
        }

        if (string.Equals(commandId, "daemon.start", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandId, "daemon.restart", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = $"{commandId} [--repo-root <path>] [--host-path <RoslynSkills.WorkspaceHost.dll>]",
                required_properties = Array.Empty<string>(),
                optional_properties = new[] { "repo_root", "host_path" },
                notes = new[]
                {
                    "Normally no --host-path is needed when roscli is installed from the packaged tool.",
                    "Use --host-path only when testing a locally built WorkspaceHost assembly.",
                },
            };
        }

        if (string.Equals(commandId, "daemon.stop", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = "daemon.stop [--repo-root <path>]",
                required_properties = Array.Empty<string>(),
                optional_properties = new[] { "repo_root" },
            };
        }

        if (string.Equals(commandId, "workspace.status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandId, "workspace.close", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                direct = $"{commandId} <workspace-handle>",
                run = $"run {commandId} --input '{{\"workspace_handle\":\"ws_...\"}}'",
                required_properties = new[] { "workspace_handle" },
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
            csharp_start = "Run 'csharp-start' before .cs reads/edits for the shortest C# semantic workflow.",
            first_steps = new[]
            {
                "csharp-start",
                "list-commands --ids-only",
                "describe-command session.open",
                "describe-command edit.create_file",
            },
            guardrails = new[]
            {
                "session.open supports only .cs/.csx files.",
                "Check workspace_context.mode on nav/diag file commands.",
                "Prefer --workspace-path <.sln|.slnx> for repo-wide or hot-workspace context; use .csproj/.vbproj only when intentionally project-scoped.",
                "Use --workspace-path when auto workspace resolution falls back to ad_hoc.",
                "Check resolved_workspace_path/workspace_kind/project_count when full solution context matters.",
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
        IReadOnlyList<CommandDescriptor> allCommands = ListDiscoverableCommands()
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
        sb.AppendLine("1. For C#/.NET repo work, run `roscli csharp-start` before `.cs` text reads or patch-editor edits.");
        sb.AppendLine("   For supervised fresh-agent trials, use `roscli csharp-start --supervised` and verify the transcript before assigning the slice.");
        sb.AppendLine("2. Pick a command from the catalog below and run it directly.");
        sb.AppendLine("3. Call `roscli describe-command <command-id>` only when argument shape is unclear.");
        sb.AppendLine("4. Use `diag.get_file_diagnostics` (or build/tests) before finalizing edits.");
        sb.AppendLine();
        sb.AppendLine("## Guardrails");
        sb.AppendLine("- `session.open` supports only `.cs/.csx` files.");
        sb.AppendLine("- For `nav.*` and `diag.*` file commands, check `workspace_context.mode`.");
        sb.AppendLine("- Prefer `--workspace-path <.sln|.slnx>` for repo-wide or hot-workspace context; use `.csproj/.vbproj` only when intentionally project-scoped.");
        sb.AppendLine("- If `workspace_context.mode=ad_hoc` and project context exists, rerun with `--workspace-path`.");
        sb.AppendLine("- Check `resolved_workspace_path`, `workspace_kind`, and `project_count` when full solution context matters.");
        sb.AppendLine("- For fail-closed project semantics, set `--require-workspace true`.");
        sb.AppendLine("- Prefer `--input-stdin` for complex JSON payloads.");
        sb.AppendLine();
        sb.AppendLine("## Quick Recipes");
        sb.AppendLine("```text");
        sb.AppendLine("roscli nav.find_symbol src/MyProject/Program.cs Process --first-declaration true --brief true --max-results 20 --workspace-path MySolution.slnx --require-workspace true");
        sb.AppendLine("roscli nav.find_symbol_batch --queries @symbol-queries.json --brief true --first-declaration true --workspace-path MySolution.slnx --require-workspace true");
        sb.AppendLine("roscli ctx.member_source src/MyProject/Program.cs 42 17 body --brief true");
        sb.AppendLine("roscli ctx.member_source src/MyProject/Program.cs 42 17 member --include-edit-target-text true --workspace-path MySolution.slnx --require-workspace true");
        sb.AppendLine("roscli ctx.member_source tests/MyTests.cs 1200 17 member --focus-text TargetCase --context-lines-before 3 --context-lines-after 8 --max-chars 12000");
        sb.AppendLine("roscli edit.replace_in_member tests/MyTests.cs --member-name TargetTest --old-text \"Assert.Equal(1, value);\" --new-text \"Assert.Equal(2, value);\" --preview-chars 256");
        sb.AppendLine("roscli run edit.batch_exact --input-stdin  # use kind=replace_span from edit_target.exact_span_text.text");
        sb.AppendLine("roscli edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true --workspace-path MySolution.slnx --require-workspace true");
        sb.AppendLine("roscli diag.get_file_diagnostics src/MyProject/Program.cs --workspace-path MySolution.slnx --require-workspace true");
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

    private static string BuildCSharpStartGuide(bool supervised, string? solutionPath)
    {
        string preloadTarget = string.IsNullOrWhiteSpace(solutionPath)
            ? "<solution.sln|.slnx>"
            : solutionPath.Trim();
        string examplePreloadTarget = string.IsNullOrWhiteSpace(solutionPath)
            ? "MySolution.slnx"
            : solutionPath.Trim();

        StringBuilder sb = new();
        sb.AppendLine("# roscli csharp-start");
        sb.AppendLine();
        sb.AppendLine("Use this before reading or editing `.cs` files in a C#/.NET repo.");
        if (supervised)
        {
            sb.AppendLine();
            sb.AppendLine("## Supervised Two-Turn Protocol");
            sb.AppendLine("Turn 1 prompt:");
            sb.AppendLine("```text");
            sb.AppendLine("Run exactly this command now, then stop and report the first two headings it prints: roscli csharp-start");
            sb.AppendLine("```");
            sb.AppendLine("Accept only evidence that the transcript contains `Ran roscli csharp-start` before any `.cs` `git diff`, `rg`, `Get-Content`, `sed`, `cat`, or patch-editor read.");
            sb.AppendLine();
            sb.AppendLine("Turn 2 prompt after the heading report:");
            sb.AppendLine("```text");
            sb.AppendLine($"Continue one narrow, testable C# slice. Use roscli for .cs context, edits, and post-edit anchors: edit.claim list, workspace.preload {preloadTarget} --alias default --require-solution true, compact ctx.file_outline filters, ctx.member_source with focus windows, describe-command before the first Roslyn edit command, then the edit command if mutation is needed. If a broad ctx.search_text returns many matches, stop broad searching and narrow with member_name_contains or member_source focus_text. Use ctx.search_text or ctx.member_source for .cs closeout line anchors; do not use rg/git diff/Get-Content on .cs files. Report any .cs fallback explicitly.");
            sb.AppendLine("```");
            sb.AppendLine("If the agent starts C# work before the command transcript appears, interrupt and rerun Turn 1; do not treat prose promises as compliance.");
        }

        sb.AppendLine();
        sb.AppendLine("## First Moves");
        sb.AppendLine("```text");
        sb.AppendLine("roscli --version");
        sb.AppendLine($"roscli workspace.preload {examplePreloadTarget} --alias default --require-solution true");
        sb.AppendLine("roscli ctx.file_outline tests/MyTests.cs --member-name-contains Target --max-members 20");
        sb.AppendLine("roscli ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text \"ExpectedLiteral\" --context-lines-before 3 --context-lines-after 8");
        sb.AppendLine("roscli describe-command edit.replace_in_member");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(solutionPath)
            ? "Replace `MySolution.slnx`, file paths, member names, and focus text with the current repo targets."
            : "Replace file paths, member names, and focus text with the current repo targets.");
        sb.AppendLine();
        sb.AppendLine("## Source Context");
        sb.AppendLine("- Use `ctx.file_outline --member-name-contains <term> --max-members 20` to find compact member anchors in large files.");
        sb.AppendLine("- Use `ctx.member_source --member-name <name>` when a member name is unique; this avoids stale line/column anchors.");
        sb.AppendLine("- Add `--focus-text <literal>` plus small context windows, usually 3-8 lines, for huge members instead of repeated broad search.");
        sb.AppendLine("- Keep `ctx.search_text` scoped and capped (`--max-results 20 --context-lines 0` first); if it returns many matches, switch to outline/member_source rather than searching again.");
        sb.AppendLine("- Add `--include-edit-target-text true` only when constructing a whole-member/body span replacement.");
        sb.AppendLine("- For whole-target span edits, build `new_text` from `Data.edit_target.exact_span_text.text`, not from line-oriented `source.text`.");
        sb.AppendLine();
        sb.AppendLine("## Editing");
        sb.AppendLine("```text");
        sb.AppendLine("roscli edit.claim list");
        sb.AppendLine("roscli edit.claim claim tests/MyTests.cs --owner agent-main --reason narrow-csharp-slice");
        sb.AppendLine("roscli edit.replace_in_member tests/MyTests.cs --member-name TargetTest --old-text \"Assert.Equal(1, value);\" --new-text \"Assert.Equal(2, value);\" --preview-chars 256");
        sb.AppendLine("dotnet test tests/MyTests.csproj --no-restore --filter FullyQualifiedName~TargetTest");
        sb.AppendLine("roscli edit.claim release <claim_id>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Use `edit.replace_in_member` for small exact changes inside one member. Use `edit.batch_exact` with `replace_span` and `expected_text` for coordinated whole-member or multi-file edits. Use `edit.insert_text` for exact-anchor insertions.");
        sb.AppendLine();
        sb.AppendLine("## Multi-Agent Coordination");
        sb.AppendLine("- Each agent/subagent checks `edit.claim list` before `.cs` mutation.");
        sb.AppendLine("- Claim every file to be changed before editing; use distinct stable `--owner` values such as `agent-ui-quake` or `agent-tests-quake`.");
        sb.AppendLine("- When spawning subagents, assign disjoint claimed files up front; if the file set is unknown, each subagent claims before its first edit and reports the claim id.");
        sb.AppendLine("- Do not force through another active claim unless a human/operator decided ownership changed.");
        sb.AppendLine("- Prefer disjoint file ownership for parallel agents; for shared files, serialize edits through one owner.");
        sb.AppendLine("- Use guarded edits (`edit.replace_in_member`, `edit.batch_exact expected_text`, or `session.commit --require-disk-unchanged true`) so stale context fails closed.");
        sb.AppendLine("- Release claims after focused tests or when abandoning the slice.");
        sb.AppendLine();
        sb.AppendLine("## Fallback Rule");
        sb.AppendLine("Do not start `.cs` orientation with `git diff`, `rg`, `Get-Content`, `sed`, `cat`, or patch-editor reads. Try `ctx.file_outline`, `ctx.member_source`, `ctx.search_text`, or `nav.*` first. If roscli cannot answer, state the attempted command and the missing capability before fallback.");
        sb.AppendLine("For post-edit `.cs` audit anchors and closeout line numbers, use `ctx.search_text` or `ctx.member_source`; do not fall back to `rg` just to find the line you changed.");
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
              csharp-start
              llmstxt [--full]
              daemon.start [--repo-root <path>] [--host-path <RoslynSkills.WorkspaceHost.dll>]
              daemon.status [--repo-root <path>]
              daemon.stop [--repo-root <path>]
              daemon.restart [--repo-root <path>] [--host-path <RoslynSkills.WorkspaceHost.dll>]
              workspace.use <solution-or-project-path> [--alias default] [--require-solution true] [--repo-root <path>]
              workspace.preload <solution-or-project-path> [--alias name] [--require-solution true] [--repo-root <path>]
              workspace.status [alias|workspace-handle] [--repo-root <path>]
              workspace.refresh [alias|workspace-handle] [--mode balanced|strict|reload|none] [--repo-root <path>]
              workspace.close [alias|workspace-handle] [--repo-root <path>]
              workspace.list [--repo-root <path>]
              validate-input <command-id> [--input <json>|@<file>|-] [--input-stdin]
              run <command-id> [--input <json>|@<file>|-] [--input-stdin]
              <command-id> [simple positional args]

            Notes:
              - Use --version, -v, or version to print the installed roscli version.
              - Start with quickstart for an agent-ready pit-of-success workflow brief.
              - Use csharp-start for the shortest C# workflow: preload, ctx.member_source, claim, edit, verify.
              - Use llmstxt for one-shot markdown bootstrap guidance (stable-first by default).
              - Use workspace.use <solution.slnx> to start the daemon, load a full solution, and bind the default alias.
              - workspace.use/preload infer daemon repo root from the target solution/project path unless --repo-root is provided.
              - Daemon-capable tool calls with file_path/workspace_path infer the same daemon repo root, so supervising from another cwd still finds the target hot workspace.
              - Daemon-capable semantic commands use ROSCLI_DAEMON=auto by default; pass --no-daemon or set ROSCLI_DAEMON=off to force the in-process path.
              - Set ROSCLI_DAEMON=required and ROSCLI_WORKSPACE_ALIAS=default to fail closed when a hot workspace is required.
              - Recommended first minute:
                roscli csharp-start
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
                edit.replace_text <file-path> --old-text <exact text> --new-text <replacement text>
                edit.replace_in_member <file-path> --member-name <name> --old-text <exact text> --new-text <replacement text> [--preview-chars 256]
                edit.insert_text <file-path> --anchor-text <exact anchor> --insert-text <text> [--position after|before]
                session.open <file-path> [session-id]
                session.get_diagnostics <session-id>
                session.status <session-id>
                session.diff <session-id>
                session.commit <session-id>
                session.close <session-id>
              - Direct shorthand also accepts command options:
                ctx.file_outline <file-path> --member-name-contains TestName --max-members 20
                diag.get_file_diagnostics <file-path> --workspace-path MySolution.slnx --require-workspace true
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
                edit.replace_text src/MyFile.cs --old-text "Title = \"Help\"" --new-text "Title = BuildHelpTitle(state.HelpOverlayScroll)"
                edit.replace_in_member tests/MyTests.cs --member-name TargetTest --old-text "Assert.Equal(1, value);" --new-text "Assert.Equal(2, value);" --preview-chars 256
                edit.insert_text src/MyFile.cs --anchor-text "[\"help_visible\"] = state.HelpVisible," --insert-text "`n            [\"help_overlay_title\"] = BuildHelpTitle(...)," --position after
                session.commit <session-id> --keep-session false --require-disk-unchanged true
              - For nav/diag file commands, check response workspace_context.mode.
                Prefer --workspace-path <.sln|.slnx> for repo-wide or hot-workspace context; use .csproj/.vbproj only when intentionally project-scoped.
                If mode=ad_hoc and project context exists, rerun with --workspace-path. For fail-closed behavior, add --require-workspace true.
                Check resolved_workspace_path/workspace_kind/project_count when full solution context matters.
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

