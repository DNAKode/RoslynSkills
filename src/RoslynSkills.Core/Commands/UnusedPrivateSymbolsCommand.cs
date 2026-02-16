using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class UnusedPrivateSymbolsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "analyze.unused_private_symbols",
        Summary: "Find likely unused private symbols in a workspace/file-root scope.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Advanced,
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
        int maxSymbols = InputParsing.GetOptionalInt(input, "max_symbols", defaultValue: 1_000, minValue: 1, maxValue: 50_000);

        (StaticAnalysisWorkspace? Workspace, CommandError? Error) loadResult = await StaticAnalysisWorkspace
            .LoadAsync(workspacePath, includeGenerated, maxFiles, cancellationToken)
            .ConfigureAwait(false);
        if (loadResult.Error is not null || loadResult.Workspace is null)
        {
            return new CommandExecutionResult(null, new[] { loadResult.Error ?? new CommandError("analysis_failed", "Failed to load workspace for analysis.") });
        }

        StaticAnalysisWorkspace workspace = loadResult.Workspace;
        Dictionary<string, PrivateSymbolCandidate> candidatesById = new(StringComparer.Ordinal);

        foreach (SyntaxTree tree in workspace.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel semanticModel = workspace.SemanticModelsByTree[tree];
            string filePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);

            foreach (FieldDeclarationSyntax field in root.DescendantNodes(descendIntoTrivia: false).OfType<FieldDeclarationSyntax>())
            {
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    IFieldSymbol? symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken) as IFieldSymbol;
                    TryAddCandidate(symbol, variable, filePath, candidatesById);
                }
            }

            foreach (EventFieldDeclarationSyntax field in root.DescendantNodes(descendIntoTrivia: false).OfType<EventFieldDeclarationSyntax>())
            {
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    IFieldSymbol? symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken) as IFieldSymbol;
                    TryAddCandidate(symbol, variable, filePath, candidatesById);
                }
            }

            foreach (EventDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<EventDeclarationSyntax>())
            {
                IEventSymbol? symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IEventSymbol;
                TryAddCandidate(symbol, declaration, filePath, candidatesById);
            }

            foreach (PropertyDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<PropertyDeclarationSyntax>())
            {
                IPropertySymbol? symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IPropertySymbol;
                TryAddCandidate(symbol, declaration, filePath, candidatesById);
            }

            foreach (IndexerDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<IndexerDeclarationSyntax>())
            {
                IPropertySymbol? symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IPropertySymbol;
                TryAddCandidate(symbol, declaration, filePath, candidatesById);
            }

            foreach (MethodDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<MethodDeclarationSyntax>())
            {
                IMethodSymbol? symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
                TryAddCandidate(symbol, declaration, filePath, candidatesById);
            }

            foreach (ConstructorDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<ConstructorDeclarationSyntax>())
            {
                IMethodSymbol? symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
                TryAddCandidate(symbol, declaration, filePath, candidatesById);
            }

            foreach (BaseTypeDeclarationSyntax declaration in root.DescendantNodes(descendIntoTrivia: false).OfType<BaseTypeDeclarationSyntax>())
            {
                INamedTypeSymbol? symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as INamedTypeSymbol;
                TryAddCandidate(symbol, declaration, filePath, candidatesById);
            }
        }

        foreach (SyntaxTree tree in workspace.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel semanticModel = workspace.SemanticModelsByTree[tree];
            string filePath = string.IsNullOrWhiteSpace(tree.FilePath) ? "<unknown>" : Path.GetFullPath(tree.FilePath);

            foreach (SyntaxNode referenceNode in EnumerateReferenceNodes(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ISymbol? symbol = semanticModel.GetSymbolInfo(referenceNode, cancellationToken).Symbol
                    ?? semanticModel.GetSymbolInfo(referenceNode, cancellationToken).CandidateSymbols.FirstOrDefault();
                if (symbol is null)
                {
                    continue;
                }

                if (!TryResolveCandidate(symbol, candidatesById, out PrivateSymbolCandidate? candidate) || candidate is null)
                {
                    continue;
                }

                string locationKey = BuildLocationKey(filePath, referenceNode.SpanStart);
                if (candidate.declaration_keys.Contains(locationKey))
                {
                    continue;
                }

                candidate.usage_count++;
            }
        }

        PrivateSymbolCandidate[] unused = candidatesById.Values
            .Where(c => c.usage_count == 0)
            .OrderBy(c => c.file_path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.line)
            .ThenBy(c => c.column)
            .Take(maxSymbols)
            .ToArray();

        object unusedPayload = brief
            ? unused.Select(c => new
            {
                c.symbol_id,
                c.symbol_display,
                c.symbol_kind,
                c.file_path,
                c.line,
                c.column,
            }).ToArray()
            : unused;

        object data = new
        {
            query = new
            {
                workspace_path = Path.GetFullPath(workspacePath),
                include_generated = includeGenerated,
                max_files = maxFiles,
                max_symbols = maxSymbols,
                brief,
            },
            analysis_scope = new
            {
                root_directory = workspace.RootDirectory,
                files_scanned = workspace.SyntaxTrees.Count,
                total_candidates = candidatesById.Count,
                unused_candidates = unused.Length,
            },
            caveats = new[]
            {
                "Results are heuristic and may undercount reflection/source-generator usage.",
                "Filesystem-root analysis is used; project graph-specific conditional includes are not applied.",
            },
            unused_symbols = unusedPayload,
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

    private static bool TryResolveCandidate(
        ISymbol symbol,
        IReadOnlyDictionary<string, PrivateSymbolCandidate> candidatesById,
        out PrivateSymbolCandidate? candidate)
    {
        string symbolId = CommandTextFormatting.GetStableSymbolId(symbol)
            ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (candidatesById.TryGetValue(symbolId, out candidate))
        {
            return true;
        }

        ISymbol original = symbol.OriginalDefinition;
        string originalId = CommandTextFormatting.GetStableSymbolId(original)
            ?? original.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return candidatesById.TryGetValue(originalId, out candidate);
    }

    private static void TryAddCandidate(
        ISymbol? symbol,
        SyntaxNode declarationNode,
        string filePath,
        IDictionary<string, PrivateSymbolCandidate> candidatesById)
    {
        if (symbol is null ||
            symbol.IsImplicitlyDeclared ||
            symbol.DeclaredAccessibility != Accessibility.Private)
        {
            return;
        }

        if (symbol is IMethodSymbol method &&
            (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove))
        {
            return;
        }

        string symbolId = CommandTextFormatting.GetStableSymbolId(symbol)
            ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            return;
        }

        FileLinePositionSpan lineSpan = declarationNode.GetLocation().GetLineSpan();
        string declarationKey = BuildLocationKey(filePath, declarationNode.SpanStart);
        if (candidatesById.TryGetValue(symbolId, out PrivateSymbolCandidate? existing))
        {
            existing.declaration_keys.Add(declarationKey);
            return;
        }

        PrivateSymbolCandidate candidate = new(
            symbol_id: symbolId,
            symbol_display: symbol.ToDisplayString(),
            symbol_kind: symbol.Kind.ToString(),
            containing_symbol: symbol.ContainingSymbol?.ToDisplayString(),
            file_path: filePath,
            line: lineSpan.StartLinePosition.Line + 1,
            column: lineSpan.StartLinePosition.Character + 1,
            declaration_keys: new HashSet<string>(StringComparer.Ordinal) { declarationKey },
            usage_count: 0);
        candidatesById[symbolId] = candidate;
    }

    private static string BuildLocationKey(string filePath, int spanStart)
        => $"{Path.GetFullPath(filePath)}::{spanStart}";

    private sealed class PrivateSymbolCandidate
    {
        public string symbol_id { get; }
        public string symbol_display { get; }
        public string symbol_kind { get; }
        public string? containing_symbol { get; }
        public string file_path { get; }
        public int line { get; }
        public int column { get; }
        public HashSet<string> declaration_keys { get; }
        public int usage_count { get; set; }

        public PrivateSymbolCandidate(
            string symbol_id,
            string symbol_display,
            string symbol_kind,
            string? containing_symbol,
            string file_path,
            int line,
            int column,
            HashSet<string> declaration_keys,
            int usage_count)
        {
            this.symbol_id = symbol_id;
            this.symbol_display = symbol_display;
            this.symbol_kind = symbol_kind;
            this.containing_symbol = containing_symbol;
            this.file_path = file_path;
            this.line = line;
            this.column = column;
            this.declaration_keys = declaration_keys;
            this.usage_count = usage_count;
        }
    }
}
