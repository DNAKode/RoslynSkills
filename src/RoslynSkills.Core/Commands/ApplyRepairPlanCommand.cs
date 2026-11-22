using RoslynSkills.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynSkills.Core.Commands;

public sealed class ApplyRepairPlanCommand : IAgentCommand
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly Dictionary<string, IAgentCommand> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edit.rename_symbol"] = new RenameSymbolCommand(),
        ["edit.change_signature"] = new ChangeSignatureCommand(),
        ["edit.add_member"] = new AddMemberCommand(),
        ["edit.replace_member_body"] = new ReplaceMemberBodyCommand(),
        ["edit.update_usings"] = new UpdateUsingsCommand(),
        ["edit.apply_code_fix"] = new ApplyCodeFixCommand(),
        ["edit.create_file"] = new CreateFileCommand(),
        ["edit.replace_text"] = new ReplaceTextCommand(),
        ["edit.replace_in_member"] = new ReplaceInMemberCommand(),
        ["edit.insert_text"] = new InsertTextCommand(),
        ["edit.transaction"] = new EditTransactionCommand(),
        ["edit.batch_exact"] = new BatchExactEditCommand(),
        ["diag.get_file_diagnostics"] = new GetFileDiagnosticsCommand(),
        ["diag.get_after_edit"] = new GetAfterEditDiagnosticsCommand(),
        ["diag.get_solution_snapshot"] = new GetSolutionSnapshotCommand(),
        ["diag.get_workspace_snapshot"] = new GetWorkspaceSnapshotCommand(),
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
        Summary: "Apply a sequence of repair operation steps on the hot workspace and return step-by-step outcomes.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "stop_on_error", errors);

        if (!TryGetStepsProperty(input, out JsonElement stepsProperty))
        {
            errors.Add(new CommandError("invalid_input", "Property 'steps' (or 'plan.steps') is required and must be an array."));
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

        bool stopOnError = GetStopOnError(input);
        string? defaultFilePath = GetDefaultFilePath(input);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool? requireWorkspace = GetOptionalBool(input, "require_workspace");

        TryGetStepsProperty(input, out JsonElement stepsProperty);
        List<PlanStepResult> stepResults = new();
        HashSet<string> modifiedFiles = new(PathComparer);
        JsonElement? lastDiagnosticsAfterEdit = null;

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

            JsonElement stepInput = BuildStepInput(
                step,
                operationId,
                defaultFilePath,
                workspacePath,
                workspaceHandle,
                requireWorkspace,
                modifiedFiles);

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

            if (execution.Ok && execution.Data is not null)
            {
                TrackStepOutcomes(execution.Data, modifiedFiles, ref lastDiagnosticsAfterEdit);
            }

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
            workspace_handle = workspaceHandle,
            workspace_path = workspacePath,
            stop_on_error = stopOnError,
            total_steps = stepResults.Count,
            succeeded_steps = successCount,
            failed_steps = failureCount,
            modified_files = modifiedFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            diagnostics_after_plan = lastDiagnosticsAfterEdit,
            step_results = stepResults,
        };

        return ok
            ? new CommandExecutionResult(data, Array.Empty<CommandError>())
            : new CommandExecutionResult(
                data,
                new[] { new CommandError("plan_failed", $"Repair plan completed with {failureCount} failed step(s).") });
    }

    private static bool TryGetStepsProperty(JsonElement input, out JsonElement stepsProperty)
    {
        if (input.TryGetProperty("steps", out stepsProperty) && stepsProperty.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (input.TryGetProperty("plan", out JsonElement planProperty) &&
            planProperty.ValueKind == JsonValueKind.Object &&
            planProperty.TryGetProperty("steps", out stepsProperty) &&
            stepsProperty.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        stepsProperty = default;
        return false;
    }

    private static bool GetStopOnError(JsonElement input)
    {
        if (input.TryGetProperty("stop_on_error", out JsonElement prop) &&
            (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
        {
            return prop.GetBoolean();
        }

        if (input.TryGetProperty("plan", out JsonElement planProp) &&
            planProp.ValueKind == JsonValueKind.Object &&
            planProp.TryGetProperty("stop_on_error", out JsonElement nestedProp) &&
            (nestedProp.ValueKind == JsonValueKind.True || nestedProp.ValueKind == JsonValueKind.False))
        {
            return nestedProp.GetBoolean();
        }

        return true;
    }

    private static string? GetDefaultFilePath(JsonElement input)
    {
        if (input.TryGetProperty("file_path", out JsonElement filePathProperty) && filePathProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = filePathProperty.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        if (input.TryGetProperty("plan", out JsonElement planProperty) &&
            planProperty.ValueKind == JsonValueKind.Object &&
            planProperty.TryGetProperty("file_path", out JsonElement nestedFilePath) &&
            nestedFilePath.ValueKind == JsonValueKind.String)
        {
            string? candidate = nestedFilePath.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool? GetOptionalBool(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static JsonElement BuildStepInput(
        JsonElement step,
        string operationId,
        string? defaultFilePath,
        string? workspacePath,
        string? workspaceHandle,
        bool? requireWorkspace,
        HashSet<string> modifiedFiles)
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

        if (!string.IsNullOrWhiteSpace(workspaceHandle) && !inputObject.ContainsKey("workspace_handle"))
        {
            inputObject["workspace_handle"] = workspaceHandle;
        }

        if (!string.IsNullOrWhiteSpace(workspacePath) && !inputObject.ContainsKey("workspace_path"))
        {
            inputObject["workspace_path"] = workspacePath;
        }

        if (requireWorkspace is not null && !inputObject.ContainsKey("require_workspace"))
        {
            inputObject["require_workspace"] = requireWorkspace.Value;
        }

        if (string.Equals(operationId, "edit.apply_code_fix", StringComparison.OrdinalIgnoreCase) &&
            !inputObject.ContainsKey("allow_line_drift") &&
            inputObject.TryGetPropertyValue("file_path", out JsonNode? filePathNode) &&
            filePathNode is JsonValue filePathValue &&
            filePathValue.TryGetValue<string>(out string? stepFilePath) &&
            !string.IsNullOrWhiteSpace(stepFilePath) &&
            modifiedFiles.Contains(Path.GetFullPath(stepFilePath)))
        {
            inputObject["allow_line_drift"] = true;
        }

        using JsonDocument doc = JsonDocument.Parse(inputObject.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static void TrackStepOutcomes(
        object stepData,
        HashSet<string> modifiedFiles,
        ref JsonElement? lastDiagnosticsAfterEdit)
    {
        JsonElement element = JsonSerializer.SerializeToElement(stepData);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("changed_files", out JsonElement changedFiles) &&
            changedFiles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in changedFiles.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("file_path", out JsonElement pathProp) &&
                    pathProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(pathProp.GetString()))
                {
                    modifiedFiles.Add(Path.GetFullPath(pathProp.GetString()!));
                }
            }
        }
        else if (element.TryGetProperty("wrote_file", out JsonElement wroteProp) &&
                 wroteProp.ValueKind == JsonValueKind.True &&
                 element.TryGetProperty("file_path", out JsonElement filePathProp) &&
                 filePathProp.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(filePathProp.GetString()))
        {
            modifiedFiles.Add(Path.GetFullPath(filePathProp.GetString()!));
        }

        if (element.TryGetProperty("diagnostics_after_edit", out JsonElement diagProp) &&
            diagProp.ValueKind == JsonValueKind.Object)
        {
            lastDiagnosticsAfterEdit = diagProp.Clone();
        }
    }

    private sealed record PlanStepResult(
        int step_index,
        string operation_id,
        bool ok,
        IReadOnlyList<CommandError> errors,
        object? data);
}
