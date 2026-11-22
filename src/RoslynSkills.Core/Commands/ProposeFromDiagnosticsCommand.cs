using Microsoft.CodeAnalysis;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class ProposeFromDiagnosticsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "repair.propose_from_diagnostics",
        Summary: "Propose Roslyn-backed repair steps (AddImport, ImplementInterface, compiler fixes) from current diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
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
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);
        int maxProposals = InputParsing.GetOptionalInt(input, "max_proposals", defaultValue: 25, minValue: 1, maxValue: 500);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 200, minValue: 1, maxValue: 2_000);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(
                filePath,
                cancellationToken,
                workspacePath,
                workspaceHandle)
            .ConfigureAwait(false);
        CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, analysis);
        if (workspaceError is not null)
        {
            return workspaceError;
        }

        Diagnostic[] diagnostics = WorkspaceDiagnostics.GetDiagnosticsForCurrentFile(analysis, cancellationToken)
            .Where(d => !d.IsSuppressed && d.Severity != DiagnosticSeverity.Hidden)
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Character)
            .Take(maxDiagnostics)
            .ToArray();

        List<ProposalBucket> proposals = new();
        Dictionary<string, ProposalBucket> dedupeMap = new(StringComparer.OrdinalIgnoreCase);
        List<object> unfixableDiagnostics = new();

        if (diagnostics.Length > 0 && string.Equals(analysis.Language, LanguageNames.CSharp, StringComparison.Ordinal))
        {
            (Document preparedDocument, Diagnostic firstDiagnostic, IDisposable? ownedWorkspace) =
                await RoslynCodeFixService.PrepareDocumentAndDiagnosticAsync(analysis, diagnostics[0], cancellationToken)
                    .ConfigureAwait(false);

            using (ownedWorkspace)
            {
                SemanticModel? preparedModel = analysis.Document is null
                    ? await preparedDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
                    : null;
                IReadOnlyList<Diagnostic>? preparedDocDiagnostics = preparedModel?.GetDiagnostics(cancellationToken: cancellationToken);

                for (int i = 0; i < diagnostics.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Diagnostic diagnostic = i == 0
                        ? firstDiagnostic
                        : RebindDiagnostic(diagnostics[i], preparedDocDiagnostics);

                    FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                    int line = span.StartLinePosition.Line + 1;
                    int column = span.StartLinePosition.Character + 1;

                    IReadOnlyList<RoslynCodeFixCandidate> candidates = await RoslynCodeFixService.GetCodeFixesAsync(
                            preparedDocument,
                            diagnostic,
                            cancellationToken)
                        .ConfigureAwait(false);

                    RoslynCodeFixCandidate[] primaryFixes = candidates
                        .Where(c => c.Tier == CodeFixTier.Primary)
                        .ToArray();

                    if (primaryFixes.Length == 0)
                    {
                        unfixableDiagnostics.Add(new
                        {
                            diagnostic_id = diagnostic.Id,
                            severity = diagnostic.Severity.ToString(),
                            message = diagnostic.GetMessage(),
                            file_path = analysis.FilePath,
                            line,
                            column,
                            reason = "no_fix_available",
                        });
                        continue;
                    }

                    (RoslynCodeFixCandidate? selectedFix, bool ambiguous) = SelectProposalFix(primaryFixes);
                    string[] offeredTitles = candidates
                        .Where(c => c.Tier != CodeFixTier.ExplicitOnly)
                        .Select(c => c.Title)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    string dedupeKey = BuildDedupeKey(analysis.FilePath, diagnostic, selectedFix, line, column, offeredTitles);
                    if (dedupeMap.TryGetValue(dedupeKey, out ProposalBucket? existing))
                    {
                        existing.CoveredDiagnosticCount++;
                        existing.CoveredLines.Add(line);
                        continue;
                    }

                    if (proposals.Count >= maxProposals)
                    {
                        continue;
                    }

                    RepairPlanStep? step = selectedFix is not null && !ambiguous
                        ? new RepairPlanStep(
                            operation_id: "edit.apply_code_fix",
                            input: new
                            {
                                file_path = analysis.FilePath,
                                diagnostic_id = diagnostic.Id,
                                line,
                                column,
                                fix_title = selectedFix.Title,
                                apply = true,
                            })
                        : null;

                    ProposalBucket bucket = new(
                        DiagnosticId: diagnostic.Id,
                        Severity: diagnostic.Severity.ToString(),
                        Message: diagnostic.GetMessage(),
                        FilePath: analysis.FilePath,
                        Line: line,
                        Column: column,
                        FixTitle: selectedFix?.Title,
                        Provider: selectedFix?.ProviderName ?? primaryFixes[0].ProviderName,
                        OfferedFixes: offeredTitles,
                        Ambiguous: ambiguous,
                        Step: step);
                    bucket.CoveredLines.Add(line);

                    dedupeMap[dedupeKey] = bucket;
                    proposals.Add(bucket);
                }
            }
        }
        else
        {
            foreach (Diagnostic diagnostic in diagnostics)
            {
                FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                unfixableDiagnostics.Add(new
                {
                    diagnostic_id = diagnostic.Id,
                    severity = diagnostic.Severity.ToString(),
                    message = diagnostic.GetMessage(),
                    file_path = analysis.FilePath,
                    line = span.StartLinePosition.Line + 1,
                    column = span.StartLinePosition.Character + 1,
                    reason = "no_fix_available",
                });
            }
        }

        RepairPlanStep[] planSteps = proposals
            .Where(p => p.Step is not null)
            .Select(p => p.Step!)
            .ToArray();

        object[] proposalPayloads = proposals
            .Select(p => (object)new
            {
                diagnostic_id = p.DiagnosticId,
                severity = p.Severity,
                message = p.Message,
                file_path = p.FilePath,
                line = p.Line,
                column = p.Column,
                fix_title = p.FixTitle,
                provider = p.Provider,
                offered_fixes = p.OfferedFixes,
                ambiguous = p.Ambiguous,
                requires_selection = p.Ambiguous,
                covered_diagnostic_count = p.CoveredDiagnosticCount,
                covered_lines = p.CoveredLines.OrderBy(l => l).ToArray(),
                suggested_operation = "edit.apply_code_fix",
            })
            .ToArray();

        bool noFixAvailable = diagnostics.Length > 0 && proposals.Count == 0;

        object data = new
        {
            file_path = analysis.FilePath,
            workspace_path = workspacePath,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            diagnostic_count = diagnostics.Length,
            proposal_count = proposals.Count,
            unfixable_count = unfixableDiagnostics.Count,
            no_fix_available = noFixAvailable,
            proposals = proposalPayloads,
            unfixable_diagnostics = unfixableDiagnostics.ToArray(),
            plan = new
            {
                file_path = analysis.FilePath,
                steps = planSteps,
                stop_on_error = true,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static (RoslynCodeFixCandidate? Selected, bool Ambiguous) SelectProposalFix(
        RoslynCodeFixCandidate[] primaryFixes)
    {
        if (primaryFixes.Length == 1)
        {
            return (primaryFixes[0], false);
        }

        // ImplementInterface offers implicit ("Implement interface") and explicit ("Implement all members explicitly").
        // Propose the standard implicit Roslyn fix while exposing all offered titles in offered_fixes.
        if (primaryFixes.All(f => RoslynCodeFixService.IsImplementInterfaceProvider(f.ProviderName)))
        {
            RoslynCodeFixCandidate preferred = primaryFixes.FirstOrDefault(f =>
                string.Equals(f.Title, "Implement interface", StringComparison.OrdinalIgnoreCase))
                ?? primaryFixes[0];
            return (preferred, false);
        }

        return (null, true);
    }

    private static string BuildDedupeKey(
        string filePath,
        Diagnostic diagnostic,
        RoslynCodeFixCandidate? selectedFix,
        int line,
        int column,
        string[] offeredTitles)
    {
        if (selectedFix is not null)
        {
            // File-wide using imports collapse across all diagnostics in the file that need the same using.
            if (RoslynCodeFixService.IsAddImportProvider(selectedFix.ProviderName))
            {
                return $"{filePath}::AddImport::{selectedFix.Title}";
            }

            return $"{filePath}::{selectedFix.ProviderName}::{selectedFix.Title}::{line}:{column}";
        }

        return $"{filePath}::ambiguous::{diagnostic.Id}::{line}:{column}::{string.Join("|", offeredTitles)}";
    }

    private static Diagnostic RebindDiagnostic(Diagnostic diagnostic, IReadOnlyList<Diagnostic>? preparedDiagnostics)
    {
        if (preparedDiagnostics is null)
        {
            return diagnostic;
        }

        return preparedDiagnostics.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, diagnostic.Id, StringComparison.OrdinalIgnoreCase) &&
            candidate.Location.SourceSpan == diagnostic.Location.SourceSpan)
            ?? diagnostic;
    }

    private sealed class ProposalBucket
    {
        public string DiagnosticId { get; }
        public string Severity { get; }
        public string Message { get; }
        public string FilePath { get; }
        public int Line { get; }
        public int Column { get; }
        public string? FixTitle { get; }
        public string Provider { get; }
        public string[] OfferedFixes { get; }
        public bool Ambiguous { get; }
        public RepairPlanStep? Step { get; }
        public int CoveredDiagnosticCount { get; set; } = 1;
        public HashSet<int> CoveredLines { get; } = new();

        public ProposalBucket(
            string DiagnosticId,
            string Severity,
            string Message,
            string FilePath,
            int Line,
            int Column,
            string? FixTitle,
            string Provider,
            string[] OfferedFixes,
            bool Ambiguous,
            RepairPlanStep? Step)
        {
            this.DiagnosticId = DiagnosticId;
            this.Severity = Severity;
            this.Message = Message;
            this.FilePath = FilePath;
            this.Line = Line;
            this.Column = Column;
            this.FixTitle = FixTitle;
            this.Provider = Provider;
            this.OfferedFixes = OfferedFixes;
            this.Ambiguous = Ambiguous;
            this.Step = Step;
        }
    }

    private sealed record RepairPlanStep(
        string operation_id,
        object input);
}