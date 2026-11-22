using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class ApplyCodeFixCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.apply_code_fix",
        Summary: "Discover and apply a Roslyn C# code fix (AddImport, ImplementInterface, compiler fixes) for a diagnostic on the hot workspace.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        string? diagnosticId = GetOptionalString(input, "diagnostic_id");
        int? line = GetOptionalPositiveInt(input, "line", errors);
        _ = GetOptionalPositiveInt(input, "column", errors);

        if (string.IsNullOrWhiteSpace(diagnosticId) && line is null)
        {
            errors.Add(new CommandError(
                "invalid_input",
                "Provide at least 'diagnostic_id' or 'line' to target a diagnostic."));
        }

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "apply", errors);
        InputParsing.ValidateOptionalBool(input, "allow_line_drift", errors);

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
        string? diagnosticId = GetOptionalString(input, "diagnostic_id");
        string? requestedFixTitle = GetOptionalString(input, "fix_title");
        int? requestedLine = GetOptionalPositiveInt(input, "line", errors: null);
        int? requestedColumn = GetOptionalPositiveInt(input, "column", errors: null);
        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        bool allowLineDrift = InputParsing.GetOptionalBool(input, "allow_line_drift", defaultValue: false);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

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

        if (!string.Equals(analysis.Language, LanguageNames.CSharp, StringComparison.Ordinal))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", $"Command '{Descriptor.Id}' currently supports C# files only.") });
        }

        if (requestedLine is not null && requestedLine.Value > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{requestedLine.Value}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        IReadOnlyList<Diagnostic> fileDiagnostics = WorkspaceDiagnostics.GetDiagnosticsForCurrentFile(analysis, cancellationToken)
            .Where(d => !d.IsSuppressed && d.Severity != DiagnosticSeverity.Hidden)
            .ToArray();

        List<Diagnostic> matchingDiagnostics = MatchDiagnostics(
            fileDiagnostics,
            analysis.FilePath,
            diagnosticId,
            requestedLine,
            requestedColumn);

        int? lineAdjustedFrom = null;
        if (matchingDiagnostics.Count == 0 &&
            allowLineDrift &&
            requestedLine is not null &&
            !string.IsNullOrWhiteSpace(diagnosticId) &&
            !string.IsNullOrWhiteSpace(requestedFixTitle))
        {
            List<Diagnostic> drifted = fileDiagnostics
                .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => Math.Abs((d.Location.GetLineSpan().StartLinePosition.Line + 1) - requestedLine.Value))
                .ToList();
            if (drifted.Count > 0)
            {
                matchingDiagnostics = drifted;
                lineAdjustedFrom = requestedLine;
            }
        }

        if (matchingDiagnostics.Count == 0)
        {
            string scopeDescription = BuildTargetDescription(diagnosticId, requestedLine, requestedColumn);
            object notFoundData = new
            {
                file_path = analysis.FilePath,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
                diagnostic_id = diagnosticId,
                matched_diagnostic_count = 0,
                offered_fixes = Array.Empty<object>(),
                apply_changes = apply,
                wrote_file = false,
                changed = false,
            };
            return new CommandExecutionResult(
                notFoundData,
                new[] { new CommandError("diagnostic_not_found", $"No matching diagnostic ({scopeDescription}) found in '{analysis.FilePath}'.") });
        }

        // Prepare one document (loaded Solution document or shared AdhocWorkspace document) for Roslyn CodeFixProviders.
        (Document preparedDocument, Diagnostic primaryDiagnostic, IDisposable? ownedWorkspace) =
            await RoslynCodeFixService.PrepareDocumentAndDiagnosticAsync(analysis, matchingDiagnostics[0], cancellationToken)
                .ConfigureAwait(false);

        using (ownedWorkspace)
        {
            IReadOnlyList<Diagnostic> effectiveMatchingDiagnostics = matchingDiagnostics.Count == 1
                ? new[] { primaryDiagnostic }
                : await RebindDiagnosticsForDocumentAsync(preparedDocument, matchingDiagnostics, cancellationToken)
                    .ConfigureAwait(false);

            // When line drift fallback matched multiple diagnostics in the file, inspect diagnostics in nearest-line order
            // until we find one offering the requested fix_title.
            List<RoslynCodeFixCandidate> offered = new();
            HashSet<(string Provider, string Title)> seenOfferKeys = new(ProviderTitleComparer.Instance);
            Diagnostic selectedTargetDiagnostic = effectiveMatchingDiagnostics[0];

            const int MaxDiagnosticsToInspect = 25;
            foreach (Diagnostic candidateDiag in effectiveMatchingDiagnostics.Take(MaxDiagnosticsToInspect))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<RoslynCodeFixCandidate> fixesForDiag = await RoslynCodeFixService.GetCodeFixesAsync(
                        preparedDocument,
                        candidateDiag,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (lineAdjustedFrom is not null && !string.IsNullOrWhiteSpace(requestedFixTitle))
                {
                    RoslynCodeFixCandidate? driftedMatch = FindRequestedFix(fixesForDiag, requestedFixTitle);
                    if (driftedMatch is null)
                    {
                        continue;
                    }

                    selectedTargetDiagnostic = candidateDiag;
                    offered.AddRange(fixesForDiag);
                    break;
                }

                foreach (RoslynCodeFixCandidate fix in fixesForDiag)
                {
                    if (seenOfferKeys.Add((fix.ProviderName, fix.Title)))
                    {
                        offered.Add(fix);
                    }
                }
            }

            if (lineAdjustedFrom is not null && offered.Count == 0)
            {
                string scopeDescription = BuildTargetDescription(diagnosticId, requestedLine, requestedColumn);
                return new CommandExecutionResult(
                    BuildBaseFailureData(analysis, workspacePath, requireWorkspace, selectedTargetDiagnostic, matchingDiagnostics.Count, offered, apply),
                    new[] { new CommandError("diagnostic_not_found", $"No matching diagnostic ({scopeDescription}) found for requested fix '{requestedFixTitle}'.") });
            }

            if (offered.Count == 0)
            {
                string reason = RoslynCodeFixService.IsDiagnosticBlocked(selectedTargetDiagnostic.Id)
                    ? $"Diagnostic '{selectedTargetDiagnostic.Id}' is intentionally not auto-fixed; no safe Roslyn code fix is available."
                    : $"No Roslyn code fix is available for diagnostic '{selectedTargetDiagnostic.Id}'.";
                return new CommandExecutionResult(
                    BuildBaseFailureData(analysis, workspacePath, requireWorkspace, selectedTargetDiagnostic, matchingDiagnostics.Count, offered, apply),
                    new[] { new CommandError("no_fix_available", reason) });
            }

            RoslynCodeFixCandidate? selectedFix;
            CommandError? selectionError = null;
            bool ambiguous = false;

            if (!string.IsNullOrWhiteSpace(requestedFixTitle))
            {
                selectedFix = FindRequestedFix(offered, requestedFixTitle);
                if (selectedFix is null)
                {
                    string titles = string.Join(", ", offered.Select(f => $"'{f.Title}'"));
                    return new CommandExecutionResult(
                        BuildBaseFailureData(analysis, workspacePath, requireWorkspace, selectedTargetDiagnostic, matchingDiagnostics.Count, offered, apply),
                        new[]
                        {
                            new CommandError(
                                "fix_not_found",
                                $"Requested fix_title '{requestedFixTitle}' was not offered for diagnostic '{selectedTargetDiagnostic.Id}'. Offered fixes: {titles}."),
                        });
                }
            }
            else
            {
                RoslynCodeFixCandidate[] primaryFixes = offered
                    .Where(f => f.Tier == CodeFixTier.Primary)
                    .ToArray();

                if (primaryFixes.Length == 1)
                {
                    selectedFix = primaryFixes[0];
                }
                else if (primaryFixes.Length > 1)
                {
                    selectedFix = null;
                    ambiguous = true;
                    string titles = string.Join(", ", primaryFixes.Select(f => $"'{f.Title}'"));
                    selectionError = new CommandError(
                        "ambiguous_code_fix",
                        $"Multiple Roslyn code fixes are available for '{selectedTargetDiagnostic.Id}' ({titles}). Specify 'fix_title' (or narrow with 'line'/'column') to choose one.");
                }
                else
                {
                    // Only secondary or explicit-only speculative fixes exist; never auto-apply speculative stubs.
                    selectedFix = null;
                    string titles = string.Join(", ", offered.Select(f => $"'{f.Title}'"));
                    selectionError = new CommandError(
                        "no_fix_available",
                        $"No automatic Roslyn code fix is available for '{selectedTargetDiagnostic.Id}'. Explicit-only alternatives offered: {titles}.");
                }
            }

            if (selectedFix is null)
            {
                if (!apply)
                {
                    object previewData = new
                    {
                        file_path = analysis.FilePath,
                        workspace_path = workspacePath,
                        require_workspace = requireWorkspace,
                        workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
                        diagnostic_id = selectedTargetDiagnostic.Id,
                        target_diagnostic = FormatDiagnostic(selectedTargetDiagnostic),
                        matched_diagnostic_count = matchingDiagnostics.Count,
                        offered_fixes = offered.Select(BuildOfferedFixPayload).ToArray(),
                        selected_fix = (object?)null,
                        ambiguous,
                        apply_changes = false,
                        changed = false,
                        wrote_file = false,
                        changed_file_count = 0,
                        wrote_file_count = 0,
                        changed_files = Array.Empty<object>(),
                    };
                    return new CommandExecutionResult(previewData, Array.Empty<CommandError>());
                }

                return new CommandExecutionResult(
                    BuildBaseFailureData(analysis, workspacePath, requireWorkspace, selectedTargetDiagnostic, matchingDiagnostics.Count, offered, apply, ambiguous),
                    new[] { selectionError! });
            }

            Solution initialSolution = preparedDocument.Project.Solution;
            RoslynCodeFixApplyOutcome outcome = await RoslynCodeFixService.ApplyFixAsync(
                    selectedFix,
                    initialSolution,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.Ok || outcome.EditPlan is null)
            {
                return new CommandExecutionResult(
                    BuildBaseFailureData(analysis, workspacePath, requireWorkspace, selectedTargetDiagnostic, matchingDiagnostics.Count, offered, apply),
                    new[] { outcome.Error ?? new CommandError("code_fix_failed", "Code fix application failed.") });
            }

            SolutionEditPlan editPlan = outcome.EditPlan;
            bool changed = editPlan.ChangedFiles.Count > 0 || outcome.AddedFiles.Count > 0;
            List<string> writtenPaths = new();

            if (apply && changed)
            {
                foreach (ChangedSolutionFile changedFile in editPlan.ChangedFiles)
                {
                    await EditFileText.WriteAsync(
                            changedFile.FilePath,
                            changedFile.UpdatedText,
                            changedFile.OriginalSnapshot.Encoding,
                            cancellationToken)
                        .ConfigureAwait(false);
                    writtenPaths.Add(changedFile.FilePath);
                }

                foreach (AddedSolutionFile addedFile in outcome.AddedFiles)
                {
                    string? parentDir = Path.GetDirectoryName(addedFile.FilePath);
                    if (!string.IsNullOrWhiteSpace(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    await EditFileText.WriteAsync(
                            addedFile.FilePath,
                            addedFile.Text,
                            EditFileText.Utf8NoBom,
                            cancellationToken)
                        .ConfigureAwait(false);
                    writtenPaths.Add(addedFile.FilePath);
                }
            }

            object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(
                    writtenPaths,
                    wroteFile: writtenPaths.Count > 0,
                    allowAddDocuments: outcome.AddedFiles.Count > 0,
                    cancellationToken)
                .ConfigureAwait(false);

            List<DocumentId> diagnosticDocumentIds = editPlan.AffectedDocumentIds.ToList();
            if (!diagnosticDocumentIds.Contains(preparedDocument.Id))
            {
                diagnosticDocumentIds.Add(preparedDocument.Id);
            }

            foreach (AddedSolutionFile addedFile in outcome.AddedFiles)
            {
                if (!diagnosticDocumentIds.Contains(addedFile.DocumentId))
                {
                    diagnosticDocumentIds.Add(addedFile.DocumentId);
                }
            }

            IReadOnlyList<Diagnostic> afterDiagnostics = await WorkspaceDiagnostics.GetDiagnosticsForUpdatedSolutionAsync(
                    editPlan.FinalSolution,
                    diagnosticDocumentIds,
                    cancellationToken)
                .ConfigureAwait(false);
            NormalizedDiagnostic[] normalizedAfter = CompilationDiagnostics.Normalize(afterDiagnostics)
                .Take(maxDiagnostics)
                .ToArray();

            object data = new
            {
                file_path = analysis.FilePath,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
                diagnostic_id = selectedFix.Diagnostic.Id,
                target_diagnostic = FormatDiagnostic(selectedFix.Diagnostic),
                line_adjusted_from = lineAdjustedFrom,
                matched_diagnostic_count = matchingDiagnostics.Count,
                offered_fixes = offered.Select(BuildOfferedFixPayload).ToArray(),
                selected_fix = new
                {
                    title = selectedFix.Title,
                    provider = selectedFix.ProviderName,
                    equivalence_key = selectedFix.EquivalenceKey,
                },
                apply_changes = apply,
                changed,
                wrote_file = writtenPaths.Count > 0,
                changed_file_count = editPlan.ChangedFiles.Count + outcome.AddedFiles.Count,
                wrote_file_count = writtenPaths.Count,
                changed_files = editPlan.ChangedFiles.Select(cf => new
                {
                    file_path = cf.FilePath,
                    replacement_count = cf.ReplacementCount,
                    changed_lines = cf.ChangedLines,
                    encoding = cf.OriginalSnapshot.Encoding.WebName,
                    has_bom = cf.OriginalSnapshot.Encoding.GetPreamble().Length > 0,
                    line_ending = cf.OriginalSnapshot.LineEnding == "\r\n" ? "crlf" : "lf",
                }).ToArray(),
                added_files = outcome.AddedFiles.Select(af => new
                {
                    file_path = af.FilePath,
                }).ToArray(),
                claim_status = EditClaimAwareness.BuildForFile(analysis.FilePath, writtenPaths.Count > 0),
                hot_workspace_refresh = hotWorkspaceRefresh,
                diagnostics_after_edit = new
                {
                    total = afterDiagnostics.Count,
                    returned = normalizedAfter.Length,
                    errors = afterDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
                    warnings = afterDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
                    diagnostics = normalizedAfter,
                },
            };

            return new CommandExecutionResult(data, Array.Empty<CommandError>());
        }
    }

    private static List<Diagnostic> MatchDiagnostics(
        IReadOnlyList<Diagnostic> fileDiagnostics,
        string normalizedFilePath,
        string? diagnosticId,
        int? line,
        int? column)
    {
        IEnumerable<Diagnostic> query = fileDiagnostics;
        if (!string.IsNullOrWhiteSpace(diagnosticId))
        {
            query = query.Where(d => string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase));
        }

        if (line is not null)
        {
            int targetLine = line.Value;
            List<Diagnostic> onLine = query
                .Where(d =>
                {
                    FileLinePositionSpan span = d.Location.GetLineSpan();
                    int startLine = span.StartLinePosition.Line + 1;
                    int endLine = span.EndLinePosition.Line + 1;
                    return targetLine >= startLine && targetLine <= endLine;
                })
                .ToList();

            if (column is not null && onLine.Count > 1)
            {
                int targetCol = column.Value;
                List<Diagnostic> containingCol = onLine
                    .Where(d =>
                    {
                        FileLinePositionSpan span = d.Location.GetLineSpan();
                        int startLine = span.StartLinePosition.Line + 1;
                        int endLine = span.EndLinePosition.Line + 1;
                        int startCol = span.StartLinePosition.Character + 1;
                        int endCol = span.EndLinePosition.Character + 1;
                        if (startLine == endLine)
                        {
                            return targetCol >= startCol && targetCol <= Math.Max(startCol, endCol);
                        }

                        if (targetLine == startLine)
                        {
                            return targetCol >= startCol;
                        }

                        if (targetLine == endLine)
                        {
                            return targetCol <= endCol;
                        }

                        return true;
                    })
                    .ToList();

                if (containingCol.Count > 0)
                {
                    onLine = containingCol;
                }
                else
                {
                    onLine = onLine
                        .OrderBy(d => Math.Abs((d.Location.GetLineSpan().StartLinePosition.Character + 1) - targetCol))
                        .ToList();
                }
            }

            return onLine;
        }

        return query
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Character)
            .ToList();
    }

    private static async Task<IReadOnlyList<Diagnostic>> RebindDiagnosticsForDocumentAsync(
        Document document,
        IReadOnlyList<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return diagnostics;
        }

        IReadOnlyList<Diagnostic> docDiagnostics = model.GetDiagnostics(cancellationToken: cancellationToken);
        List<Diagnostic> rebound = new(diagnostics.Count);
        foreach (Diagnostic d in diagnostics)
        {
            Diagnostic? match = docDiagnostics.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, d.Id, StringComparison.OrdinalIgnoreCase) &&
                candidate.Location.SourceSpan == d.Location.SourceSpan);
            rebound.Add(match ?? d);
        }

        return rebound;
    }

    private static RoslynCodeFixCandidate? FindRequestedFix(
        IReadOnlyList<RoslynCodeFixCandidate> offered,
        string requestedFixTitle)
    {
        string trimmed = requestedFixTitle.Trim();
        RoslynCodeFixCandidate? exact = offered.FirstOrDefault(f => string.Equals(f.Title, trimmed, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        return offered.FirstOrDefault(f => string.Equals(f.Title.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static object BuildOfferedFixPayload(RoslynCodeFixCandidate candidate)
    {
        FileLinePositionSpan span = candidate.Diagnostic.Location.GetLineSpan();
        return new
        {
            title = candidate.Title,
            provider = candidate.ProviderName,
            equivalence_key = candidate.EquivalenceKey,
            priority = candidate.Priority.ToString(),
            tier = candidate.Tier switch
            {
                CodeFixTier.Primary => "primary",
                CodeFixTier.Secondary => "secondary",
                _ => "explicit_only",
            },
            auto_selectable = candidate.AutoSelectable,
            diagnostic_id = candidate.Diagnostic.Id,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
        };
    }

    private static object BuildBaseFailureData(
        CommandFileAnalysis analysis,
        string? workspacePath,
        bool requireWorkspace,
        Diagnostic targetDiagnostic,
        int matchedCount,
        IReadOnlyList<RoslynCodeFixCandidate> offered,
        bool apply,
        bool ambiguous = false)
        => new
        {
            file_path = analysis.FilePath,
            workspace_path = workspacePath,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            diagnostic_id = targetDiagnostic.Id,
            target_diagnostic = FormatDiagnostic(targetDiagnostic),
            matched_diagnostic_count = matchedCount,
            offered_fixes = offered.Select(BuildOfferedFixPayload).ToArray(),
            selected_fix = (object?)null,
            ambiguous,
            apply_changes = apply,
            changed = false,
            wrote_file = false,
            changed_file_count = 0,
            wrote_file_count = 0,
            changed_files = Array.Empty<object>(),
        };

    private static object FormatDiagnostic(Diagnostic diagnostic)
    {
        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        return new
        {
            id = diagnostic.Id,
            severity = diagnostic.Severity.ToString(),
            message = diagnostic.GetMessage(),
            file_path = string.IsNullOrWhiteSpace(span.Path) ? diagnostic.Location.SourceTree?.FilePath : span.Path,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
            end_line = span.EndLinePosition.Line + 1,
            end_column = span.EndLinePosition.Character + 1,
        };
    }

    private static string BuildTargetDescription(string? diagnosticId, int? line, int? column)
    {
        List<string> parts = new();
        if (!string.IsNullOrWhiteSpace(diagnosticId))
        {
            parts.Add($"id='{diagnosticId}'");
        }

        if (line is not null)
        {
            parts.Add(column is not null ? $"line={line.Value}, column={column.Value}" : $"line={line.Value}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "file";
    }

    private static string? GetOptionalString(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? GetOptionalPositiveInt(JsonElement input, string propertyName, List<CommandError>? errors)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out int value) || value < 1)
        {
            errors?.Add(new CommandError("invalid_input", $"Property '{propertyName}' must be a positive integer when provided."));
            return null;
        }

        return value;
    }

    private sealed class ProviderTitleComparer : IEqualityComparer<(string Provider, string Title)>
    {
        public static readonly ProviderTitleComparer Instance = new();

        public bool Equals((string Provider, string Title) x, (string Provider, string Title) y)
            => string.Equals(x.Provider, y.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Title, y.Title, StringComparison.Ordinal);

        public int GetHashCode((string Provider, string Title) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Provider),
                StringComparer.Ordinal.GetHashCode(obj.Title));
    }
}
