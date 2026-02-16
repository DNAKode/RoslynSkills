using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class DependencyViolationsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "analyze.dependency_violations",
        Summary: "Detect namespace-layer dependency violations from ordered layer rules.",
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
        InputParsing.ValidateOptionalBool(input, "ignore_same_namespace", errors);
        ValidateLayers(input, errors);

        if (!TryGetDirection(input, out _))
        {
            errors.Add(new CommandError("invalid_input", "Property 'direction' must be 'toward_end' or 'toward_start'."));
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

        string[] layers = GetLayers(input);
        if (layers.Length < 2)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'layers' must contain at least two namespace prefixes.") });
        }

        if (!TryGetDirection(input, out DependencyDirection direction))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'direction' must be 'toward_end' or 'toward_start'.") });
        }

        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        bool ignoreSameNamespace = InputParsing.GetOptionalBool(input, "ignore_same_namespace", defaultValue: true);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        int maxFiles = InputParsing.GetOptionalInt(input, "max_files", defaultValue: 2_000, minValue: 1, maxValue: 50_000);
        int maxViolations = InputParsing.GetOptionalInt(input, "max_violations", defaultValue: 2_000, minValue: 1, maxValue: 100_000);

        (StaticAnalysisWorkspace? Workspace, CommandError? Error) loadResult = await StaticAnalysisWorkspace
            .LoadAsync(workspacePath, includeGenerated, maxFiles, cancellationToken)
            .ConfigureAwait(false);
        if (loadResult.Error is not null || loadResult.Workspace is null)
        {
            return new CommandExecutionResult(null, new[] { loadResult.Error ?? new CommandError("analysis_failed", "Failed to load workspace for analysis.") });
        }

        StaticAnalysisWorkspace workspace = loadResult.Workspace;
        List<DependencyViolation> violations = new();
        HashSet<string> seenViolations = new(StringComparer.Ordinal);
        bool truncated = false;

        foreach (SyntaxTree tree in workspace.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel semanticModel = workspace.SemanticModelsByTree[tree];
            string filePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);

            foreach (BaseTypeDeclarationSyntax typeDeclaration in root.DescendantNodes(descendIntoTrivia: false).OfType<BaseTypeDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                INamedTypeSymbol? fromType = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
                if (fromType is null)
                {
                    continue;
                }

                string fromNamespace = fromType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                int fromLayerIndex = ResolveLayerIndex(fromNamespace, layers);
                if (fromLayerIndex < 0)
                {
                    continue;
                }

                foreach (SyntaxNode referenceNode in EnumerateReferenceNodes(typeDeclaration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ISymbol? symbol = semanticModel.GetSymbolInfo(referenceNode, cancellationToken).Symbol
                        ?? semanticModel.GetSymbolInfo(referenceNode, cancellationToken).CandidateSymbols.FirstOrDefault();
                    if (symbol is null)
                    {
                        continue;
                    }

                    INamespaceSymbol? targetNamespaceSymbol = ResolveTargetNamespaceSymbol(symbol);
                    if (targetNamespaceSymbol is null || targetNamespaceSymbol.IsGlobalNamespace)
                    {
                        continue;
                    }

                    if (symbol.ContainingType is not null &&
                        SymbolEqualityComparer.Default.Equals(symbol.ContainingType, fromType))
                    {
                        continue;
                    }

                    string targetNamespace = targetNamespaceSymbol.ToDisplayString();
                    if (ignoreSameNamespace && string.Equals(fromNamespace, targetNamespace, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int targetLayerIndex = ResolveLayerIndex(targetNamespace, layers);
                    if (targetLayerIndex < 0)
                    {
                        continue;
                    }

                    if (!IsViolation(fromLayerIndex, targetLayerIndex, direction))
                    {
                        continue;
                    }

                    FileLinePositionSpan lineSpan = referenceNode.GetLocation().GetLineSpan();
                    int line = lineSpan.StartLinePosition.Line + 1;
                    int column = lineSpan.StartLinePosition.Character + 1;
                    string key = $"{fromType.ToDisplayString()}|{targetNamespace}|{filePath}|{line}|{column}|{direction}";
                    if (!seenViolations.Add(key))
                    {
                        continue;
                    }

                    violations.Add(new DependencyViolation(
                        from_type: fromType.ToDisplayString(),
                        from_namespace: fromNamespace,
                        from_layer_index: fromLayerIndex,
                        to_namespace: targetNamespace,
                        to_layer_index: targetLayerIndex,
                        referenced_symbol: symbol.ToDisplayString(),
                        file_path: filePath,
                        line: line,
                        column: column,
                        rule: BuildRuleText(direction, layers, fromLayerIndex, targetLayerIndex)));

                    if (violations.Count >= maxViolations)
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

            if (truncated)
            {
                break;
            }
        }

        DependencyViolation[] orderedViolations = violations
            .OrderBy(v => v.file_path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.line)
            .ThenBy(v => v.column)
            .ToArray();

        object violationsPayload = brief
            ? orderedViolations.Select(v => new
            {
                v.from_type,
                v.from_namespace,
                v.to_namespace,
                v.referenced_symbol,
                v.file_path,
                v.line,
                v.column,
            }).ToArray()
            : orderedViolations;

        object data = new
        {
            query = new
            {
                workspace_path = Path.GetFullPath(workspacePath),
                layers,
                direction = DirectionToString(direction),
                include_generated = includeGenerated,
                ignore_same_namespace = ignoreSameNamespace,
                max_files = maxFiles,
                max_violations = maxViolations,
                brief,
            },
            analysis_scope = new
            {
                root_directory = workspace.RootDirectory,
                files_scanned = workspace.SyntaxTrees.Count,
                total_violations = orderedViolations.Length,
                truncated,
            },
            caveats = new[]
            {
                "Layer resolution is namespace-prefix based and may need project-specific tuning.",
                "Filesystem-root analysis is used; project graph-specific conditional includes are not applied.",
            },
            violations = violationsPayload,
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
    }

    private static INamespaceSymbol? ResolveTargetNamespaceSymbol(ISymbol symbol)
    {
        return symbol switch
        {
            INamespaceSymbol namespaceSymbol => namespaceSymbol,
            INamedTypeSymbol typeSymbol => typeSymbol.ContainingNamespace,
            IMethodSymbol methodSymbol => methodSymbol.ContainingType?.ContainingNamespace ?? methodSymbol.ContainingNamespace,
            IPropertySymbol propertySymbol => propertySymbol.ContainingType?.ContainingNamespace ?? propertySymbol.ContainingNamespace,
            IFieldSymbol fieldSymbol => fieldSymbol.ContainingType?.ContainingNamespace ?? fieldSymbol.ContainingNamespace,
            IEventSymbol eventSymbol => eventSymbol.ContainingType?.ContainingNamespace ?? eventSymbol.ContainingNamespace,
            _ => symbol.ContainingNamespace,
        };
    }

    private static void ValidateLayers(JsonElement input, List<CommandError> errors)
    {
        if (!input.TryGetProperty("layers", out JsonElement layersProperty) || layersProperty.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'layers' is required and must be an array of namespace prefixes."));
            return;
        }

        string[] layers = GetLayers(input);
        if (layers.Length < 2)
        {
            errors.Add(new CommandError("invalid_input", "Property 'layers' must contain at least two namespace prefixes."));
        }
    }

    private static string[] GetLayers(JsonElement input)
        => InputParsing.GetOptionalStringArray(input, "layers")
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static int ResolveLayerIndex(string namespaceName, IReadOnlyList<string> layers)
    {
        int bestMatch = -1;
        int bestLength = -1;
        for (int i = 0; i < layers.Count; i++)
        {
            string layer = layers[i];
            if (namespaceName.Equals(layer, StringComparison.Ordinal) ||
                namespaceName.StartsWith(layer + ".", StringComparison.Ordinal))
            {
                if (layer.Length > bestLength)
                {
                    bestLength = layer.Length;
                    bestMatch = i;
                }
            }
        }

        return bestMatch;
    }

    private static bool TryGetDirection(JsonElement input, out DependencyDirection direction)
    {
        if (!input.TryGetProperty("direction", out JsonElement directionProperty) || directionProperty.ValueKind != JsonValueKind.String)
        {
            direction = DependencyDirection.TowardEnd;
            return true;
        }

        string value = (directionProperty.GetString() ?? string.Empty).Trim();
        if (string.Equals(value, "toward_end", StringComparison.OrdinalIgnoreCase))
        {
            direction = DependencyDirection.TowardEnd;
            return true;
        }

        if (string.Equals(value, "toward_start", StringComparison.OrdinalIgnoreCase))
        {
            direction = DependencyDirection.TowardStart;
            return true;
        }

        direction = default;
        return false;
    }

    private static bool IsViolation(int fromLayerIndex, int toLayerIndex, DependencyDirection direction)
    {
        return direction switch
        {
            DependencyDirection.TowardEnd => toLayerIndex < fromLayerIndex,
            DependencyDirection.TowardStart => toLayerIndex > fromLayerIndex,
            _ => false,
        };
    }

    private static string BuildRuleText(DependencyDirection direction, IReadOnlyList<string> layers, int fromLayerIndex, int toLayerIndex)
    {
        string fromLayer = layers[fromLayerIndex];
        string toLayer = layers[toLayerIndex];
        return direction switch
        {
            DependencyDirection.TowardEnd => $"Layer rule violation: '{fromLayer}' must not depend on earlier layer '{toLayer}'.",
            DependencyDirection.TowardStart => $"Layer rule violation: '{fromLayer}' must not depend on later layer '{toLayer}'.",
            _ => "Layer rule violation.",
        };
    }

    private static string DirectionToString(DependencyDirection direction)
        => direction == DependencyDirection.TowardStart ? "toward_start" : "toward_end";

    private enum DependencyDirection
    {
        TowardEnd,
        TowardStart,
    }

    private sealed record DependencyViolation(
        string from_type,
        string from_namespace,
        int from_layer_index,
        string to_namespace,
        int to_layer_index,
        string referenced_symbol,
        string file_path,
        int line,
        int column,
        string rule);
}
