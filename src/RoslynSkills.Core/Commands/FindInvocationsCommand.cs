using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class FindInvocationsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.find_invocations",
        Summary: "Find method call sites anchored by line/column with semantic matching across the active compilation.",
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

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalBool(input, "include_object_creations", errors);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError(
                "file_not_found",
                $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) ||
            !InputParsing.TryGetRequiredInt(input, "line", errors, out int line, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "column", errors, out int column, minValue: 1, maxValue: 1_000_000))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 200, minValue: 1, maxValue: 10_000);
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 1, minValue: 0, maxValue: 20);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        bool includeObjectCreations = InputParsing.GetOptionalBool(input, "include_object_creations", defaultValue: true);
        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken, workspacePath).ConfigureAwait(false);
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
        ISymbol? anchorSymbol = SymbolResolution.GetSymbolForToken(anchorToken, analysis.SemanticModel, cancellationToken);
        IMethodSymbol? targetMethod = anchorSymbol as IMethodSymbol;

        if (targetMethod is null)
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError(
                        "invalid_target",
                        $"Symbol '{anchorSymbol?.ToDisplayString() ?? "<unknown>"}' is not a method. Anchor to a method declaration/reference and retry."),
                });
        }

        List<InvocationMatch> matches = new();
        bool truncated = false;
        int searchedSyntaxTreeCount = 0;

        foreach (SyntaxTree tree in analysis.Compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string treeFilePath = string.IsNullOrWhiteSpace(tree.FilePath)
                ? analysis.FilePath
                : Path.GetFullPath(tree.FilePath);

            if (!includeGenerated && CommandFileFilters.IsGeneratedPath(treeFilePath))
            {
                continue;
            }

            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SourceText sourceText = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel semanticModel = analysis.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            searchedSyntaxTreeCount++;

            foreach (CallSiteAnalysis.CallSite callSite in CallSiteAnalysis.EnumerateCallSites(
                         semanticModel,
                         root,
                         includeObjectCreations,
                         cancellationToken))
            {
                if (!IsSameTargetMethod(callSite.callee, targetMethod))
                {
                    continue;
                }

                AddMatch(
                    matches,
                    callSite.node.Span,
                    callSite.callee,
                    treeFilePath,
                    sourceText,
                    contextLines,
                    maxResults,
                    brief,
                    callSite.call_kind);

                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        object matchesPayload = brief
            ? matches.Select(match => new
            {
                match.file_path,
                match.line,
                match.column,
                match.call_kind,
                match.symbol_display,
            }).ToArray()
            : matches;

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                max_results = maxResults,
                context_lines = contextLines,
                include_object_creations = includeObjectCreations,
                include_generated = includeGenerated,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                brief,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            symbol = new
            {
                symbol_display = targetMethod.ToDisplayString(),
                symbol_kind = targetMethod.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(targetMethod),
                method_kind = targetMethod.MethodKind.ToString(),
            },
            searched_syntax_trees = searchedSyntaxTreeCount,
            total_matches = matches.Count,
            truncated,
            matches = matchesPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static void AddMatch(
        List<InvocationMatch> matches,
        TextSpan span,
        IMethodSymbol calledMethod,
        string filePath,
        SourceText sourceText,
        int contextLines,
        int maxResults,
        bool brief,
        string callKind)
    {
        if (matches.Count >= maxResults)
        {
            return;
        }

        LinePositionSpan lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        int line = lineSpan.Start.Line + 1;
        int column = lineSpan.Start.Character + 1;
        string snippet = brief
            ? string.Empty
            : CommandTextFormatting.BuildSnippet(sourceText, line, contextLines);

        matches.Add(new InvocationMatch(
            file_path: filePath,
            line: line,
            column: column,
            call_kind: callKind,
            symbol_display: calledMethod.ToDisplayString(),
            snippet: snippet));
    }

    private static bool IsSameTargetMethod(IMethodSymbol? candidate, IMethodSymbol target)
    {
        if (candidate is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(candidate, target) ||
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition))
        {
            return true;
        }

        IMethodSymbol? candidateReduced = candidate.ReducedFrom;
        IMethodSymbol? targetReduced = target.ReducedFrom;
        if (candidateReduced is not null &&
            (SymbolEqualityComparer.Default.Equals(candidateReduced, target) ||
             SymbolEqualityComparer.Default.Equals(candidateReduced.OriginalDefinition, target.OriginalDefinition)))
        {
            return true;
        }

        if (targetReduced is not null &&
            (SymbolEqualityComparer.Default.Equals(candidate, targetReduced) ||
             SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, targetReduced.OriginalDefinition)))
        {
            return true;
        }

        return false;
    }

    private sealed record InvocationMatch(
        string file_path,
        int line,
        int column,
        string call_kind,
        string symbol_display,
        string snippet);
}
