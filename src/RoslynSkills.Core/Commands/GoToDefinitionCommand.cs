using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class GoToDefinitionCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "nav.go_to_definition",
        Summary: "Resolve the symbol at line/column to its declaration(s) in source or metadata.",
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

        InputParsing.TryGetRequiredInt(input, "line", errors, out _, minValue: 1, maxValue: 1_000_000);
        InputParsing.TryGetRequiredInt(input, "column", errors, out _, minValue: 1, maxValue: 1_000_000);

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
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

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: false);
        int contextLines = InputParsing.GetOptionalInt(input, "context_lines", defaultValue: 1, minValue: 0, maxValue: 20);
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        CommandFileAnalysis analysis = await CommandFileAnalysis
            .LoadAsync(filePath, cancellationToken, workspacePath, WorkspaceInput.GetOptionalWorkspaceHandle(input))
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
        ISymbol? anchorSymbol = ResolveAnchorSymbol(anchorToken, analysis.SemanticModel, cancellationToken);
        if (anchorSymbol is null)
        {
            string hint = SymbolResolution.FormatLineSymbolHint(analysis, line, predicate: null, cancellationToken);
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("symbol_not_found", $"Unable to resolve a semantic symbol at {line}:{column}.{hint}") });
        }

        ISymbol definitionSymbol = NormalizeDefinitionSymbol(anchorSymbol);
        Solution? solution = analysis.Document?.Project.Solution;
        string searchScope = solution is not null ? SolutionSearchScope.SolutionKind : SolutionSearchScope.CompilationKind;

        if (solution is not null)
        {
            ISymbol? sourceDefinition = await SymbolFinder
                .FindSourceDefinitionAsync(definitionSymbol, solution, cancellationToken)
                .ConfigureAwait(false);
            if (sourceDefinition is not null)
            {
                definitionSymbol = NormalizeDefinitionSymbol(sourceDefinition);
            }
        }

        // An implicitly generated parameterless constructor has no source declaration of its own;
        // navigate to its containing type declaration instead.
        if (definitionSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: true } implicitCtor &&
            !implicitCtor.Locations.Any(location => location.IsInSource) &&
            implicitCtor.ContainingType is not null)
        {
            definitionSymbol = NormalizeDefinitionSymbol(implicitCtor.ContainingType);
            if (solution is not null)
            {
                ISymbol? sourceType = await SymbolFinder
                    .FindSourceDefinitionAsync(definitionSymbol, solution, cancellationToken)
                    .ConfigureAwait(false);
                if (sourceType is not null)
                {
                    definitionSymbol = NormalizeDefinitionSymbol(sourceType);
                }
            }
        }

        List<ISymbol> targetSymbols = CollectPartialDefinitionSymbols(definitionSymbol);
        List<DefinitionLocation> definitions = await BuildDefinitionLocationsAsync(
                targetSymbols,
                solution,
                brief,
                contextLines,
                cancellationToken)
            .ConfigureAwait(false);

        int sourceCount = definitions.Count(d => string.Equals(d.location_kind, "source", StringComparison.Ordinal));
        int metadataCount = definitions.Count(d => string.Equals(d.location_kind, "metadata", StringComparison.Ordinal));

        object data = new
        {
            file_path = analysis.FilePath,
            query = new
            {
                line,
                column,
                brief,
                context_lines = contextLines,
                workspace_path = workspacePath,
                require_workspace = requireWorkspace,
                workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            },
            symbol = new
            {
                symbol_display = definitionSymbol.ToDisplayString(),
                symbol_kind = definitionSymbol.Kind.ToString(),
                symbol_id = CommandTextFormatting.GetStableSymbolId(definitionSymbol),
                assembly_name = definitionSymbol.ContainingAssembly?.Name,
                metadata_name = GetMetadataName(definitionSymbol),
                is_from_source = sourceCount > 0,
                is_from_metadata = metadataCount > 0,
            },
            search_scope = searchScope,
            total_definitions = definitions.Count,
            source_definition_count = sourceCount,
            metadata_definition_count = metadataCount,
            definitions,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static ISymbol? ResolveAnchorSymbol(
        SyntaxToken anchorToken,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = SymbolResolution.GetSymbolForToken(anchorToken, semanticModel, cancellationToken);
        if (symbol is not null)
        {
            return symbol;
        }

        SyntaxNode? node = anchorToken.Parent;
        if (node is null)
        {
            return null;
        }

        foreach (SyntaxNode candidate in node.AncestorsAndSelf())
        {
            ITypeSymbol? typeSymbol = semanticModel.GetTypeInfo(candidate, cancellationToken).Type;
            if (typeSymbol is not null && typeSymbol.TypeKind != TypeKind.Error)
            {
                return typeSymbol;
            }
        }

        return null;
    }

    private static ISymbol NormalizeDefinitionSymbol(ISymbol symbol)
    {
        ISymbol current = symbol;
        if (current is IAliasSymbol aliasSymbol)
        {
            current = aliasSymbol.Target;
        }

        if (current is IMethodSymbol methodSymbol && methodSymbol.ReducedFrom is not null)
        {
            current = methodSymbol.ReducedFrom;
        }

        return current.OriginalDefinition;
    }

    private static List<ISymbol> CollectPartialDefinitionSymbols(ISymbol definitionSymbol)
    {
        List<ISymbol> symbols = new() { definitionSymbol };
        if (definitionSymbol is IMethodSymbol methodSymbol)
        {
            if (methodSymbol.PartialDefinitionPart is not null &&
                !symbols.Any(s => SymbolEqualityComparer.Default.Equals(s, methodSymbol.PartialDefinitionPart)))
            {
                symbols.Add(methodSymbol.PartialDefinitionPart);
            }

            if (methodSymbol.PartialImplementationPart is not null &&
                !symbols.Any(s => SymbolEqualityComparer.Default.Equals(s, methodSymbol.PartialImplementationPart)))
            {
                symbols.Add(methodSymbol.PartialImplementationPart);
            }
        }
        else if (definitionSymbol is IPropertySymbol propertySymbol)
        {
            if (propertySymbol.PartialDefinitionPart is not null &&
                !symbols.Any(s => SymbolEqualityComparer.Default.Equals(s, propertySymbol.PartialDefinitionPart)))
            {
                symbols.Add(propertySymbol.PartialDefinitionPart);
            }

            if (propertySymbol.PartialImplementationPart is not null &&
                !symbols.Any(s => SymbolEqualityComparer.Default.Equals(s, propertySymbol.PartialImplementationPart)))
            {
                symbols.Add(propertySymbol.PartialImplementationPart);
            }
        }

        return symbols;
    }

    private static async Task<List<DefinitionLocation>> BuildDefinitionLocationsAsync(
        IReadOnlyList<ISymbol> symbols,
        Solution? solution,
        bool brief,
        int contextLines,
        CancellationToken cancellationToken)
    {
        List<DefinitionLocation> sourceLocations = new();
        HashSet<string> seenSource = new(StringComparer.OrdinalIgnoreCase);

        foreach (ISymbol symbol in symbols)
        {
            foreach (Location location in symbol.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!location.IsInSource || location.SourceTree is null)
                {
                    continue;
                }

                FileLinePositionSpan span = location.GetLineSpan();
                string rawPath = !string.IsNullOrWhiteSpace(span.Path)
                    ? span.Path
                    : location.SourceTree.FilePath;
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(rawPath);
                int startLine = span.StartLinePosition.Line + 1;
                int startColumn = span.StartLinePosition.Character + 1;
                int endLine = span.EndLinePosition.Line + 1;
                int endColumn = span.EndLinePosition.Character + 1;

                string key = $"{fullPath}|{startLine}|{startColumn}";
                if (!seenSource.Add(key))
                {
                    continue;
                }

                string? snippet = null;
                if (!brief)
                {
                    SourceText sourceText = await location.SourceTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    snippet = CommandTextFormatting.BuildSnippet(sourceText, startLine, contextLines);
                }

                string? projectName = solution?.GetDocument(location.SourceTree)?.Project.Name;
                sourceLocations.Add(new DefinitionLocation(
                    location_kind: "source",
                    symbol_display: symbol.ToDisplayString(),
                    symbol_kind: symbol.Kind.ToString(),
                    file_path: fullPath,
                    line: startLine,
                    column: startColumn,
                    end_line: endLine,
                    end_column: endColumn,
                    project_name: projectName,
                    assembly_name: symbol.ContainingAssembly?.Name,
                    assembly_identity: symbol.ContainingAssembly?.Identity.ToString(),
                    metadata_module: symbol.ContainingModule?.Name,
                    metadata_name: GetMetadataName(symbol),
                    snippet: snippet));
            }
        }

        if (sourceLocations.Count > 0)
        {
            return sourceLocations
                .OrderBy(d => d.file_path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.line)
                .ThenBy(d => d.column)
                .ToList();
        }

        List<DefinitionLocation> metadataLocations = new();
        HashSet<string> seenMetadata = new(StringComparer.OrdinalIgnoreCase);

        foreach (ISymbol symbol in symbols)
        {
            string? assemblyName = symbol.ContainingAssembly?.Name;
            string? assemblyIdentity = symbol.ContainingAssembly?.Identity.ToString();
            string metadataName = GetMetadataName(symbol);
            bool addedForSymbol = false;

            foreach (Location location in symbol.Locations)
            {
                if (!location.IsInMetadata)
                {
                    continue;
                }

                string? moduleName = location.MetadataModule?.Name ?? symbol.ContainingModule?.Name;
                string key = $"{assemblyName}|{moduleName}|{metadataName}";
                if (!seenMetadata.Add(key))
                {
                    addedForSymbol = true;
                    continue;
                }

                metadataLocations.Add(new DefinitionLocation(
                    location_kind: "metadata",
                    symbol_display: symbol.ToDisplayString(),
                    symbol_kind: symbol.Kind.ToString(),
                    file_path: null,
                    line: null,
                    column: null,
                    end_line: null,
                    end_column: null,
                    project_name: null,
                    assembly_name: assemblyName,
                    assembly_identity: assemblyIdentity,
                    metadata_module: moduleName,
                    metadata_name: metadataName,
                    snippet: null));
                addedForSymbol = true;
            }

            if (!addedForSymbol)
            {
                string? moduleName = symbol.ContainingModule?.Name;
                string key = $"{assemblyName}|{moduleName}|{metadataName}";
                if (seenMetadata.Add(key))
                {
                    metadataLocations.Add(new DefinitionLocation(
                        location_kind: "metadata",
                        symbol_display: symbol.ToDisplayString(),
                        symbol_kind: symbol.Kind.ToString(),
                        file_path: null,
                        line: null,
                        column: null,
                        end_line: null,
                        end_column: null,
                        project_name: null,
                        assembly_name: assemblyName,
                        assembly_identity: assemblyIdentity,
                        metadata_module: moduleName,
                        metadata_name: metadataName,
                        snippet: null));
                }
            }
        }

        return metadataLocations;
    }

    private static string GetMetadataName(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedType)
        {
            return BuildTypeMetadataName(namedType);
        }

        if (symbol.ContainingType is not null)
        {
            return $"{BuildTypeMetadataName(symbol.ContainingType)}.{symbol.MetadataName}";
        }

        if (symbol.ContainingNamespace is not null && !symbol.ContainingNamespace.IsGlobalNamespace)
        {
            return $"{symbol.ContainingNamespace.ToDisplayString()}.{symbol.MetadataName}";
        }

        return symbol.MetadataName;
    }

    private static string BuildTypeMetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
        {
            return $"{BuildTypeMetadataName(type.ContainingType)}+{type.MetadataName}";
        }

        if (type.ContainingNamespace is not null && !type.ContainingNamespace.IsGlobalNamespace)
        {
            return $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}";
        }

        return type.MetadataName;
    }

    private sealed record DefinitionLocation(
        string location_kind,
        string symbol_display,
        string symbol_kind,
        string? file_path,
        int? line,
        int? column,
        int? end_line,
        int? end_column,
        string? project_name,
        string? assembly_name,
        string? assembly_identity,
        string? metadata_module,
        string metadata_name,
        string? snippet);
}
