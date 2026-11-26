using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Collections.Immutable;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class ChangeSignatureCommand : IAgentCommand
{
    private const string TargetMethodAnnotationKind = "RoslynSkills.ChangeSignature.TargetMethod";
    private const string FormatSpanAnnotationKind = "RoslynSkills.ChangeSignature.FormatSpan";

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.change_signature",
        Summary: "Change method signature anchored by line/column or --symbol across the loaded solution (parameters, return type, optional name).",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        string? symbolQuery = SymbolQueryResolution.GetOptionalSymbolQuery(input);
        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            WorkspaceInput.TryGetRequiredWorkspaceTarget(input, errors, out _, out _);
            InputParsing.TryGetRequiredString(input, "parameters", errors, out _);
            InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
            InputParsing.ValidateOptionalBool(input, "apply", errors);
        }
        else
        {
            if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
            {
                return errors;
            }

            InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
            InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
            InputParsing.TryGetRequiredString(input, "parameters", errors, out _);
            WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
            WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
            InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
            if (!File.Exists(filePath))
            {
                errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
            }
        }

        if (input.TryGetProperty("new_name", out JsonElement newNameProperty) && newNameProperty.ValueKind == JsonValueKind.String)
        {
            string? newName = newNameProperty.GetString();
            if (!string.IsNullOrWhiteSpace(newName) && !SyntaxFacts.IsValidIdentifier(newName))
            {
                errors.Add(new CommandError(
                    "invalid_input",
                    $"Property 'new_name' value '{newName}' is not a valid C# identifier."));
            }
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        if (!InputParsing.TryGetRequiredString(input, "parameters", errors, out string parametersRaw))
        {
            return new CommandExecutionResult(null, errors);
        }

        string? newName = null;
        if (input.TryGetProperty("new_name", out JsonElement newNameProperty) && newNameProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = newNameProperty.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                if (!SyntaxFacts.IsValidIdentifier(candidate))
                {
                    return new CommandExecutionResult(
                        null,
                        new[] { new CommandError("invalid_input", $"Property 'new_name' value '{candidate}' is not a valid C# identifier.") });
                }

                newName = candidate;
            }
        }

        string? returnTypeRaw = null;
        if (input.TryGetProperty("return_type", out JsonElement returnTypeProperty) && returnTypeProperty.ValueKind == JsonValueKind.String)
        {
            string? candidate = returnTypeProperty.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                returnTypeRaw = candidate;
            }
        }

        if (!TryParseParameterList(parametersRaw, out ParameterListSyntax? parameterList) || parameterList is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'parameters' could not be parsed as a valid C# parameter list.") });
        }

        TypeSyntax? parsedReturnType = null;
        if (returnTypeRaw is not null)
        {
            parsedReturnType = SyntaxFactory.ParseTypeName(returnTypeRaw);
            if (parsedReturnType.ContainsDiagnostics || parsedReturnType.IsMissing)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_input", $"Property 'return_type' value '{returnTypeRaw}' is not a valid C# type.") });
            }
        }

        string? filePath;
        int line;
        int column;
        string? symbolQuery = SymbolQueryResolution.GetOptionalSymbolQuery(input);
        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            (SymbolQueryResult? resolved, CommandExecutionResult? errorResult) = await SymbolQueryResolution.ResolveAsync(
                input,
                symbolQuery,
                workspacePath,
                targetPredicate: s => s is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation or MethodKind.ReducedExtension },
                invalidTargetMessageFormat: "Symbol '{0}' ({1}) is not a method declaration. Specify a method (e.g. Type.Method or Type.Method(int)) and retry.",
                allowSameTypeOverloads: false,
                cancellationToken).ConfigureAwait(false);
            if (errorResult is not null || resolved is null)
            {
                return errorResult!;
            }

            (string? declFile, int? declLine, int? declColumn) = SymbolQueryResolution.GetPrimaryDeclarationLocation(resolved.Symbols[0]);
            if (string.IsNullOrWhiteSpace(declFile) || !declLine.HasValue || !declColumn.HasValue)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_target", $"Method '{resolved.Symbols[0].ToDisplayString()}' does not have a source declaration.") });
            }

            filePath = declFile;
            line = declLine.Value;
            column = declColumn.Value;
            workspacePath ??= resolved.Workspace.ResolvedWorkspacePath;
        }
        else
        {
            if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out filePath) ||
                !InputParsing.TryGetRequiredInt(input, "line", errors, out line, minValue: 1, maxValue: 1_000_000) ||
                !InputParsing.TryGetRequiredInt(input, "column", errors, out column, minValue: 1, maxValue: 1_000_000))
            {
                return new CommandExecutionResult(null, errors);
            }

            if (!File.Exists(filePath))
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
            }
        }

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

        if (line > analysis.SourceText.Lines.Count)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({analysis.SourceText.Lines.Count}).") });
        }

        SyntaxToken anchorToken = analysis.FindAnchorToken(line, column);
        MethodDeclarationSyntax? method = anchorToken.Parent?
            .AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (method is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "The provided line/column is not inside a method declaration.") });
        }

        if (analysis.Document is not null)
        {
            return await ExecuteSolutionChangeSignatureAsync(
                    analysis,
                    method,
                    parameterList,
                    parsedReturnType,
                    newName,
                    line,
                    column,
                    workspacePath,
                    workspaceHandle,
                    requireWorkspace,
                    apply,
                    maxDiagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await ExecuteAdHocChangeSignatureAsync(
                analysis,
                method,
                parameterList,
                parsedReturnType,
                newName,
                line,
                column,
                workspacePath,
                workspaceHandle,
                requireWorkspace,
                apply,
                maxDiagnostics,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CommandExecutionResult> ExecuteSolutionChangeSignatureAsync(
        CommandFileAnalysis analysis,
        MethodDeclarationSyntax initialMethod,
        ParameterListSyntax newParameterList,
        TypeSyntax? parsedReturnType,
        string? newName,
        int line,
        int column,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        bool apply,
        int maxDiagnostics,
        CancellationToken cancellationToken)
    {
        Solution baseSolution = SolutionEditSupport.EnsureDocumentationCommentParsing(analysis.Document!.Project.Solution);
        DocumentId anchorDocumentId = analysis.Document.Id;

        string oldMemberName = initialMethod.Identifier.ValueText;
        string oldSignature = initialMethod.ParameterList.ToString();
        string oldReturnType = initialMethod.ReturnType.ToString();

        // Tag the target method declaration so we can locate it reliably across Renamer transformations.
        SyntaxAnnotation targetAnnotation = new(TargetMethodAnnotationKind);
        Document anchorDoc = baseSolution.GetDocument(anchorDocumentId)!;
        SyntaxNode anchorRoot = (await anchorDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        MethodDeclarationSyntax? baseMethodNode = anchorRoot
            .FindNode(initialMethod.Span, getInnermostNodeForTie: true)
            .AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (baseMethodNode is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "Unable to locate method declaration in the loaded solution.") });
        }

        Solution currentSolution = baseSolution.WithDocumentSyntaxRoot(
            anchorDocumentId,
            anchorRoot.ReplaceNode(baseMethodNode, baseMethodNode.WithAdditionalAnnotations(targetAnnotation)));

        (MethodDeclarationSyntax? currentMethod, IMethodSymbol? currentMethodSymbol) =
            await ResolveAnnotatedMethodAsync(currentSolution, anchorDocumentId, targetAnnotation, cancellationToken)
                .ConfigureAwait(false);
        if (currentMethod is null || currentMethodSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", "Unable to resolve semantic method symbol in the loaded solution.") });
        }

        ParameterMapping mapping = BuildParameterMapping(currentMethodSymbol.Parameters, newParameterList.Parameters);
        SymbolRenameOptions renameOptions = new(
            RenameOverloads: false,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false);

        // 1. Rename method across the entire solution (including call sites, overrides, interfaces, and XML doc crefs).
        if (!string.IsNullOrWhiteSpace(newName) &&
            !string.Equals(currentMethodSymbol.Name, newName, StringComparison.Ordinal))
        {
            ISymbol renamableMethod = SolutionEditSupport.ResolveRenamableSymbol(currentMethodSymbol);
            currentSolution = await Renamer.RenameSymbolAsync(
                    currentSolution,
                    renamableMethod,
                    renameOptions,
                    newName,
                    cancellationToken)
                .ConfigureAwait(false);

            (currentMethod, currentMethodSymbol) =
                await ResolveAnnotatedMethodAsync(currentSolution, anchorDocumentId, targetAnnotation, cancellationToken)
                    .ConfigureAwait(false);
            if (currentMethod is null || currentMethodSymbol is null)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("symbol_not_found", "Method symbol could not be located after rename.") });
            }
        }

        // 2. Rename any renamed parameters via Renamer (updates body references, named arguments, and <param>/<paramref>).
        foreach ((int oldIndex, string targetParamName) in mapping.ParameterRenames)
        {
            if (oldIndex < 0 || oldIndex >= currentMethodSymbol.Parameters.Length)
            {
                continue;
            }

            IParameterSymbol paramSymbol = currentMethodSymbol.Parameters[oldIndex];
            if (string.Equals(paramSymbol.Name, targetParamName, StringComparison.Ordinal))
            {
                continue;
            }

            currentSolution = await Renamer.RenameSymbolAsync(
                    currentSolution,
                    paramSymbol,
                    renameOptions,
                    targetParamName,
                    cancellationToken)
                .ConfigureAwait(false);

            (currentMethod, currentMethodSymbol) =
                await ResolveAnnotatedMethodAsync(currentSolution, anchorDocumentId, targetAnnotation, cancellationToken)
                    .ConfigureAwait(false);
            if (currentMethod is null || currentMethodSymbol is null)
            {
                break;
            }
        }

        if (currentMethod is null || currentMethodSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", "Method symbol could not be located after parameter rename.") });
        }

        // 3. Collect all related method symbols (overrides, interface members, implementations, partials).
        ImmutableArray<IMethodSymbol> relatedMethods = await CollectRelatedMethodSymbolsAsync(
                currentMethodSymbol,
                currentSolution,
                cancellationToken)
            .ConfigureAwait(false);

        SyntaxAnnotation formatSpanAnnotation = new(FormatSpanAnnotationKind);

        // 4. Rewrite related declarations, call sites, and explicit cref parameter lists in one pass per document
        // so same-file edits (e.g., recursive calls or XML doc crefs) never invalidate declaration spans.
        (currentSolution, int updatedCallSiteCount) = await RewriteSolutionNodesAsync(
                currentSolution,
                relatedMethods,
                mapping,
                newParameterList,
                parsedReturnType,
                formatSpanAnnotation,
                cancellationToken)
            .ConfigureAwait(false);

        // 6. Collect explicit format spans so Formatter only touches rewritten parameter lists, return types, and
        // rewritten argument lists—never disturbing surrounding indentation or opening braces.
        Dictionary<DocumentId, IReadOnlyList<TextSpan>> explicitFormatSpans =
            await CollectAnnotatedFormatSpansAsync(currentSolution, formatSpanAnnotation, cancellationToken)
                .ConfigureAwait(false);

        SolutionEditPlan plan = await SolutionEditSupport.FinalizeSolutionChangesAsync(
                baseSolution,
                currentSolution,
                explicitFormatSpans,
                formatChangedSpans: true,
                cancellationToken)
            .ConfigureAwait(false);

        List<string> writtenPaths = new();
        if (apply)
        {
            foreach (ChangedSolutionFile changedFile in plan.ChangedFiles)
            {
                await EditFileText.WriteAsync(
                        changedFile.FilePath,
                        changedFile.UpdatedText,
                        changedFile.OriginalSnapshot.Encoding,
                        cancellationToken)
                    .ConfigureAwait(false);
                writtenPaths.Add(changedFile.FilePath);
            }
        }

        object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(
                writtenPaths,
                wroteFile: writtenPaths.Count > 0,
                allowAddDocuments: false,
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<DocumentId> diagnosticDocumentIds = plan.AffectedDocumentIds.Count > 0
            ? plan.AffectedDocumentIds
            : new[] { anchorDocumentId };
        IReadOnlyList<Diagnostic> updatedDiagnostics = await WorkspaceDiagnostics.GetDiagnosticsForUpdatedSolutionAsync(
                plan.FinalSolution,
                diagnosticDocumentIds,
                cancellationToken)
            .ConfigureAwait(false);
        NormalizedDiagnostic[] allNormalized = CompilationDiagnostics.Normalize(updatedDiagnostics);
        NormalizedDiagnostic[] normalized = allNormalized.Take(maxDiagnostics).ToArray();
        int diagnosticsErrors = allNormalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = allNormalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        // Read final signature text from the finalized anchor document for accurate reporting.
        string newMemberName = !string.IsNullOrWhiteSpace(newName) ? newName : oldMemberName;
        string finalSignature = newParameterList.ToString();
        string finalReturnType = parsedReturnType?.ToString() ?? oldReturnType;
        Document? finalAnchorDoc = plan.FinalSolution.GetDocument(anchorDocumentId);
        if (finalAnchorDoc is not null)
        {
            SyntaxNode? finalRoot = await finalAnchorDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            MethodDeclarationSyntax? finalMethod = finalRoot?
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => string.Equals(m.Identifier.ValueText, newMemberName, StringComparison.Ordinal) &&
                                     Math.Abs(m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 - line) <= 5);
            if (finalMethod is not null)
            {
                finalSignature = finalMethod.ParameterList.ToString();
                finalReturnType = finalMethod.ReturnType.ToString();
            }
        }

        object data = new
        {
            file_path = analysis.FilePath,
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            edit_scope = "solution",
            line,
            column,
            member_name = oldMemberName,
            new_member_name = newMemberName,
            old_signature = oldSignature,
            new_signature = finalSignature,
            old_return_type = oldReturnType,
            new_return_type = finalReturnType,
            changed_file_count = plan.ChangedFiles.Count,
            wrote_file_count = writtenPaths.Count,
            updated_call_site_count = updatedCallSiteCount,
            changed_files = plan.ChangedFiles
                .Select(f => new
                {
                    file_path = f.FilePath,
                    replacement_count = f.ReplacementCount,
                    changed_line_count = f.ChangedLines.Length,
                    changed_lines = f.ChangedLines,
                })
                .ToArray(),
            apply_changes = apply,
            wrote_file = writtenPaths.Count > 0,
            changed = plan.ChangedFiles.Count > 0,
            hot_workspace_refresh = hotWorkspaceRefresh,
            diagnostics_after_edit = new
            {
                total = updatedDiagnostics.Count,
                returned = normalized.Length,
                errors = diagnosticsErrors,
                warnings = diagnosticsWarnings,
                diagnostics = normalized,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static async Task<CommandExecutionResult> ExecuteAdHocChangeSignatureAsync(
        CommandFileAnalysis analysis,
        MethodDeclarationSyntax method,
        ParameterListSyntax parameterList,
        TypeSyntax? parsedReturnType,
        string? newName,
        int line,
        int column,
        string? workspacePath,
        string? workspaceHandle,
        bool requireWorkspace,
        bool apply,
        int maxDiagnostics,
        CancellationToken cancellationToken)
    {
        FileTextSnapshot originalSnapshot = await EditFileText.ReadAsync(analysis.FilePath, cancellationToken).ConfigureAwait(false);
        ParameterListSyntax normalizedParameterList = ReplaceParameterListPreservingTrivia(method.ParameterList, parameterList);
        MethodDeclarationSyntax updatedMethod = method.WithParameterList(normalizedParameterList);

        if (parsedReturnType is not null)
        {
            TypeSyntax normalizedReturnType = parsedReturnType
                .WithLeadingTrivia(method.ReturnType.GetLeadingTrivia())
                .WithTrailingTrivia(method.ReturnType.GetTrailingTrivia());
            updatedMethod = updatedMethod.WithReturnType(normalizedReturnType);
        }

        if (!string.IsNullOrWhiteSpace(newName))
        {
            SyntaxToken oldIdentifier = updatedMethod.Identifier;
            updatedMethod = updatedMethod.WithIdentifier(SyntaxFactory.Identifier(
                oldIdentifier.LeadingTrivia,
                newName,
                oldIdentifier.TrailingTrivia));
        }

        SyntaxNode updatedRoot = analysis.Root.ReplaceNode(method, updatedMethod);
        string updatedSource = EditFileText.PreserveLineEndings(originalSnapshot, updatedRoot.ToFullString());
        bool changed = !string.Equals(originalSnapshot.Text, updatedSource, StringComparison.Ordinal);

        IReadOnlyList<Diagnostic> diagnostics = WorkspaceDiagnostics.GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
        NormalizedDiagnostic[] allNormalized = CompilationDiagnostics.Normalize(diagnostics);
        NormalizedDiagnostic[] normalized = allNormalized.Take(maxDiagnostics).ToArray();
        int diagnosticsErrors = allNormalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int diagnosticsWarnings = allNormalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        bool wroteFile = false;
        if (apply && changed)
        {
            await EditFileText.WriteAsync(
                    analysis.FilePath,
                    updatedSource,
                    originalSnapshot.Encoding,
                    cancellationToken)
                .ConfigureAwait(false);
            wroteFile = true;
        }

        object data = new
        {
            file_path = analysis.FilePath,
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            require_workspace = requireWorkspace,
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            edit_scope = "file_ad_hoc",
            line,
            column,
            member_name = method.Identifier.ValueText,
            new_member_name = updatedMethod.Identifier.ValueText,
            old_signature = method.ParameterList.ToString(),
            new_signature = updatedMethod.ParameterList.ToString(),
            old_return_type = method.ReturnType.ToString(),
            new_return_type = updatedMethod.ReturnType.ToString(),
            changed_file_count = changed ? 1 : 0,
            wrote_file_count = wroteFile ? 1 : 0,
            updated_call_site_count = 0,
            changed_files = changed
                ? new object[]
                {
                    new
                    {
                        file_path = analysis.FilePath,
                        replacement_count = 1,
                        changed_line_count = 1,
                        changed_lines = new[] { line },
                    },
                }
                : Array.Empty<object>(),
            apply_changes = apply,
            wrote_file = wroteFile,
            changed,
            diagnostics_after_edit = new
            {
                total = diagnostics.Count,
                returned = normalized.Length,
                errors = diagnosticsErrors,
                warnings = diagnosticsWarnings,
                diagnostics = normalized,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static ParameterListSyntax ReplaceParameterListPreservingTrivia(
        ParameterListSyntax existing,
        ParameterListSyntax replacement)
        => replacement
            .WithLeadingTrivia(existing.GetLeadingTrivia())
            .WithTrailingTrivia(existing.GetTrailingTrivia());

    private static async Task<(MethodDeclarationSyntax? Method, IMethodSymbol? Symbol)> ResolveAnnotatedMethodAsync(
        Solution solution,
        DocumentId documentId,
        SyntaxAnnotation annotation,
        CancellationToken cancellationToken)
    {
        Document? document = solution.GetDocument(documentId);
        if (document is null)
        {
            return (null, null);
        }

        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return (null, null);
        }

        MethodDeclarationSyntax? method = root.GetAnnotatedNodes(annotation)
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (method is null)
        {
            return (null, null);
        }

        IMethodSymbol? symbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
        return (method, symbol);
    }

    private static async Task<ImmutableArray<IMethodSymbol>> CollectRelatedMethodSymbolsAsync(
        IMethodSymbol methodSymbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        HashSet<IMethodSymbol> collected = new(SymbolEqualityComparer.Default);
        Queue<IMethodSymbol> queue = new();

        void Enqueue(IMethodSymbol? candidate)
        {
            if (candidate is null)
            {
                return;
            }

            IMethodSymbol original = candidate.OriginalDefinition;
            if (collected.Add(original))
            {
                queue.Enqueue(original);
            }
        }

        Enqueue(methodSymbol);

        while (queue.Count > 0)
        {
            IMethodSymbol current = queue.Dequeue();

            if (current.OverriddenMethod is not null)
            {
                Enqueue(current.OverriddenMethod);
            }

            foreach (IMethodSymbol explicitImpl in current.ExplicitInterfaceImplementations)
            {
                Enqueue(explicitImpl);
            }

            if (current.ContainingType is not null && current.ContainingType.TypeKind != TypeKind.Interface)
            {
                foreach (INamedTypeSymbol iface in current.ContainingType.AllInterfaces)
                {
                    foreach (ISymbol member in iface.GetMembers(current.Name))
                    {
                        if (member is IMethodSymbol ifaceMethod)
                        {
                            ISymbol? impl = current.ContainingType.FindImplementationForInterfaceMember(ifaceMethod);
                            if (impl is IMethodSymbol implMethod &&
                                SymbolEqualityComparer.Default.Equals(implMethod.OriginalDefinition, current.OriginalDefinition))
                            {
                                Enqueue(ifaceMethod);
                            }
                        }
                    }
                }
            }

            IEnumerable<ISymbol> overrides = await SymbolFinder.FindOverridesAsync(
                    current,
                    solution,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (ISymbol symbol in overrides)
            {
                if (symbol is IMethodSymbol overrideMethod)
                {
                    Enqueue(overrideMethod);
                }
            }

            if (current.ContainingType?.TypeKind == TypeKind.Interface)
            {
                IEnumerable<ISymbol> implementations = await SymbolFinder.FindImplementationsAsync(
                        current,
                        solution,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                foreach (ISymbol symbol in implementations)
                {
                    if (symbol is IMethodSymbol implMethod)
                    {
                        Enqueue(implMethod);
                    }
                }
            }
        }

        return collected.ToImmutableArray();
    }

    private static async Task<(Solution UpdatedSolution, int UpdatedCallSites)> RewriteSolutionNodesAsync(
        Solution solution,
        ImmutableArray<IMethodSymbol> relatedMethods,
        ParameterMapping mapping,
        ParameterListSyntax newParameterList,
        TypeSyntax? parsedReturnType,
        SyntaxAnnotation formatSpanAnnotation,
        CancellationToken cancellationToken)
    {
        Dictionary<DocumentId, List<TextSpan>> refSpansByDoc = new();
        Dictionary<DocumentId, List<TextSpan>> declSpansByDoc = new();

        foreach (IMethodSymbol method in relatedMethods)
        {
            foreach (SyntaxReference syntaxRef in method.DeclaringSyntaxReferences)
            {
                Document? doc = solution.GetDocument(syntaxRef.SyntaxTree);
                if (doc is null)
                {
                    continue;
                }

                if (!declSpansByDoc.TryGetValue(doc.Id, out List<TextSpan>? declSpans))
                {
                    declSpans = new List<TextSpan>();
                    declSpansByDoc[doc.Id] = declSpans;
                }

                declSpans.Add(syntaxRef.Span);
            }

            if (!mapping.RequiresCallSiteRewrite)
            {
                continue;
            }

            IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(
                    method,
                    solution,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (ReferencedSymbol referenced in references)
            {
                foreach (ReferenceLocation location in referenced.Locations)
                {
                    if (location.IsImplicit || location.Document is null)
                    {
                        continue;
                    }

                    if (!refSpansByDoc.TryGetValue(location.Document.Id, out List<TextSpan>? spans))
                    {
                        spans = new List<TextSpan>();
                        refSpansByDoc[location.Document.Id] = spans;
                    }

                    spans.Add(location.Location.SourceSpan);
                }
            }
        }

        HashSet<DocumentId> allDocIds = new(declSpansByDoc.Keys);
        allDocIds.UnionWith(refSpansByDoc.Keys);

        Solution updatedSolution = solution;
        int totalUpdatedCallSites = 0;

        foreach (DocumentId docId in allDocIds)
        {
            Document? doc = updatedSolution.GetDocument(docId);
            if (doc is null)
            {
                continue;
            }

            SyntaxNode? root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            Dictionary<SyntaxNode, Func<SyntaxNode, SyntaxNode>> rewriters = new();

            if (refSpansByDoc.TryGetValue(docId, out List<TextSpan>? refSpans))
            {
                foreach (TextSpan refSpan in refSpans.Distinct())
                {
                    SyntaxToken token = root.FindToken(refSpan.Start, findInsideTrivia: true);
                    SyntaxNode? parent = token.Parent;
                    if (parent is null)
                    {
                        continue;
                    }

                    // Check for XML doc cref with explicit parameter list: <see cref="Foo(int, string)"/>
                    NameMemberCrefSyntax? crefSyntax = parent.AncestorsAndSelf().OfType<NameMemberCrefSyntax>().FirstOrDefault();
                    if (crefSyntax?.Parameters is not null &&
                        !rewriters.ContainsKey(crefSyntax.Parameters))
                    {
                        rewriters[crefSyntax.Parameters] = rewritten =>
                            BuildUpdatedCrefParameterList((CrefParameterListSyntax)rewritten, newParameterList)
                                .WithAdditionalAnnotations(formatSpanAnnotation);
                        continue;
                    }

                    InvocationExpressionSyntax? invocation = FindOwningInvocation(parent);
                    if (invocation is null || rewriters.ContainsKey(invocation.ArgumentList))
                    {
                        continue;
                    }

                    ArgumentListSyntax? rewrittenArgList = TryRewriteArgumentList(
                        invocation.ArgumentList,
                        mapping,
                        newParameterList);
                    if (rewrittenArgList is not null)
                    {
                        ArgumentListSyntax capturedArgList = rewrittenArgList;
                        rewriters[invocation.ArgumentList] = rewritten => capturedArgList
                            .WithLeadingTrivia(rewritten.GetLeadingTrivia())
                            .WithTrailingTrivia(rewritten.GetTrailingTrivia())
                            .WithAdditionalAnnotations(formatSpanAnnotation);
                        totalUpdatedCallSites++;
                    }
                }
            }

            if (declSpansByDoc.TryGetValue(docId, out List<TextSpan>? declSpans))
            {
                foreach (TextSpan declSpan in declSpans.Distinct())
                {
                    MethodDeclarationSyntax? methodNode = root
                        .FindNode(declSpan, getInnermostNodeForTie: true)
                        .AncestorsAndSelf()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault();
                    if (methodNode is null || rewriters.ContainsKey(methodNode))
                    {
                        continue;
                    }

                    rewriters[methodNode] = rewritten =>
                    {
                        MethodDeclarationSyntax currentMethodNode = (MethodDeclarationSyntax)rewritten;
                        ParameterListSyntax normalizedParamList = ReplaceParameterListPreservingTrivia(
                                currentMethodNode.ParameterList,
                                newParameterList)
                            .WithAdditionalAnnotations(formatSpanAnnotation);
                        MethodDeclarationSyntax updatedMethod = currentMethodNode.WithParameterList(normalizedParamList);

                        if (parsedReturnType is not null)
                        {
                            TypeSyntax normalizedReturnType = parsedReturnType
                                .WithLeadingTrivia(currentMethodNode.ReturnType.GetLeadingTrivia())
                                .WithTrailingTrivia(currentMethodNode.ReturnType.GetTrailingTrivia())
                                .WithAdditionalAnnotations(formatSpanAnnotation);
                            updatedMethod = updatedMethod.WithReturnType(normalizedReturnType);
                        }

                        return updatedMethod;
                    };
                }
            }

            if (rewriters.Count == 0)
            {
                continue;
            }

            SyntaxNode updatedRoot = root.ReplaceNodes(
                rewriters.Keys,
                (original, rewritten) => rewriters[original](rewritten));
            updatedSolution = updatedSolution.WithDocumentSyntaxRoot(docId, updatedRoot);
        }

        return (updatedSolution, totalUpdatedCallSites);
    }

    private static InvocationExpressionSyntax? FindOwningInvocation(SyntaxNode node)
    {
        SyntaxNode current = node;
        while (current.Parent is not null)
        {
            if (current.Parent is InvocationExpressionSyntax invocation &&
                ReferenceEquals(invocation.Expression, current))
            {
                return invocation;
            }

            if (current.Parent is MemberAccessExpressionSyntax memberAccess &&
                ReferenceEquals(memberAccess.Name, current))
            {
                current = memberAccess;
                continue;
            }

            if (current.Parent is MemberBindingExpressionSyntax memberBinding &&
                ReferenceEquals(memberBinding.Name, current))
            {
                current = memberBinding;
                continue;
            }

            if (current.Parent is GenericNameSyntax)
            {
                current = current.Parent;
                continue;
            }

            break;
        }

        return null;
    }

    private static ArgumentListSyntax? TryRewriteArgumentList(
        ArgumentListSyntax existingArguments,
        ParameterMapping mapping,
        ParameterListSyntax newParameterList)
    {
        SeparatedSyntaxList<ArgumentSyntax> oldArgs = existingArguments.Arguments;
        if (oldArgs.Count == 0)
        {
            return null;
        }

        // Map each existing argument to its old parameter index.
        Dictionary<int, ArgumentSyntax> argsByOldIndex = new();
        List<ArgumentSyntax> paramsOverflowArgs = new();
        int oldParamCount = mapping.OldParameterNames.Count;

        for (int i = 0; i < oldArgs.Count; i++)
        {
            ArgumentSyntax arg = oldArgs[i];
            if (arg.NameColon is not null)
            {
                string argName = arg.NameColon.Name.Identifier.ValueText;
                int namedIndex = mapping.FindOldIndexByName(argName);
                if (namedIndex >= 0)
                {
                    argsByOldIndex[namedIndex] = arg;
                }
            }
            else if (i < oldParamCount)
            {
                argsByOldIndex[i] = arg;
            }
            else if (mapping.LastOldParameterIsParams)
            {
                paramsOverflowArgs.Add(arg);
            }
        }

        List<ArgumentSyntax> newArgs = new();
        bool skippedAnyPrecedingNewParam = false;
        for (int newIndex = 0; newIndex < newParameterList.Parameters.Count; newIndex++)
        {
            if (mapping.NewToOldIndex.TryGetValue(newIndex, out int oldIndex) &&
                argsByOldIndex.TryGetValue(oldIndex, out ArgumentSyntax? existingArg))
            {
                ArgumentSyntax cleanArg = existingArg.WithoutTrivia();
                if (skippedAnyPrecedingNewParam && cleanArg.NameColon is null)
                {
                    string newParamName = newParameterList.Parameters[newIndex].Identifier.ValueText;
                    cleanArg = cleanArg.WithNameColon(SyntaxFactory.NameColon(newParamName));
                }

                newArgs.Add(cleanArg);

                if (mapping.LastOldParameterIsParams && oldIndex == oldParamCount - 1)
                {
                    foreach (ArgumentSyntax overflow in paramsOverflowArgs)
                    {
                        newArgs.Add(overflow.WithoutTrivia());
                    }
                }
            }
            else
            {
                skippedAnyPrecedingNewParam = true;
            }
        }

        SeparatedSyntaxList<ArgumentSyntax> separated = SyntaxFactory.SeparatedList(newArgs);
        return SyntaxFactory.ArgumentList(separated);
    }

    private static CrefParameterListSyntax BuildUpdatedCrefParameterList(
        CrefParameterListSyntax existing,
        ParameterListSyntax newParameterList)
    {
        List<CrefParameterSyntax> crefParams = new();
        foreach (ParameterSyntax parameter in newParameterList.Parameters)
        {
            if (parameter.Type is null)
            {
                continue;
            }

            SyntaxToken refKindKeyword = parameter.Modifiers.FirstOrDefault(m =>
                m.IsKind(SyntaxKind.RefKeyword) ||
                m.IsKind(SyntaxKind.OutKeyword) ||
                m.IsKind(SyntaxKind.InKeyword));
            CrefParameterSyntax crefParam = refKindKeyword.IsKind(SyntaxKind.None)
                ? SyntaxFactory.CrefParameter(parameter.Type.WithoutTrivia())
                : SyntaxFactory.CrefParameter(SyntaxFactory.Token(refKindKeyword.Kind()), parameter.Type.WithoutTrivia());
            crefParams.Add(crefParam);
        }

        List<SyntaxNodeOrToken> separatedNodes = new();
        for (int i = 0; i < crefParams.Count; i++)
        {
            if (i > 0)
            {
                separatedNodes.Add(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space));
            }

            separatedNodes.Add(crefParams[i]);
        }

        return SyntaxFactory.CrefParameterList(SyntaxFactory.SeparatedList<CrefParameterSyntax>(separatedNodes))
            .WithLeadingTrivia(existing.GetLeadingTrivia())
            .WithTrailingTrivia(existing.GetTrailingTrivia());
    }

    private static async Task<Dictionary<DocumentId, IReadOnlyList<TextSpan>>> CollectAnnotatedFormatSpansAsync(
        Solution solution,
        SyntaxAnnotation formatSpanAnnotation,
        CancellationToken cancellationToken)
    {
        Dictionary<DocumentId, IReadOnlyList<TextSpan>> spansByDoc = new();
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                TextSpan[] spans = root.GetAnnotatedNodes(formatSpanAnnotation)
                    .Select(node => node.Span)
                    .Where(span => span.Length > 0)
                    .OrderBy(span => span.Start)
                    .ToArray();
                if (spans.Length > 0)
                {
                    spansByDoc[document.Id] = spans;
                }
            }
        }

        return spansByDoc;
    }

    private static ParameterMapping BuildParameterMapping(
        ImmutableArray<IParameterSymbol> oldParameters,
        SeparatedSyntaxList<ParameterSyntax> newParameters)
    {
        Dictionary<int, int> oldToNew = new();
        Dictionary<int, int> newToOld = new();
        List<(int OldIndex, string NewName)> renames = new();

        // Pass 1: exact name match (preserves reorders as reorders instead of misclassifying them as renames).
        for (int oldIndex = 0; oldIndex < oldParameters.Length; oldIndex++)
        {
            string oldName = oldParameters[oldIndex].Name;
            for (int newIndex = 0; newIndex < newParameters.Count; newIndex++)
            {
                if (newToOld.ContainsKey(newIndex))
                {
                    continue;
                }

                if (string.Equals(oldName, newParameters[newIndex].Identifier.ValueText, StringComparison.Ordinal))
                {
                    oldToNew[oldIndex] = newIndex;
                    newToOld[newIndex] = oldIndex;
                    break;
                }
            }
        }

        // Pass 2: positional rename match for unmatched slots with matching position.
        bool sameArity = oldParameters.Length == newParameters.Count;
        for (int i = 0; i < Math.Min(oldParameters.Length, newParameters.Count); i++)
        {
            if (oldToNew.ContainsKey(i) || newToOld.ContainsKey(i))
            {
                continue;
            }

            if (sameArity)
            {
                oldToNew[i] = i;
                newToOld[i] = i;
                renames.Add((i, newParameters[i].Identifier.ValueText));
            }
        }

        bool anyRemoved = oldToNew.Count < oldParameters.Length;
        bool anyShiftedOrReordered = oldToNew.Any(pair => pair.Key != pair.Value);
        bool requiresCallSiteRewrite = anyRemoved || anyShiftedOrReordered;
        bool lastOldIsParams = oldParameters.Length > 0 && oldParameters[^1].IsParams;

        // Post-rename names for lookup when rewriting call sites.
        string[] postRenameOldNames = new string[oldParameters.Length];
        for (int i = 0; i < oldParameters.Length; i++)
        {
            postRenameOldNames[i] = oldParameters[i].Name;
        }

        foreach ((int oldIndex, string renamedTo) in renames)
        {
            postRenameOldNames[oldIndex] = renamedTo;
        }

        return new ParameterMapping(
            postRenameOldNames,
            oldToNew,
            newToOld,
            renames,
            requiresCallSiteRewrite,
            lastOldIsParams);
    }

    private static bool TryParseParameterList(string raw, out ParameterListSyntax? parameterList)
    {
        parameterList = null;
        string trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (!trimmed.StartsWith('('))
        {
            trimmed = $"({trimmed})";
        }

        parameterList = SyntaxFactory.ParseParameterList(trimmed);
        return !parameterList.ContainsDiagnostics && !parameterList.IsMissing;
    }

    private sealed record ParameterMapping(
        IReadOnlyList<string> OldParameterNames,
        IReadOnlyDictionary<int, int> OldToNewIndex,
        IReadOnlyDictionary<int, int> NewToOldIndex,
        IReadOnlyList<(int OldIndex, string NewName)> ParameterRenames,
        bool RequiresCallSiteRewrite,
        bool LastOldParameterIsParams)
    {
        public int FindOldIndexByName(string name)
        {
            for (int i = 0; i < OldParameterNames.Count; i++)
            {
                if (string.Equals(OldParameterNames[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}

