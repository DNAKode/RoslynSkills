using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class OverrideCoverageCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "analyze.override_coverage",
        Summary: "Analyze override coverage for virtual/abstract members across derived source types.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
        Traits: [CommandTrait.DerivedAnalysis, CommandTrait.PotentiallySlow]);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "workspace_path", errors, out string workspacePath))
        {
            return errors;
        }

        InputParsing.ValidateOptionalBool(input, "include_generated", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        if (!TryGetCoverageThreshold(input, out _))
        {
            errors.Add(new CommandError("invalid_input", "Property 'coverage_threshold' must be a number between 0 and 1."));
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

        if (!TryGetCoverageThreshold(input, out double coverageThreshold))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "Property 'coverage_threshold' must be a number between 0 and 1.") });
        }

        bool includeGenerated = InputParsing.GetOptionalBool(input, "include_generated", defaultValue: false);
        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: true);
        int maxFiles = InputParsing.GetOptionalInt(input, "max_files", defaultValue: 2_000, minValue: 1, maxValue: 50_000);
        int maxMembers = InputParsing.GetOptionalInt(input, "max_members", defaultValue: 1_000, minValue: 1, maxValue: 50_000);
        int minDerivedTypes = InputParsing.GetOptionalInt(input, "min_derived_types", defaultValue: 1, minValue: 1, maxValue: 10_000);

        (StaticAnalysisWorkspace? Workspace, CommandError? Error) loadResult = await StaticAnalysisWorkspace
            .LoadAsync(workspacePath, includeGenerated, maxFiles, cancellationToken)
            .ConfigureAwait(false);
        if (loadResult.Error is not null || loadResult.Workspace is null)
        {
            return new CommandExecutionResult(null, new[] { loadResult.Error ?? new CommandError("analysis_failed", "Failed to load workspace for analysis.") });
        }

        StaticAnalysisWorkspace workspace = loadResult.Workspace;
        Dictionary<string, INamedTypeSymbol> sourceTypesById = new(StringComparer.Ordinal);
        foreach (SyntaxTree tree in workspace.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel semanticModel = workspace.SemanticModelsByTree[tree];
            foreach (BaseTypeDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<BaseTypeDeclarationSyntax>())
            {
                INamedTypeSymbol? typeSymbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as INamedTypeSymbol;
                if (typeSymbol is null)
                {
                    continue;
                }

                string typeId = CommandTextFormatting.GetStableSymbolId(typeSymbol)
                    ?? typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sourceTypesById[typeId] = typeSymbol;
            }
        }

        INamedTypeSymbol[] sourceTypes = sourceTypesById.Values
            .OrderBy(value => value.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();
        List<OverrideCoverageFinding> findings = new();
        bool truncated = false;

        foreach (INamedTypeSymbol baseType in sourceTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            INamedTypeSymbol[] derivedTypes = sourceTypes
                .Where(candidate => InheritsFrom(candidate, baseType))
                .ToArray();
            if (derivedTypes.Length < minDerivedTypes)
            {
                continue;
            }

            ISymbol[] candidates = baseType.GetMembers()
                .Where(IsOverridableMember)
                .ToArray();
            foreach (ISymbol member in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int overrideCount = derivedTypes.Count(derivedType => HasOverride(derivedType, member));
                int concreteDerivedCount = derivedTypes.Count(derivedType => !derivedType.IsAbstract);
                int missingRequiredOverrides = member.IsAbstract
                    ? derivedTypes.Count(derivedType => !derivedType.IsAbstract && !HasOverride(derivedType, member))
                    : 0;
                double coverage = derivedTypes.Length == 0 ? 1d : (double)overrideCount / derivedTypes.Length;

                bool include = member.IsAbstract
                    ? missingRequiredOverrides > 0
                    : coverage < coverageThreshold;
                if (!include)
                {
                    continue;
                }

                Location? sourceLocation = member.Locations.FirstOrDefault(location => location.IsInSource);
                string filePath = sourceLocation?.SourceTree?.FilePath is { Length: > 0 } path
                    ? Path.GetFullPath(path)
                    : "<unknown>";
                FileLinePositionSpan lineSpan = sourceLocation?.GetLineSpan() ?? default;

                findings.Add(new OverrideCoverageFinding(
                    base_type: baseType.ToDisplayString(),
                    member_symbol: member.ToDisplayString(),
                    member_kind: member.Kind.ToString(),
                    is_abstract: member.IsAbstract,
                    file_path: filePath,
                    line: sourceLocation is null ? 0 : lineSpan.StartLinePosition.Line + 1,
                    column: sourceLocation is null ? 0 : lineSpan.StartLinePosition.Character + 1,
                    derived_type_count: derivedTypes.Length,
                    concrete_derived_type_count: concreteDerivedCount,
                    override_count: overrideCount,
                    missing_required_overrides: missingRequiredOverrides,
                    coverage_ratio: coverage));

                if (findings.Count >= maxMembers)
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

        OverrideCoverageFinding[] orderedFindings = findings
            .OrderByDescending(value => value.missing_required_overrides)
            .ThenBy(value => value.coverage_ratio)
            .ThenBy(value => value.file_path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.line)
            .ToArray();

        object findingsPayload = brief
            ? orderedFindings.Select(value => new
            {
                value.base_type,
                value.member_symbol,
                value.member_kind,
                value.derived_type_count,
                value.override_count,
                value.missing_required_overrides,
                value.coverage_ratio,
                value.file_path,
                value.line,
                value.column,
            }).ToArray()
            : orderedFindings;

        object data = new
        {
            query = new
            {
                workspace_path = Path.GetFullPath(workspacePath),
                include_generated = includeGenerated,
                max_files = maxFiles,
                max_members = maxMembers,
                min_derived_types = minDerivedTypes,
                coverage_threshold = coverageThreshold,
                brief,
            },
            analysis_scope = new
            {
                root_directory = workspace.RootDirectory,
                files_scanned = workspace.SyntaxTrees.Count,
                source_types = sourceTypes.Length,
                findings = orderedFindings.Length,
                truncated,
            },
            caveats = new[]
            {
                "Coverage is source-only and reports likely hotspots, not strict policy failures.",
                "Filesystem-root analysis is used; project graph-specific conditional includes are not applied.",
            },
            findings = findingsPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool TryGetCoverageThreshold(JsonElement input, out double threshold)
    {
        threshold = 0.60;
        if (!input.TryGetProperty("coverage_threshold", out JsonElement property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out double parsed))
        {
            return false;
        }

        if (parsed < 0d || parsed > 1d)
        {
            return false;
        }

        threshold = parsed;
        return true;
    }

    private static bool IsOverridableMember(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared || symbol.IsStatic)
        {
            return false;
        }

        if (!(symbol.IsAbstract || symbol.IsVirtual))
        {
            return false;
        }

        return symbol switch
        {
            IMethodSymbol method => method.MethodKind is MethodKind.Ordinary && !method.IsStatic,
            IPropertySymbol _ => true,
            IEventSymbol _ => true,
            _ => false,
        };
    }

    private static bool InheritsFrom(INamedTypeSymbol candidate, INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol? current = candidate.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOverride(INamedTypeSymbol derivedType, ISymbol baseMember)
    {
        return baseMember switch
        {
            IMethodSymbol method => derivedType.GetMembers(method.Name)
                .OfType<IMethodSymbol>()
                .Any(candidate => candidate.IsOverride &&
                    candidate.OverriddenMethod is not null &&
                    SymbolEqualityComparer.Default.Equals(candidate.OverriddenMethod.OriginalDefinition, method.OriginalDefinition)),
            IPropertySymbol property => derivedType.GetMembers(property.Name)
                .OfType<IPropertySymbol>()
                .Any(candidate => candidate.IsOverride &&
                    candidate.OverriddenProperty is not null &&
                    SymbolEqualityComparer.Default.Equals(candidate.OverriddenProperty.OriginalDefinition, property.OriginalDefinition)),
            IEventSymbol eventSymbol => derivedType.GetMembers(eventSymbol.Name)
                .OfType<IEventSymbol>()
                .Any(candidate => candidate.IsOverride &&
                    candidate.OverriddenEvent is not null &&
                    SymbolEqualityComparer.Default.Equals(candidate.OverriddenEvent.OriginalDefinition, eventSymbol.OriginalDefinition)),
            _ => false,
        };
    }

    private sealed record OverrideCoverageFinding(
        string base_type,
        string member_symbol,
        string member_kind,
        bool is_abstract,
        string file_path,
        int line,
        int column,
        int derived_type_count,
        int concrete_derived_type_count,
        int override_count,
        int missing_required_overrides,
        double coverage_ratio);
}
