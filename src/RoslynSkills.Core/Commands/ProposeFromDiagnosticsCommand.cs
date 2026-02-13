using Microsoft.CodeAnalysis;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class ProposeFromDiagnosticsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "repair.propose_from_diagnostics",
        Summary: "Propose a structured repair plan from current diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
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

        string filePath = input.GetProperty("file_path").GetString()!;
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);
        int maxProposals = InputParsing.GetOptionalInt(input, "max_proposals", defaultValue: 25, minValue: 1, maxValue: 500);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 200, minValue: 1, maxValue: 2_000);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath).ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, analysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

        IReadOnlyList<Diagnostic> diagnostics = WorkspaceDiagnostics.GetDiagnosticsForCurrentFile(analysis, cancellationToken);
        NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics).Take(maxDiagnostics).ToArray();

        List<RepairProposal> proposals = new();
        foreach (NormalizedDiagnostic diagnostic in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RepairProposal? proposal = CreateProposal(analysis.FilePath, diagnostic);
            if (proposal is null)
            {
                continue;
            }

            proposals.Add(proposal);
            if (proposals.Count >= maxProposals)
            {
                break;
            }
        }

        RepairPlanStep[] planSteps = proposals
            .Select(p => p.step)
            .ToArray();

        object data = new
        {
            file_path = analysis.FilePath,
            workspace_path = workspacePath,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            diagnostic_count = normalized.Length,
            proposal_count = proposals.Count,
            proposals = proposals.Select(p => p.proposal).ToArray(),
            plan = new
            {
                steps = planSteps,
                stop_on_error = true,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static RepairProposal? CreateProposal(string filePath, NormalizedDiagnostic diagnostic)
    {
        if (string.Equals(diagnostic.id, "CS8019", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(diagnostic.id, "IDE0005", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(diagnostic.id, "CS0105", StringComparison.OrdinalIgnoreCase))
        {
            return new RepairProposal(
                proposal: new
                {
                    diagnostic_id = diagnostic.id,
                    severity = diagnostic.severity,
                    message = diagnostic.message,
                    suggested_operation = "edit.apply_code_fix",
                    confidence = 0.95,
                    rationale = "Diagnostic maps to a supported deterministic using-directive code fix.",
                },
                step: new RepairPlanStep(
                    operation_id: "edit.apply_code_fix",
                    input: new
                    {
                        file_path = filePath,
                        diagnostic_id = diagnostic.id,
                        line = diagnostic.line,
                        apply = true,
                    }));
        }

        if (string.Equals(diagnostic.id, "CS0103", StringComparison.OrdinalIgnoreCase))
        {
            return new RepairProposal(
                proposal: new
                {
                    diagnostic_id = diagnostic.id,
                    severity = diagnostic.severity,
                    message = diagnostic.message,
                    suggested_operation = "edit.add_member",
                    confidence = 0.35,
                    rationale = "Potential missing member reference; add_member may resolve after human-guided input.",
                },
                step: new RepairPlanStep(
                    operation_id: "edit.add_member",
                    input: new
                    {
                        file_path = filePath,
                        member_declaration = "private void TODO_ResolveMissingSymbol() { }",
                        apply = false,
                    }));
        }

        return null;
    }

    private sealed record RepairProposal(
        object proposal,
        RepairPlanStep step);

    private sealed record RepairPlanStep(
        string operation_id,
        object input);
}