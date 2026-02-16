using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class AsyncRiskScanCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "analyze.async_risk_scan",
        Summary: "Scan for common async/sync-mixing risk patterns (heuristic).",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Experimental,
        Traits: [CommandTrait.Heuristic, CommandTrait.DerivedAnalysis, CommandTrait.PotentiallySlow]);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "workspace_path", errors, out string workspacePath))
        {
            return errors;
        }

        InputParsing.ValidateOptionalBool(input, "include_generated", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);

        if (input.TryGetProperty("severity_filter", out JsonElement severityFilter) && severityFilter.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'severity_filter' must be an array when provided."));
        }

        if (!File.Exists(workspacePath) && !Directory.Exists(workspacePath))
        {
            errors.Add(new CommandError("workspace_not_found", $"Workspace path '{workspacePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "workspace_path", errors, out string workspacePath))
        {
            return new CommandExecutionResult(null, errors);
        }

        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        int maxFiles = InputParsing.GetOptionalInt(input, "max_files", defaultValue: 2_000, minValue: 1, maxValue: 50_000);
        int maxFindings = InputParsing.GetOptionalInt(input, "max_findings", defaultValue: 2_000, minValue: 1, maxValue: 100_000);
        HashSet<string> severityFilter = ParseSeverityFilter(input);

        (StaticAnalysisWorkspace? Workspace, CommandError? Error) loadResult = await StaticAnalysisWorkspace
            .LoadAsync(workspacePath, includeGenerated, maxFiles, cancellationToken)
            .ConfigureAwait(false);
        if (loadResult.Error is not null || loadResult.Workspace is null)
        {
            return new CommandExecutionResult(null, new[] { loadResult.Error ?? new CommandError("analysis_failed", "Failed to load workspace for analysis.") });
        }

        StaticAnalysisWorkspace workspace = loadResult.Workspace;
        List<AsyncRiskFinding> findings = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool truncated = false;

        foreach (SyntaxTree tree in workspace.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticModel semanticModel = workspace.SemanticModelsByTree[tree];
            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            string filePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);

            foreach (MethodDeclarationSyntax method in root.DescendantNodes(descendIntoTrivia: false).OfType<MethodDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (findings.Count >= maxFindings)
                {
                    truncated = true;
                    break;
                }

                if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
                {
                    continue;
                }

                if (!IsVoidType(method.ReturnType))
                {
                    continue;
                }

                AddFinding(
                    findings,
                    seen,
                    severityFilter,
                    maxFindings,
                    new AsyncRiskFinding(
                        rule_id: "async_void",
                        severity: "warning",
                        message: "Avoid async void except for UI/event handlers; exceptions are hard to observe.",
                        file_path: filePath,
                        line: method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        column: method.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
                        member_symbol: (semanticModel.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol)?.ToDisplayString() ?? method.Identifier.Text));
            }

            if (truncated)
            {
                break;
            }

            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes(descendIntoTrivia: false).OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (findings.Count >= maxFindings)
                {
                    truncated = true;
                    break;
                }

                IMethodSymbol? method = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol
                    ?? semanticModel.GetSymbolInfo(invocation, cancellationToken).CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                if (method is null)
                {
                    continue;
                }

                string ruleId;
                string severity;
                string message;

                if (IsThreadSleep(method))
                {
                    ruleId = "thread_sleep";
                    severity = "warning";
                    message = "Thread.Sleep blocks threads; prefer Task.Delay in async flows.";
                }
                else if (IsTaskWait(method))
                {
                    ruleId = "task_wait";
                    severity = "warning";
                    message = "Task.Wait can deadlock and block thread-pool threads.";
                }
                else if (IsGetAwaiterGetResult(invocation, method))
                {
                    ruleId = "awaiter_get_result";
                    severity = "warning";
                    message = "GetAwaiter().GetResult() is a synchronous wait pattern with deadlock risk.";
                }
                else if (IsFireAndForgetInvocation(invocation, method))
                {
                    ruleId = "unobserved_task";
                    severity = "info";
                    message = "Task-returning call is not awaited or captured; verify fire-and-forget intent.";
                }
                else
                {
                    continue;
                }

                FileLinePositionSpan lineSpan = invocation.GetLocation().GetLineSpan();
                AddFinding(
                    findings,
                    seen,
                    severityFilter,
                    maxFindings,
                    new AsyncRiskFinding(
                        rule_id: ruleId,
                        severity: severity,
                        message: message,
                        file_path: filePath,
                        line: lineSpan.StartLinePosition.Line + 1,
                        column: lineSpan.StartLinePosition.Character + 1,
                        member_symbol: (semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken) as IMethodSymbol)?.ToDisplayString() ?? "<unknown>"));
            }

            if (truncated)
            {
                break;
            }

            foreach (MemberAccessExpressionSyntax memberAccess in root.DescendantNodes(descendIntoTrivia: false).OfType<MemberAccessExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (findings.Count >= maxFindings)
                {
                    truncated = true;
                    break;
                }

                if (!string.Equals(memberAccess.Name.Identifier.ValueText, "Result", StringComparison.Ordinal))
                {
                    continue;
                }

                IPropertySymbol? property = semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol as IPropertySymbol;
                if (property is null || !IsTaskLike(property.ContainingType))
                {
                    continue;
                }

                FileLinePositionSpan lineSpan = memberAccess.GetLocation().GetLineSpan();
                AddFinding(
                    findings,
                    seen,
                    severityFilter,
                    maxFindings,
                    new AsyncRiskFinding(
                        rule_id: "task_result",
                        severity: "warning",
                        message: "Task.Result is a synchronous wait pattern with deadlock risk.",
                        file_path: filePath,
                        line: lineSpan.StartLinePosition.Line + 1,
                        column: lineSpan.StartLinePosition.Character + 1,
                        member_symbol: (semanticModel.GetEnclosingSymbol(memberAccess.SpanStart, cancellationToken) as IMethodSymbol)?.ToDisplayString() ?? "<unknown>"));
            }
        }

        AsyncRiskFinding[] ordered = findings
            .OrderByDescending(value => SeverityRank(value.severity))
            .ThenBy(value => value.file_path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.line)
            .ThenBy(value => value.column)
            .ToArray();

        object findingPayload = brief
            ? ordered.Select(value => new
            {
                value.rule_id,
                value.severity,
                value.file_path,
                value.line,
                value.column,
                value.member_symbol,
            }).ToArray()
            : ordered;

        object summary = new
        {
            total_findings = ordered.Length,
            warnings = ordered.Count(value => string.Equals(value.severity, "warning", StringComparison.OrdinalIgnoreCase)),
            info = ordered.Count(value => string.Equals(value.severity, "info", StringComparison.OrdinalIgnoreCase)),
            by_rule = ordered
                .GroupBy(value => value.rule_id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { rule_id = group.Key, count = group.Count() })
                .ToArray(),
        };

        object data = new
        {
            query = new
            {
                workspace_path = Path.GetFullPath(workspacePath),
                include_generated = includeGenerated,
                max_files = maxFiles,
                max_findings = maxFindings,
                severity_filter = severityFilter.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                brief,
            },
            analysis_scope = new
            {
                root_directory = workspace.RootDirectory,
                files_scanned = workspace.SyntaxTrees.Count,
                truncated,
            },
            caveats = new[]
            {
                "Async risk scan is heuristic and prioritizes high-signal patterns.",
                "Some patterns can be intentional; review findings before changing behavior.",
            },
            summary,
            findings = findingPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool IsVoidType(TypeSyntax returnType)
        => returnType is PredefinedTypeSyntax predefined &&
           predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static HashSet<string> ParseSeverityFilter(JsonElement input)
    {
        if (!input.TryGetProperty("severity_filter", out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(new[] { "warning", "info" }, StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> filter = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string value = (element.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            filter.Add(value.ToLowerInvariant());
        }

        return filter.Count == 0
            ? new HashSet<string>(new[] { "warning", "info" }, StringComparer.OrdinalIgnoreCase)
            : filter;
    }

    private static void AddFinding(
        ICollection<AsyncRiskFinding> findings,
        ISet<string> seen,
        ISet<string> severityFilter,
        int maxFindings,
        AsyncRiskFinding finding)
    {
        if (!severityFilter.Contains(finding.severity))
        {
            return;
        }

        if (findings.Count >= maxFindings)
        {
            return;
        }

        string key = $"{finding.rule_id}|{finding.file_path}|{finding.line}|{finding.column}";
        if (!seen.Add(key))
        {
            return;
        }

        findings.Add(finding);
    }

    private static bool IsThreadSleep(IMethodSymbol method)
        => string.Equals(method.Name, "Sleep", StringComparison.Ordinal) &&
           string.Equals(method.ContainingType?.ToDisplayString(), "System.Threading.Thread", StringComparison.Ordinal);

    private static bool IsTaskWait(IMethodSymbol method)
        => string.Equals(method.Name, "Wait", StringComparison.Ordinal) &&
           IsTaskLike(method.ContainingType);

    private static bool IsGetAwaiterGetResult(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        if (!string.Equals(method.Name, "GetResult", StringComparison.Ordinal))
        {
            return false;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax outerMember)
        {
            return false;
        }

        if (outerMember.Expression is not InvocationExpressionSyntax innerInvocation)
        {
            return false;
        }

        if (innerInvocation.Expression is not MemberAccessExpressionSyntax innerMember)
        {
            return false;
        }

        return string.Equals(innerMember.Name.Identifier.ValueText, "GetAwaiter", StringComparison.Ordinal);
    }

    private static bool IsFireAndForgetInvocation(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        if (!IsTaskLike(method.ReturnType))
        {
            return false;
        }

        if (invocation.Parent is not ExpressionStatementSyntax)
        {
            return false;
        }

        if (string.Equals(method.Name, "Forget", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method.Name, "SafeFireAndForget", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsTaskLike(ITypeSymbol? type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if ((string.Equals(current.Name, "Task", StringComparison.Ordinal) ||
                 string.Equals(current.Name, "ValueTask", StringComparison.Ordinal)) &&
                string.Equals(current.ContainingNamespace?.ToDisplayString(), "System.Threading.Tasks", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int SeverityRank(string severity)
        => string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private sealed record AsyncRiskFinding(
        string rule_id,
        string severity,
        string message,
        string file_path,
        int line,
        int column,
        string member_symbol);
}
