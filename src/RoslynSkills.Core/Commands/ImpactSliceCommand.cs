using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class ImpactSliceCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "analyze.impact_slice",
        Summary: "Build a bounded impact slice for an anchored symbol (references/callers/callees/overrides/implementations).",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: [CommandTrait.Heuristic, CommandTrait.DerivedAnalysis, CommandTrait.PotentiallySlow]);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalBool(input, "include_references", errors);
        InputParsing.ValidateOptionalBool(input, "include_callers", errors);
        InputParsing.ValidateOptionalBool(input, "include_callees", errors);
        InputParsing.ValidateOptionalBool(input, "include_overrides", errors);
        InputParsing.ValidateOptionalBool(input, "include_implementations", errors);
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
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) ||
            !InputParsing.TryGetRequiredInt(input, "line", errors, out int line, minValue: 1, maxValue: 1_000_000) ||
            !InputParsing.TryGetRequiredInt(input, "column", errors, out int column, minValue: 1, maxValue: 1_000_000))
        {
            return new CommandExecutionResult(null, errors);
        }

        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        bool includeReferences = InputParsing.GetOptionalBool(input, "include_references", defaultValue: true);
        bool includeCallers = InputParsing.GetOptionalBool(input, "include_callers", defaultValue: true);
        bool includeCallees = InputParsing.GetOptionalBool(input, "include_callees", defaultValue: true);
        bool includeOverrides = InputParsing.GetOptionalBool(input, "include_overrides", defaultValue: true);
        bool includeImplementations = InputParsing.GetOptionalBool(input, "include_implementations", defaultValue: true);
        int maxReferences = InputParsing.GetOptionalInt(input, "max_references", defaultValue: 200, minValue: 1, maxValue: 10_000);
        int maxCallers = InputParsing.GetOptionalInt(input, "max_callers", defaultValue: 200, minValue: 1, maxValue: 10_000);
        int maxCallees = InputParsing.GetOptionalInt(input, "max_callees", defaultValue: 200, minValue: 1, maxValue: 10_000);
        int maxRelated = InputParsing.GetOptionalInt(input, "max_related", defaultValue: 200, minValue: 1, maxValue: 10_000);
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
        if (anchorSymbol is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", "Unable to resolve a semantic symbol at the provided location.") });
        }

        HashSet<string> declarationKeys = anchorSymbol.Locations
            .Where(location => location.IsInSource && location.SourceTree is not null && !string.IsNullOrWhiteSpace(location.SourceTree.FilePath))
            .Select(location => BuildLocationKey(Path.GetFullPath(location.SourceTree!.FilePath), location.SourceSpan.Start))
            .ToHashSet(StringComparer.Ordinal);

        List<ImpactReference> references = new();
        List<ImpactCaller> callers = new();
        List<ImpactCallee> callees = new();
        List<ImpactRelatedSymbol> overrides = new();
        List<ImpactRelatedSymbol> implementations = new();

        if (includeReferences || includeCallers || includeOverrides || includeImplementations)
        {
            foreach (SyntaxTree tree in analysis.Compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SemanticModel semanticModel = analysis.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                string treeFilePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);

                if (includeReferences && references.Count < maxReferences)
                {
                    foreach (SyntaxNode node in EnumerateReferenceNodes(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (references.Count >= maxReferences)
                        {
                            break;
                        }

                        ISymbol? resolved = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol
                            ?? semanticModel.GetSymbolInfo(node, cancellationToken).CandidateSymbols.FirstOrDefault();
                        if (!MatchesAnchor(resolved, anchorSymbol))
                        {
                            continue;
                        }

                        string key = BuildLocationKey(treeFilePath, node.SpanStart);
                        if (declarationKeys.Contains(key))
                        {
                            continue;
                        }

                        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
                        references.Add(new ImpactReference(
                            file_path: treeFilePath,
                            line: lineSpan.StartLinePosition.Line + 1,
                            column: lineSpan.StartLinePosition.Character + 1,
                            symbol_display: resolved!.ToDisplayString()));
                    }
                }

                if (includeCallers &&
                    anchorSymbol is IMethodSymbol anchorMethod &&
                    callers.Count < maxCallers)
                {
                    foreach (InvocationExpressionSyntax invocation in root.DescendantNodes(descendIntoTrivia: false).OfType<InvocationExpressionSyntax>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (callers.Count >= maxCallers)
                        {
                            break;
                        }

                        IMethodSymbol? callee = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol
                            ?? semanticModel.GetSymbolInfo(invocation, cancellationToken).CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                        if (!MatchesAnchor(callee, anchorMethod))
                        {
                            continue;
                        }

                        IMethodSymbol? caller = semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken) as IMethodSymbol;
                        FileLinePositionSpan lineSpan = invocation.GetLocation().GetLineSpan();
                        callers.Add(new ImpactCaller(
                            caller_symbol: caller?.ToDisplayString() ?? "<unknown>",
                            file_path: treeFilePath,
                            line: lineSpan.StartLinePosition.Line + 1,
                            column: lineSpan.StartLinePosition.Character + 1));
                    }

                    foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes(descendIntoTrivia: false).OfType<ObjectCreationExpressionSyntax>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (callers.Count >= maxCallers)
                        {
                            break;
                        }

                        IMethodSymbol? callee = ResolveInvokedMethodSymbol(semanticModel, creation, cancellationToken);
                        if (!MatchesAnchor(callee, anchorMethod))
                        {
                            continue;
                        }

                        IMethodSymbol? caller = semanticModel.GetEnclosingSymbol(creation.SpanStart, cancellationToken) as IMethodSymbol;
                        FileLinePositionSpan lineSpan = creation.GetLocation().GetLineSpan();
                        callers.Add(new ImpactCaller(
                            caller_symbol: caller?.ToDisplayString() ?? "<unknown>",
                            file_path: treeFilePath,
                            line: lineSpan.StartLinePosition.Line + 1,
                            column: lineSpan.StartLinePosition.Character + 1));
                    }

                    foreach (ImplicitObjectCreationExpressionSyntax creation in root.DescendantNodes(descendIntoTrivia: false).OfType<ImplicitObjectCreationExpressionSyntax>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (callers.Count >= maxCallers)
                        {
                            break;
                        }

                        IMethodSymbol? callee = ResolveInvokedMethodSymbol(semanticModel, creation, cancellationToken);
                        if (!MatchesAnchor(callee, anchorMethod))
                        {
                            continue;
                        }

                        IMethodSymbol? caller = semanticModel.GetEnclosingSymbol(creation.SpanStart, cancellationToken) as IMethodSymbol;
                        FileLinePositionSpan lineSpan = creation.GetLocation().GetLineSpan();
                        callers.Add(new ImpactCaller(
                            caller_symbol: caller?.ToDisplayString() ?? "<unknown>",
                            file_path: treeFilePath,
                            line: lineSpan.StartLinePosition.Line + 1,
                            column: lineSpan.StartLinePosition.Character + 1));
                    }

                    foreach (ConstructorInitializerSyntax initializer in root.DescendantNodes(descendIntoTrivia: false).OfType<ConstructorInitializerSyntax>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (callers.Count >= maxCallers)
                        {
                            break;
                        }

                        IMethodSymbol? callee = ResolveInvokedMethodSymbol(semanticModel, initializer, cancellationToken);
                        if (!MatchesAnchor(callee, anchorMethod))
                        {
                            continue;
                        }

                        IMethodSymbol? caller = semanticModel.GetEnclosingSymbol(initializer.SpanStart, cancellationToken) as IMethodSymbol;
                        FileLinePositionSpan lineSpan = initializer.GetLocation().GetLineSpan();
                        callers.Add(new ImpactCaller(
                            caller_symbol: caller?.ToDisplayString() ?? "<unknown>",
                            file_path: treeFilePath,
                            line: lineSpan.StartLinePosition.Line + 1,
                            column: lineSpan.StartLinePosition.Character + 1));
                    }
                }

                if (includeOverrides && overrides.Count < maxRelated)
                {
                    AddOverrideMatches(anchorSymbol, root, semanticModel, treeFilePath, overrides, maxRelated, cancellationToken);
                }

                if (includeImplementations && implementations.Count < maxRelated)
                {
                    AddImplementationMatches(anchorSymbol, root, semanticModel, treeFilePath, implementations, maxRelated, cancellationToken);
                }
            }
        }

        if (includeCallees && anchorSymbol is IMethodSymbol anchorMethodSymbol)
        {
            AddCalleeMatches(anchorMethodSymbol, analysis.Compilation, callees, maxCallees, cancellationToken);
        }

        object referencesPayload = brief
            ? references.Select(value => new { value.file_path, value.line, value.column, value.symbol_display }).ToArray()
            : references.ToArray();
        object callersPayload = brief
            ? callers.Select(value => new { value.caller_symbol, value.file_path, value.line, value.column }).ToArray()
            : callers.ToArray();
        object calleesPayload = brief
            ? callees.Select(value => new { value.callee_symbol, value.file_path, value.line, value.column }).ToArray()
            : callees.ToArray();
        object overridesPayload = brief
            ? overrides.Select(value => new { value.symbol_display, value.file_path, value.line, value.column }).ToArray()
            : overrides.ToArray();
        object implementationsPayload = brief
            ? implementations.Select(value => new { value.symbol_display, value.file_path, value.line, value.column }).ToArray()
            : implementations.ToArray();

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                brief,
                include_references = includeReferences,
                include_callers = includeCallers,
                include_callees = includeCallees,
                include_overrides = includeOverrides,
                include_implementations = includeImplementations,
                max_references = maxReferences,
                max_callers = maxCallers,
                max_callees = maxCallees,
                max_related = maxRelated,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            anchor_symbol = new
            {
                symbol_display = anchorSymbol.ToDisplayString(),
                symbol_kind = anchorSymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(anchorSymbol),
            },
            impact_counts = new
            {
                references = references.Count,
                callers = callers.Count,
                callees = callees.Count,
                overrides = overrides.Count,
                implementations = implementations.Count,
                total = references.Count + callers.Count + callees.Count + overrides.Count + implementations.Count,
            },
            caveats = new[]
            {
                "Impact slice is heuristic and can miss dynamic dispatch, reflection, and source-generated edges.",
            },
            references = referencesPayload,
            callers = callersPayload,
            callees = calleesPayload,
            overrides = overridesPayload,
            implementations = implementationsPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static IEnumerable<SyntaxNode> EnumerateReferenceNodes(SyntaxNode root)
    {
        foreach (IdentifierNameSyntax node in root.DescendantNodes(descendIntoTrivia: false).OfType<IdentifierNameSyntax>())
        {
            yield return node;
        }

        foreach (GenericNameSyntax node in root.DescendantNodes(descendIntoTrivia: false).OfType<GenericNameSyntax>())
        {
            yield return node;
        }

        foreach (ObjectCreationExpressionSyntax node in root.DescendantNodes(descendIntoTrivia: false).OfType<ObjectCreationExpressionSyntax>())
        {
            yield return node;
        }

        foreach (ImplicitObjectCreationExpressionSyntax node in root.DescendantNodes(descendIntoTrivia: false).OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            yield return node;
        }

        foreach (ConstructorInitializerSyntax node in root.DescendantNodes(descendIntoTrivia: false).OfType<ConstructorInitializerSyntax>())
        {
            yield return node;
        }
    }

    private static void AddCalleeMatches(
        IMethodSymbol anchorMethod,
        Compilation compilation,
        List<ImpactCallee> results,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!anchorMethod.Locations.Any(location => location.IsInSource))
        {
            return;
        }

        Location location = anchorMethod.Locations.First(location => location.IsInSource);
        SyntaxTree? tree = location.SourceTree;
        if (tree is null)
        {
            return;
        }

        SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        SyntaxNode root = tree.GetRoot(cancellationToken);
        SyntaxNode? node = root.FindNode(location.SourceSpan)
            .AncestorsAndSelf()
            .FirstOrDefault(candidate => candidate is MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax);
        if (node is null)
        {
            return;
        }

        string treeFilePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);
        foreach (InvocationExpressionSyntax invocation in node.DescendantNodes(descendIntoTrivia: false).OfType<InvocationExpressionSyntax>())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            IMethodSymbol? callee = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol
                ?? semanticModel.GetSymbolInfo(invocation, cancellationToken).CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (callee is null)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = invocation.GetLocation().GetLineSpan();
            results.Add(new ImpactCallee(
                callee_symbol: callee.ToDisplayString(),
                file_path: treeFilePath,
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1));
        }

        foreach (ObjectCreationExpressionSyntax creation in node.DescendantNodes(descendIntoTrivia: false).OfType<ObjectCreationExpressionSyntax>())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            IMethodSymbol? callee = ResolveInvokedMethodSymbol(semanticModel, creation, cancellationToken);
            if (callee is null)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = creation.GetLocation().GetLineSpan();
            results.Add(new ImpactCallee(
                callee_symbol: callee.ToDisplayString(),
                file_path: treeFilePath,
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1));
        }

        foreach (ImplicitObjectCreationExpressionSyntax creation in node.DescendantNodes(descendIntoTrivia: false).OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            IMethodSymbol? callee = ResolveInvokedMethodSymbol(semanticModel, creation, cancellationToken);
            if (callee is null)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = creation.GetLocation().GetLineSpan();
            results.Add(new ImpactCallee(
                callee_symbol: callee.ToDisplayString(),
                file_path: treeFilePath,
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1));
        }

        foreach (ConstructorInitializerSyntax initializer in node.DescendantNodes(descendIntoTrivia: false).OfType<ConstructorInitializerSyntax>())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            IMethodSymbol? callee = ResolveInvokedMethodSymbol(semanticModel, initializer, cancellationToken);
            if (callee is null)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = initializer.GetLocation().GetLineSpan();
            results.Add(new ImpactCallee(
                callee_symbol: callee.ToDisplayString(),
                file_path: treeFilePath,
                line: lineSpan.StartLinePosition.Line + 1,
                column: lineSpan.StartLinePosition.Character + 1));
        }
    }

    private static IMethodSymbol? ResolveInvokedMethodSymbol(
        SemanticModel semanticModel,
        SyntaxNode callNode,
        CancellationToken cancellationToken)
    {
        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(callNode, cancellationToken);
        return symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static void AddOverrideMatches(
        ISymbol anchorSymbol,
        SyntaxNode root,
        SemanticModel semanticModel,
        string treeFilePath,
        List<ImpactRelatedSymbol> results,
        int maxResults,
        CancellationToken cancellationToken)
    {
        switch (anchorSymbol)
        {
            case IMethodSymbol methodSymbol:
            {
                IMethodSymbol baseMethod = methodSymbol.OverriddenMethod ?? methodSymbol;
                foreach (MethodDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<MethodDeclarationSyntax>())
                {
                    if (results.Count >= maxResults)
                    {
                        break;
                    }

                    IMethodSymbol? candidate = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                    if (candidate?.OverriddenMethod is null)
                    {
                        continue;
                    }

                    if (!MatchesAnchor(candidate.OverriddenMethod, baseMethod))
                    {
                        continue;
                    }

                    FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
                    results.Add(new ImpactRelatedSymbol(
                        symbol_display: candidate.ToDisplayString(),
                        file_path: treeFilePath,
                        line: lineSpan.StartLinePosition.Line + 1,
                        column: lineSpan.StartLinePosition.Character + 1));
                }

                break;
            }
            case IPropertySymbol propertySymbol:
            {
                IPropertySymbol baseProperty = propertySymbol.OverriddenProperty ?? propertySymbol;
                foreach (PropertyDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<PropertyDeclarationSyntax>())
                {
                    if (results.Count >= maxResults)
                    {
                        break;
                    }

                    IPropertySymbol? candidate = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                    if (candidate?.OverriddenProperty is null)
                    {
                        continue;
                    }

                    if (!MatchesAnchor(candidate.OverriddenProperty, baseProperty))
                    {
                        continue;
                    }

                    FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
                    results.Add(new ImpactRelatedSymbol(
                        symbol_display: candidate.ToDisplayString(),
                        file_path: treeFilePath,
                        line: lineSpan.StartLinePosition.Line + 1,
                        column: lineSpan.StartLinePosition.Character + 1));
                }

                break;
            }
            case IEventSymbol eventSymbol:
            {
                IEventSymbol baseEvent = eventSymbol.OverriddenEvent ?? eventSymbol;
                foreach (EventDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<EventDeclarationSyntax>())
                {
                    if (results.Count >= maxResults)
                    {
                        break;
                    }

                    IEventSymbol? candidate = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                    if (candidate?.OverriddenEvent is null)
                    {
                        continue;
                    }

                    if (!MatchesAnchor(candidate.OverriddenEvent, baseEvent))
                    {
                        continue;
                    }

                    FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
                    results.Add(new ImpactRelatedSymbol(
                        symbol_display: candidate.ToDisplayString(),
                        file_path: treeFilePath,
                        line: lineSpan.StartLinePosition.Line + 1,
                        column: lineSpan.StartLinePosition.Character + 1));
                }

                break;
            }
        }
    }

    private static void AddImplementationMatches(
        ISymbol anchorSymbol,
        SyntaxNode root,
        SemanticModel semanticModel,
        string treeFilePath,
        List<ImpactRelatedSymbol> results,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (anchorSymbol is INamedTypeSymbol interfaceType && interfaceType.TypeKind == TypeKind.Interface)
        {
            foreach (BaseTypeDeclarationSyntax typeDeclaration in root.DescendantNodes(descendIntoTrivia: false).OfType<BaseTypeDeclarationSyntax>())
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                INamedTypeSymbol? candidateType = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken);
                if (candidateType is null)
                {
                    continue;
                }

                if (!candidateType.AllInterfaces.Any(value => MatchesAnchor(value, interfaceType)))
                {
                    continue;
                }

                FileLinePositionSpan lineSpan = typeDeclaration.GetLocation().GetLineSpan();
                results.Add(new ImpactRelatedSymbol(
                    symbol_display: candidateType.ToDisplayString(),
                    file_path: treeFilePath,
                    line: lineSpan.StartLinePosition.Line + 1,
                    column: lineSpan.StartLinePosition.Character + 1));
            }
        }

        if (anchorSymbol is IMethodSymbol interfaceMember &&
            interfaceMember.ContainingType.TypeKind == TypeKind.Interface)
        {
            foreach (MethodDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<MethodDeclarationSyntax>())
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                IMethodSymbol? candidate = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                if (candidate is null)
                {
                    continue;
                }

                if (!candidate.ExplicitInterfaceImplementations.Any(value => MatchesAnchor(value, interfaceMember)))
                {
                    IMethodSymbol? implementation = candidate.ContainingType.FindImplementationForInterfaceMember(interfaceMember) as IMethodSymbol;
                    if (!MatchesAnchor(implementation, candidate))
                    {
                        continue;
                    }
                }

                FileLinePositionSpan lineSpan = declaration.GetLocation().GetLineSpan();
                results.Add(new ImpactRelatedSymbol(
                    symbol_display: candidate.ToDisplayString(),
                    file_path: treeFilePath,
                    line: lineSpan.StartLinePosition.Line + 1,
                    column: lineSpan.StartLinePosition.Character + 1));
            }
        }
    }

    private static bool MatchesAnchor(ISymbol? resolved, ISymbol? anchor)
    {
        if (resolved is null || anchor is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(resolved, anchor))
        {
            return true;
        }

        return SymbolEqualityComparer.Default.Equals(resolved.OriginalDefinition, anchor.OriginalDefinition);
    }

    private static string BuildLocationKey(string filePath, int spanStart)
        => $"{Path.GetFullPath(filePath)}::{spanStart}";

    private sealed record ImpactReference(
        string file_path,
        int line,
        int column,
        string symbol_display);

    private sealed record ImpactCaller(
        string caller_symbol,
        string file_path,
        int line,
        int column);

    private sealed record ImpactCallee(
        string callee_symbol,
        string file_path,
        int line,
        int column);

    private sealed record ImpactRelatedSymbol(
        string symbol_display,
        string file_path,
        int line,
        int column);
}
