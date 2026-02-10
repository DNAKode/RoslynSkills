using RoslynSkills.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynSkills.Core.Commands;

public sealed class ApplyRepairPlanCommand : IAgentCommand
{
    private static readonly Dictionary<string, IAgentCommand> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edit.rename_symbol"] = new RenameSymbolCommand(),
        ["edit.change_signature"] = new ChangeSignatureCommand(),
        ["edit.add_member"] = new AddMemberCommand(),
        ["edit.replace_member_body"] = new ReplaceMemberBodyCommand(),
        ["edit.update_usings"] = new UpdateUsingsCommand(),
        ["edit.apply_code_fix"] = new ApplyCodeFixCommand(),
        ["edit.transaction"] = new EditTransactionCommand(),
        ["diag.get_after_edit"] = new GetAfterEditDiagnosticsCommand(),
        ["diag.get_solution_snapshot"] = new GetSolutionSnapshotCommand(),
        ["diag.diff"] = new DiagnosticsDiffCommand(),
        ["session.open"] = new SessionOpenCommand(),
        ["session.set_content"] = new SessionSetContentCommand(),
        ["session.apply_text_edits"] = new SessionApplyTextEditsCommand(),
        ["session.get_diagnostics"] = new SessionGetDiagnosticsCommand(),
        ["session.status"] = new SessionStatusCommand(),
        ["session.diff"] = new SessionDiffCommand(),
        ["session.commit"] = new SessionCommitCommand(),
        ["session.close"] = new SessionCloseCommand(),
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "repair.apply_plan",
        Summary: "Apply a sequence of operation steps and return step-by-step outcomes.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!input.TryGetProperty("steps", out JsonElement stepsProperty) || stepsProperty.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'steps' is required and must be an array."));
            return errors;
        }

        int index = 0;
        foreach (JsonElement step in stepsProperty.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new CommandError("invalid_input", $"Step {index} must be an object."));
                index++;
                continue;
            }

            if (!step.TryGetProperty("operation_id", out JsonElement operationIdProperty) ||
                operationIdProperty.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(operationIdProperty.GetString()))
            {
                errors.Add(new CommandError("invalid_input", $"Step {index} requires non-empty string property 'operation_id'."));
            }

            index++;
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> validationErrors = Validate(input).ToList();
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        bool stopOnError = InputParsing.GetOptionalBool(input, "stop_on_error", defaultValue: true);

        string? defaultFilePath = null;
        if (input.TryGetProperty("file_path", out JsonElement filePathProperty) && filePathProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = filePathProperty.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                defaultFilePath = candidate;
            }
        }

        JsonElement stepsProperty = input.GetProperty("steps");
        List<PlanStepResult> stepResults = new();

        int stepIndex = 0;
        foreach (JsonElement step in stepsProperty.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string operationId = step.GetProperty("operation_id").GetString()!;

            if (!SupportedCommands.TryGetValue(operationId, out IAgentCommand? command))
            {
                PlanStepResult unsupportedResult = new(
                    step_index: stepIndex,
                    operation_id: operationId,
                    ok: false,
                    errors: new[] { new CommandError("unsupported_operation", $"Operation '{operationId}' is not supported by repair.apply_plan.") },
                    data: null);
                stepResults.Add(unsupportedResult);
                if (stopOnError)
                {
                    break;
                }

                stepIndex++;
                continue;
            }

            JsonElement stepInput = BuildStepInput(step, defaultFilePath);
            IReadOnlyList<CommandError> stepValidationErrors = command.Validate(stepInput);
            if (stepValidationErrors.Count > 0)
            {
                PlanStepResult invalidResult = new(
                    step_index: stepIndex,
                    operation_id: operationId,
                    ok: false,
                    errors: stepValidationErrors,
                    data: null);
                stepResults.Add(invalidResult);
                if (stopOnError)
                {
                    break;
                }

                stepIndex++;
                continue;
            }

            CommandExecutionResult execution = await command.ExecuteAsync(stepInput, cancellationToken).ConfigureAwait(false);
            stepResults.Add(new PlanStepResult(
                step_index: stepIndex,
                operation_id: operationId,
                ok: execution.Ok,
                errors: execution.Errors,
                data: execution.Data));

            if (!execution.Ok && stopOnError)
            {
                break;
            }

            stepIndex++;
        }

        int successCount = stepResults.Count(r => r.ok);
        int failureCount = stepResults.Count - successCount;
        bool ok = failureCount == 0;

        object data = new
        {
            stop_on_error = stopOnError,
            total_steps = stepResults.Count,
            succeeded_steps = successCount,
            failed_steps = failureCount,
            step_results = stepResults,
        };

        return ok
            ? new CommandExecutionResult(data, Array.Empty<CommandError>())
            : new CommandExecutionResult(
                data,
                new[] { new CommandError("plan_failed", $"Repair plan completed with {failureCount} failed step(s).") });
    }

    private static JsonElement BuildStepInput(JsonElement step, string? defaultFilePath)
    {
        JsonObject inputObject = new();
        if (step.TryGetProperty("input", out JsonElement inputProperty) && inputProperty.ValueKind == JsonValueKind.Object)
        {
            JsonObject? parsed = JsonNode.Parse(inputProperty.GetRawText()) as JsonObject;
            if (parsed is not null)
            {
                inputObject = parsed;
            }
        }

        if (!string.IsNullOrWhiteSpace(defaultFilePath) && !inputObject.ContainsKey("file_path"))
        {
            inputObject["file_path"] = defaultFilePath;
        }

        using JsonDocument doc = JsonDocument.Parse(inputObject.ToJsonString());
        return doc.RootElement.Clone();
    }

    private sealed record PlanStepResult(
        int step_index,
        string operation_id,
        bool ok,
        IReadOnlyList<CommandError> errors,
        object? data);
}

