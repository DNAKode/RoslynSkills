using RoslynSkills.Contracts;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynSkills.Core.Commands;

public sealed class FindSymbolBatchCommand : IAgentCommand
{
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "queries",
        "continue_on_error",
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_symbol_batch",
        Summary: "Run many nav.find_symbol lookups in one call with shared defaults and per-query overrides.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: new[] { CommandTrait.BatchOrchestration, CommandTrait.PotentiallySlow });

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.ValidateOptionalBool(input, "continue_on_error", errors);

        if (!input.TryGetProperty("queries", out JsonElement queries) || queries.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'queries' is required and must be an array."));
            return errors;
        }

        FindSymbolCommand findSymbol = new();
        int index = 0;
        foreach (JsonElement query in queries.EnumerateArray())
        {
            if (query.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new CommandError("invalid_input", $"Query at index {index} must be an object."));
                index++;
                continue;
            }

            JsonElement mergedInput = BuildMergedQueryInput(input, query);
            IReadOnlyList<CommandError> queryErrors = findSymbol.Validate(mergedInput);
            foreach (CommandError queryError in queryErrors)
            {
                errors.Add(new CommandError(
                    queryError.Code,
                    $"Query at index {index}: {queryError.Message}",
                    queryError.Details));
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
        FindSymbolCommand findSymbol = new();
        List<BatchResultEntry> results = new();
        int succeeded = 0;
        int failed = 0;
        bool stoppedEarly = false;

        int index = 0;
        foreach (JsonElement query in input.GetProperty("queries").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            JsonElement mergedInput = BuildMergedQueryInput(input, query);
            string? filePath = TryGetOptionalString(mergedInput, "file_path");
            string? symbolName = TryGetOptionalString(mergedInput, "symbol_name");
            string? label = TryGetOptionalString(query, "label");

            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyList<CommandError> queryValidationErrors = findSymbol.Validate(mergedInput);
            if (queryValidationErrors.Count > 0)
            {
                stopwatch.Stop();
                results.Add(new BatchResultEntry(
                    index,
                    label,
                    filePath,
                    symbolName,
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

            CommandExecutionResult result = await findSymbol.ExecuteAsync(mergedInput, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (result.Ok)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            results.Add(new BatchResultEntry(
                index,
                label,
                filePath,
                symbolName,
                result.Ok,
                stopwatch.ElapsedMilliseconds,
                result.Data,
                result.Errors));

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
                defaults = BuildDefaultsPreview(input),
            },
            total_executed = results.Count,
            succeeded,
            failed,
            stopped_early = stoppedEarly,
            results,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static JsonElement BuildMergedQueryInput(JsonElement topLevelInput, JsonElement query)
    {
        JsonObject merged = new();

        foreach (JsonProperty property in topLevelInput.EnumerateObject())
        {
            if (ReservedKeys.Contains(property.Name))
            {
                continue;
            }

            merged[property.Name] = ToNode(property.Value);
        }

        foreach (JsonProperty property in query.EnumerateObject())
        {
            if (string.Equals(property.Name, "label", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            merged[property.Name] = ToNode(property.Value);
        }

        return JsonSerializer.SerializeToElement(merged);
    }

    private static object BuildDefaultsPreview(JsonElement topLevelInput)
    {
        Dictionary<string, object?> defaults = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in topLevelInput.EnumerateObject())
        {
            if (ReservedKeys.Contains(property.Name))
            {
                continue;
            }

            defaults[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        }

        return defaults;
    }

    private static string? TryGetOptionalString(JsonElement input, string propertyName)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static JsonNode? ToNode(JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText());
    }

    private sealed record BatchResultEntry(
        int index,
        string? label,
        string? file_path,
        string? symbol_name,
        bool ok,
        long elapsed_ms,
        object? data,
        IReadOnlyList<CommandError> errors);
}
