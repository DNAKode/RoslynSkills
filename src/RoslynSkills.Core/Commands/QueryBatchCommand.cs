using RoslynSkills.Contracts;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class QueryBatchCommand : IAgentCommand
{
    private static readonly IReadOnlyDictionary<string, Func<IAgentCommand>> SupportedQueryFactories =
        new Dictionary<string, Func<IAgentCommand>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctx.search_text"] = static () => new SearchTextCommand(),
            ["ctx.file_outline"] = static () => new FileOutlineCommand(),
            ["ctx.member_source"] = static () => new MemberSourceCommand(),
            ["nav.find_symbol"] = static () => new FindSymbolCommand(),
            ["nav.find_references"] = static () => new FindReferencesCommand(),
            ["nav.find_invocations"] = static () => new FindInvocationsCommand(),
            ["nav.call_hierarchy"] = static () => new CallHierarchyCommand(),
            ["nav.call_path"] = static () => new CallPathCommand(),
            ["analyze.control_flow_graph"] = static () => new CfgCommand(),
            ["analyze.dataflow_slice"] = static () => new DataflowSliceCommand(),
            ["analyze.unused_private_symbols"] = static () => new UnusedPrivateSymbolsCommand(),
            ["analyze.dependency_violations"] = static () => new DependencyViolationsCommand(),
            ["analyze.impact_slice"] = static () => new ImpactSliceCommand(),
            ["analyze.override_coverage"] = static () => new OverrideCoverageCommand(),
            ["analyze.async_risk_scan"] = static () => new AsyncRiskScanCommand(),
            ["diag.get_file_diagnostics"] = static () => new GetFileDiagnosticsCommand(),
        };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "query.batch",
        Summary: "Execute multiple read-only Roslyn queries in one request with per-query results.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: [CommandTrait.BatchOrchestration, CommandTrait.PotentiallySlow]);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.ValidateOptionalBool(input, "continue_on_error", errors);

        if (!input.TryGetProperty("queries", out JsonElement queriesProperty) || queriesProperty.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'queries' is required and must be an array."));
            return errors;
        }

        int index = 0;
        foreach (JsonElement query in queriesProperty.EnumerateArray())
        {
            if (query.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new CommandError("invalid_input", $"Query at index {index} must be an object."));
                index++;
                continue;
            }

            if (!query.TryGetProperty("command_id", out JsonElement commandIdProperty) || commandIdProperty.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", $"Query at index {index} requires string property 'command_id'."));
                index++;
                continue;
            }

            string commandId = (commandIdProperty.GetString() ?? string.Empty).Trim();
            if (!SupportedQueryFactories.ContainsKey(commandId))
            {
                errors.Add(new CommandError(
                    "unsupported_command",
                    $"Query at index {index} uses unsupported command '{commandId}'. Supported commands: {string.Join(", ", SupportedQueryFactories.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}."));
                index++;
                continue;
            }

            if (!query.TryGetProperty("input", out JsonElement queryInput) || queryInput.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new CommandError("invalid_input", $"Query at index {index} requires object property 'input'."));
                index++;
                continue;
            }

            index++;
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        IReadOnlyList<CommandError> validationErrors = Validate(input);
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        bool continueOnError = InputParsing.GetOptionalBool(input, "continue_on_error", defaultValue: true);
        List<QueryBatchResultEntry> entries = new();
        int succeeded = 0;
        int failed = 0;
        bool stoppedEarly = false;

        int index = 0;
        foreach (JsonElement query in input.GetProperty("queries").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string commandId = query.GetProperty("command_id").GetString()!;
            JsonElement queryInput = query.GetProperty("input").Clone();
            IAgentCommand command = SupportedQueryFactories[commandId]();

            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyList<CommandError> queryValidationErrors = command.Validate(queryInput);
            if (queryValidationErrors.Count > 0)
            {
                stopwatch.Stop();
                entries.Add(new QueryBatchResultEntry(
                    index: index,
                    command_id: commandId,
                    ok: false,
                    elapsed_ms: stopwatch.ElapsedMilliseconds,
                    data: null,
                    errors: queryValidationErrors));
                failed++;

                if (!continueOnError)
                {
                    stoppedEarly = true;
                    break;
                }

                index++;
                continue;
            }

            CommandExecutionResult result = await command.ExecuteAsync(queryInput, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (result.Ok)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            entries.Add(new QueryBatchResultEntry(
                index: index,
                command_id: commandId,
                ok: result.Ok,
                elapsed_ms: stopwatch.ElapsedMilliseconds,
                data: result.Data,
                errors: result.Errors));

            if (!result.Ok && !continueOnError)
            {
                stoppedEarly = true;
                break;
            }

            index++;
        }

        object data = new
        {
            query = new
            {
                continue_on_error = continueOnError,
                requested_query_count = input.GetProperty("queries").GetArrayLength(),
                supported_commands = SupportedQueryFactories.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            },
            total_executed = entries.Count,
            succeeded,
            failed,
            stopped_early = stoppedEarly,
            results = entries,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private sealed record QueryBatchResultEntry(
        int index,
        string command_id,
        bool ok,
        long elapsed_ms,
        object? data,
        IReadOnlyList<CommandError> errors);
}
