using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Compound Mindspace retrieval command (<c>ctx.symbol_context</c> / MCP <c>roslyn_context</c>): collapses the
/// common multi-turn agent loop (symbol search + declaration read + references + interface-aware callers + callees +
/// implementations + file diagnostics) into one bounded hot-workspace round trip.
/// </summary>
public sealed class SymbolContextCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.symbol_context",
        Summary: "Compound symbol mindspace retrieval: resolves --symbol or file:line:col and returns declaration source (zero-disk), semantic references, interface-aware callers, callees, implementations, and file diagnostics in one bounded round trip.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        string? symbolQuery = SymbolQueryResolution.GetOptionalSymbolQuery(input);
        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            WorkspaceInput.TryGetRequiredWorkspaceTarget(input, errors, out _, out _);
        }
        else
        {
            if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
            {
                return errors;
            }

            InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
            InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
            if (!File.Exists(filePath))
            {
                errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
            }
        }

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalBool(input, "include_definition", errors);
        InputParsing.ValidateOptionalBool(input, "include_references", errors);
        InputParsing.ValidateOptionalBool(input, "include_callers", errors);
        InputParsing.ValidateOptionalBool(input, "include_callees", errors);
        InputParsing.ValidateOptionalBool(input, "include_implementations", errors);
        InputParsing.ValidateOptionalBool(input, "include_diagnostics", errors);
        InputParsing.ValidateOptionalBool(input, "include_line_numbers", errors);
        InputParsing.ValidateOptionalInt(input, "max_definition_lines", errors, minValue: 1, maxValue: 500);
        InputParsing.ValidateOptionalInt(input, "max_references", errors, minValue: 0, maxValue: 200);
        InputParsing.ValidateOptionalInt(input, "max_callers", errors, minValue: 0, maxValue: 200);
        InputParsing.ValidateOptionalInt(input, "max_callees", errors, minValue: 0, maxValue: 200);
        InputParsing.ValidateOptionalInt(input, "max_implementations", errors, minValue: 0, maxValue: 200);
        InputParsing.ValidateOptionalInt(input, "max_diagnostics", errors, minValue: 0, maxValue: 200);
        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        IReadOnlyList<CommandError> validationErrors = Validate(input);
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        bool includeDefinition = InputParsing.GetOptionalBool(input, "include_definition", defaultValue: true);
        bool includeReferences = InputParsing.GetOptionalBool(input, "include_references", defaultValue: true);
        bool includeCallers = InputParsing.GetOptionalBool(input, "include_callers", defaultValue: true);
        bool includeCallees = InputParsing.GetOptionalBool(input, "include_callees", defaultValue: true);
        bool includeImplementations = InputParsing.GetOptionalBool(input, "include_implementations", defaultValue: true);
        bool includeDiagnostics = InputParsing.GetOptionalBool(input, "include_diagnostics", defaultValue: true);
        bool includeLineNumbers = InputParsing.GetOptionalBool(input, "include_line_numbers", defaultValue: true);
        int maxDefinitionLines = InputParsing.GetOptionalInt(input, "max_definition_lines", defaultValue: brief ? 50 : 120, minValue: 1, maxValue: 500);
        int defContextBefore = InputParsing.GetOptionalInt(input, "definition_context_before", defaultValue: 2, minValue: 0, maxValue: 30);
        int defContextAfter = InputParsing.GetOptionalInt(input, "definition_context_after", defaultValue: 2, minValue: 0, maxValue: 30);
        int maxReferences = InputParsing.GetOptionalInt(input, "max_references", defaultValue: brief ? 20 : 50, minValue: 0, maxValue: 200);
        int maxCallers = InputParsing.GetOptionalInt(input, "max_callers", defaultValue: brief ? 15 : 40, minValue: 0, maxValue: 200);
        int maxCallees = InputParsing.GetOptionalInt(input, "max_callees", defaultValue: brief ? 12 : 30, minValue: 0, maxValue: 200);
        int maxImplementations = InputParsing.GetOptionalInt(input, "max_implementations", defaultValue: brief ? 15 : 40, minValue: 0, maxValue: 200);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 20, minValue: 0, maxValue: 200);
        int refContextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 0, minValue: 0, maxValue: 5);

        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        HostedWorkspace? hosted = !string.IsNullOrWhiteSpace(workspaceHandle) &&
                                  WorkspaceHostStoreProvider.Current.TryGet(workspaceHandle, out HostedWorkspace? foundHosted)
            ? foundHosted
            : null;

        ISymbol targetSymbol;
        Solution? solution;
        CommandFileAnalysis? fileAnalysis = null;
        string? symbolQuery = SymbolQueryResolution.GetOptionalSymbolQuery(input);

        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            (SymbolQueryResult? resolved, CommandExecutionResult? errorResult) = await SymbolQueryResolution.ResolveAsync(
                input,
                symbolQuery,
                workspacePath,
                targetPredicate: null,
                invalidTargetMessageFormat: string.Empty,
                allowSameTypeOverloads: false,
                cancellationToken).ConfigureAwait(false);
            if (errorResult is not null || resolved is null)
            {
                return errorResult!;
            }

            targetSymbol = SolutionSearchScope.NormalizePositionalRecordProperty(resolved.Symbols[0]);
            solution = resolved.Workspace.Solution;
        }
        else
        {
            string filePath = input.GetProperty("file_path").GetString()!.Trim();
            int line = input.GetProperty("line").GetInt32();
            int column = input.GetProperty("column").GetInt32();

            fileAnalysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath, workspaceHandle).ConfigureAwait(false);
            CommandExecutionResult? workspaceError = WorkspaceGuard.RequireWorkspaceIfRequested(Descriptor.Id, requireWorkspace, fileAnalysis);
            if (workspaceError is not null)
            {
                return workspaceError;
            }

            if (line > fileAnalysis.SourceText.Lines.Count)
            {
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("invalid_input", $"Requested line '{line}' exceeds file line count ({fileAnalysis.SourceText.Lines.Count}).") });
            }

            SyntaxToken anchorToken = fileAnalysis.FindAnchorToken(line, column);
            ISymbol? resolvedSym = SymbolResolution.GetSymbolForToken(anchorToken, fileAnalysis.SemanticModel, cancellationToken);
            if (resolvedSym is null)
            {
                string hint = SymbolResolution.FormatLineSymbolHint(fileAnalysis, line, predicate: null, cancellationToken);
                return new CommandExecutionResult(
                    null,
                    new[] { new CommandError("symbol_not_found", $"Unable to resolve a semantic symbol at {line}:{column}.{hint}") });
            }

            targetSymbol = SolutionSearchScope.NormalizePositionalRecordProperty(resolvedSym);
            solution = fileAnalysis.Document?.Project.Solution;
        }

        // 1. Declaration location & zero-disk source window
        (string? declFilePath, int? declLine, int? declColumn) = SymbolQueryResolution.GetPrimaryDeclarationLocation(targetSymbol);
        string textSource = hosted is not null ? "workspace_overlay_index" : "hot_workspace";
        string? fileSha256 = null;
        object? definitionSourcePayload = null;
        int? declEndLine = null;
        SyntaxTree? declTree = null;

        SyntaxReference? primaryDeclRef = targetSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (primaryDeclRef is not null)
        {
            declTree = primaryDeclRef.SyntaxTree;
            if (string.IsNullOrWhiteSpace(declFilePath) && !string.IsNullOrWhiteSpace(declTree.FilePath))
            {
                declFilePath = Path.GetFullPath(declTree.FilePath);
            }
        }
        else
        {
            Location? srcLoc = targetSymbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree is not null);
            declTree = srcLoc?.SourceTree;
            if (string.IsNullOrWhiteSpace(declFilePath) && declTree is not null && !string.IsNullOrWhiteSpace(declTree.FilePath))
            {
                declFilePath = Path.GetFullPath(declTree.FilePath);
            }
        }

        OverlayDocumentEntry? declOverlayEntry = null;
        if (hosted is not null &&
            declFilePath is not null &&
            hosted.OverlayIndex.TryGetDocument(hosted.Workspace, declFilePath, out OverlayDocumentEntry? foundOverlay, cancellationToken) &&
            foundOverlay is not null)
        {
            declOverlayEntry = foundOverlay;
            fileSha256 = foundOverlay.GetTextSha256();
            textSource = "workspace_overlay_index";
        }
        else if (declFilePath is not null)
        {
            fileSha256 = EditFileText.TryComputeFileSha256(declFilePath);
        }

        if (includeDefinition && declTree is not null)
        {
            SourceText? declSourceText = null;
            if (declOverlayEntry is not null)
            {
                declSourceText = declOverlayEntry.SourceText;
                textSource = "workspace_overlay_index";
            }
            else
            {
                declSourceText = await declTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                textSource = hosted is not null ? "hot_workspace" : "disk";
            }

            TextSpan declSpan;
            if (primaryDeclRef is not null)
            {
                SyntaxNode declNode = await primaryDeclRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                declSpan = declNode.Span;
            }
            else if (targetSymbol.Locations.FirstOrDefault(l => l.IsInSource) is { } loc)
            {
                declSpan = loc.SourceSpan;
            }
            else
            {
                declSpan = default;
            }

            LinePositionSpan lineSpan = declSourceText.Lines.GetLinePositionSpan(declSpan);
            int nodeStartLine = lineSpan.Start.Line + 1;
            int nodeEndLine = Math.Max(nodeStartLine, lineSpan.End.Line + 1);
            declEndLine = nodeEndLine;

            int windowStart = Math.Max(1, nodeStartLine - defContextBefore);
            int windowEnd = Math.Min(declSourceText.Lines.Count, nodeEndLine + defContextAfter);
            bool defTruncated = false;
            if (windowEnd - windowStart + 1 > maxDefinitionLines)
            {
                windowEnd = windowStart + maxDefinitionLines - 1;
                defTruncated = true;
            }

            StringBuilder sb = new();
            for (int ln = windowStart; ln <= windowEnd; ln++)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                string textLine = declSourceText.Lines[ln - 1].ToString();
                sb.Append(includeLineNumbers ? $"{ln,5}: {textLine}" : textLine);
            }

            definitionSourcePayload = new
            {
                file_path = declFilePath,
                file_sha256 = fileSha256,
                declaration_start_line = nodeStartLine,
                declaration_end_line = nodeEndLine,
                start_line = windowStart,
                end_line = windowEnd,
                line_count = windowEnd - windowStart + 1,
                truncated = defTruncated,
                content = sb.ToString(),
            };
        }

        // 2. Semantic references across solution
        object? referencesPayload = null;
        if (includeReferences && maxReferences > 0 && solution is not null)
        {
            (List<FindReferencesCommand.ReferenceMatch> refMatches, bool refTruncated) = await FindReferencesCommand
                .FindSolutionReferencesAsync(
                    solution,
                    targetSymbol,
                    includeDeclaration: false,
                    includeGenerated: false,
                    contextLines: refContextLines,
                    maxResults: maxReferences,
                    cancellationToken)
                .ConfigureAwait(false);

            referencesPayload = new
            {
                total_returned = refMatches.Count,
                truncated = refTruncated,
                matches = refMatches.Select(m => new
                {
                    m.file_path,
                    m.line,
                    m.column,
                    m.syntax_kind,
                    preview = SingleLinePreview(m.snippet),
                }).ToArray(),
            };
        }

        // 3. Interface-aware 1-hop incoming callers & outgoing callees (reusing CallHierarchyCommand.CallGraph)
        object? callersPayload = null;
        object? calleesPayload = null;
        bool supportsCallGraph = targetSymbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;
        if (supportsCallGraph && solution is not null && ((includeCallers && maxCallers > 0) || (includeCallees && maxCallees > 0)))
        {
            ISymbol normalizedAnchor = CallHierarchyCommand.NormalizeGraphSymbol(targetSymbol);
            SearchScope scope = await SolutionSearchScope.ResolveForSolutionAsync(
                solution,
                anchorProject: null,
                target: null,
                includeGenerated: false,
                cancellationToken).ConfigureAwait(false);
            CallHierarchyCommand.CallGraph graph = CallHierarchyCommand.CallGraph.Create(
                scope,
                includeObjectCreations: true,
                includeExternal: false,
                contextLines: 0,
                brief: false);
            string anchorId = graph.GetOrAddNode(normalizedAnchor).symbol_id;

            if (includeCallers && maxCallers > 0)
            {
                IReadOnlyList<CallHierarchyCommand.CallHierarchyEdge> incoming = graph.GetIncomingEdges(anchorId, cancellationToken);
                var limited = incoming.Take(maxCallers).Select(e =>
                {
                    graph.nodesById.TryGetValue(e.from_symbol_id, out CallHierarchyCommand.CallHierarchyNode? callerNode);
                    return new
                    {
                        caller = callerNode?.symbol_display ?? e.from_symbol_id,
                        e.call_kind,
                        e.via_lambda,
                        e.implemented_member,
                        e.file_path,
                        e.line,
                        e.column,
                        preview = SingleLinePreview(e.snippet),
                    };
                }).ToArray();

                callersPayload = new
                {
                    total_count = incoming.Count,
                    returned = limited.Length,
                    truncated = incoming.Count > maxCallers,
                    items = limited,
                };
            }

            if (includeCallees && maxCallees > 0)
            {
                IReadOnlyList<CallHierarchyCommand.CallHierarchyEdge> outgoing = graph.GetOutgoingEdges(anchorId, cancellationToken);
                var limited = outgoing.Take(maxCallees).Select(e =>
                {
                    graph.nodesById.TryGetValue(e.to_symbol_id, out CallHierarchyCommand.CallHierarchyNode? calleeNode);
                    return new
                    {
                        callee = calleeNode?.symbol_display ?? e.to_symbol_id,
                        e.call_kind,
                        e.file_path,
                        e.line,
                        e.column,
                        preview = SingleLinePreview(e.snippet),
                    };
                }).ToArray();

                calleesPayload = new
                {
                    total_count = outgoing.Count,
                    returned = limited.Length,
                    truncated = outgoing.Count > maxCallees,
                    items = limited,
                };
            }
        }

        // 4. Implementations & overrides
        object? implementationsPayload = null;
        if (includeImplementations &&
            maxImplementations > 0 &&
            solution is not null &&
            targetSymbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            IReadOnlyList<RelatedDeclaration> related = await SolutionSymbolRelations
                .FindImplementationsAsync(solution, targetSymbol, cancellationToken)
                .ConfigureAwait(false);
            if (related.Count > 0)
            {
                var limited = related.Take(maxImplementations).Select(r => new
                {
                    kind = r.Kind,
                    relationship = r.Relationship,
                    symbol_display = r.SymbolDisplay,
                    file_path = r.FilePath,
                    line = r.Line,
                    column = r.Column,
                }).ToArray();

                implementationsPayload = new
                {
                    total_count = related.Count,
                    returned = limited.Length,
                    truncated = related.Count > maxImplementations,
                    items = limited,
                };
            }
        }

        // 5. Declaring document compiler diagnostics
        object? diagnosticsPayload = null;
        if (includeDiagnostics && maxDiagnostics > 0)
        {
            IReadOnlyList<Diagnostic>? docDiagnostics = null;
            if (solution is not null && declTree is not null)
            {
                Document? declDoc = solution.GetDocument(declTree);
                if (declDoc is not null)
                {
                    SemanticModel? model = await declDoc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    if (model is not null)
                    {
                        docDiagnostics = model.GetDiagnostics(cancellationToken: cancellationToken)
                            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                            .ToArray();
                    }
                }
            }
            else if (fileAnalysis is not null)
            {
                docDiagnostics = fileAnalysis.SemanticModel.GetDiagnostics(cancellationToken: cancellationToken)
                    .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    .ToArray();
            }

            if (docDiagnostics is not null)
            {
                NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(docDiagnostics);
                int errorCount = normalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
                int warningCount = normalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));
                diagnosticsPayload = new
                {
                    file_path = declFilePath,
                    total = normalized.Length,
                    errors = errorCount,
                    warnings = warningCount,
                    truncated = normalized.Length > maxDiagnostics,
                    items = normalized.Take(maxDiagnostics).ToArray(),
                };
            }
        }

        string? xmlSummary = ExtractXmlDocSummary(targetSymbol);

        object data = new
        {
            text_source = textSource,
            symbol = new
            {
                symbol_display = targetSymbol.ToDisplayString(),
                qualified_name = SymbolQueryResolution.BuildDottedSymbolName(targetSymbol),
                symbol_kind = targetSymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(targetSymbol),
                accessibility = targetSymbol.DeclaredAccessibility.ToString(),
                containing_type = targetSymbol.ContainingType?.ToDisplayString(),
                containing_namespace = targetSymbol.ContainingNamespace?.IsGlobalNamespace == true
                    ? null
                    : targetSymbol.ContainingNamespace?.ToDisplayString(),
                xml_summary = xmlSummary,
                file_path = declFilePath,
                file_sha256 = fileSha256,
                line = declLine,
                column = declColumn,
                end_line = declEndLine,
            },
            definition_source = definitionSourcePayload,
            references = referencesPayload,
            callers = callersPayload,
            callees = calleesPayload,
            implementations = implementationsPayload,
            diagnostics = diagnosticsPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static string SingleLinePreview(string? snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return string.Empty;
        }

        string trimmed = snippet.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= 180 ? trimmed : trimmed[..177] + "...";
    }

    private static string? ExtractXmlDocSummary(ISymbol symbol)
    {
        string? xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            XDocument doc = XDocument.Parse($"<root>{xml}</root>");
            string? summary = doc.Descendants("summary").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }

            string normalized = string.Join(" ", summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 400 ? normalized : normalized[..397] + "...";
        }
        catch
        {
            return null;
        }
    }
}
