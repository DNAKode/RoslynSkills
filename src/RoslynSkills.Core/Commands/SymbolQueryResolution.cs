using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

internal sealed record SymbolQueryResult(
    StaticAnalysisWorkspace Workspace,
    IReadOnlyList<ISymbol> Symbols,
    string Query);

/// <summary>
/// Resolves <c>--symbol</c> / <c>symbol</c> / <c>symbol_name</c> queries against a loaded Roslyn solution for
/// <c>nav.*</c> commands, including synthesized positional-record properties and optional parameter-type overload
/// disambiguation (e.g. <c>SchemaWriter.CreateTable(TransactionCoordinator, CreateTableRequest, ulong, ulong)</c>).
/// </summary>
internal static class SymbolQueryResolution
{
    public static string? GetOptionalSymbolQuery(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (input.TryGetProperty("symbol", out JsonElement symbolProp) &&
            symbolProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(symbolProp.GetString()))
        {
            return symbolProp.GetString()!.Trim();
        }

        if (input.TryGetProperty("symbol_name", out JsonElement symbolNameProp) &&
            symbolNameProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(symbolNameProp.GetString()))
        {
            return symbolNameProp.GetString()!.Trim();
        }

        return null;
    }

    public static async Task<(SymbolQueryResult? Result, CommandExecutionResult? ErrorResult)> ResolveAsync(
        JsonElement input,
        string symbolQuery,
        string? workspacePath,
        Func<ISymbol, bool>? targetPredicate,
        string invalidTargetMessageFormat,
        bool allowSameTypeOverloads,
        CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!WorkspaceInput.TryGetRequiredWorkspaceTarget(input, errors, out _, out _))
        {
            return (null, new CommandExecutionResult(null, errors));
        }

        (StaticAnalysisWorkspace? workspace, CommandError? loadError) = await StaticAnalysisWorkspace
            .ResolveAsync(input, workspacePath, includeGenerated: false, maxFiles: 50_000, cancellationToken)
            .ConfigureAwait(false);
        if (loadError is not null || workspace is null)
        {
            return (
                null,
                new CommandExecutionResult(
                    null,
                    new[] { loadError ?? new CommandError("workspace_load_failed", "Failed to resolve workspace for symbol lookup.") }));
        }

        if (workspace.Solution is null)
        {
            return (
                null,
                new CommandExecutionResult(
                    null,
                    new[] { new CommandError("workspace_required", "Symbol lookup (--symbol) requires a loaded MSBuild solution or project workspace. Run 'roscli workspace.use <solution>' first.") }));
        }

        List<ISymbol> candidates = await ResolveSymbolsByNameAsync(workspace.Solution, symbolQuery, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return (
                null,
                new CommandExecutionResult(
                    null,
                    new[] { new CommandError("symbol_not_found", $"No source symbol matching '{symbolQuery}' was found in workspace '{workspace.ResolvedWorkspacePath}'.") }));
        }

        if (targetPredicate is not null)
        {
            List<ISymbol> filtered = candidates.Where(targetPredicate).ToList();
            if (filtered.Count == 0)
            {
                ISymbol first = candidates[0];
                string message = string.Format(invalidTargetMessageFormat, first.ToDisplayString(), first.Kind);
                return (null, new CommandExecutionResult(null, new[] { new CommandError("invalid_target", message) }));
            }

            candidates = filtered;
        }

        if (candidates.Count > 1 && !(allowSameTypeOverloads && AreSameTypeMethodOverloads(candidates)))
        {
            object[] candidateSummaries = candidates
                .Take(20)
                .Select(FormatSymbolCandidate)
                .ToArray();
            return (
                null,
                new CommandExecutionResult(
                    null,
                    new[]
                    {
                        new CommandError(
                            "ambiguous_symbol",
                            $"Symbol '{symbolQuery}' matched {candidates.Count} declarations. Qualify the name further, specify parameter types (e.g. Type.Method(int)), or pass <file>:<line>:<column>.",
                            new { candidates = candidateSummaries }),
                    }));
        }

        return (new SymbolQueryResult(workspace, candidates, symbolQuery), null);
    }

    public static async Task<List<ISymbol>> ResolveSymbolsByNameAsync(
        Solution solution,
        string symbolQuery,
        CancellationToken cancellationToken)
    {
        ParsedSymbolQuery? parsed = ParseQuery(symbolQuery);
        if (parsed is null)
        {
            return new List<ISymbol>();
        }

        Dictionary<string, ISymbol> byKey = new(StringComparer.Ordinal);
        void TryAddCandidate(ISymbol candidate)
        {
            if (!candidate.Locations.Any(loc => loc.IsInSource))
            {
                return;
            }

            if (!MatchesParsedQuery(candidate, parsed))
            {
                return;
            }

            string key = CommandTextFormatting.GetStableSymbolId(candidate)
                ?? candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            byKey.TryAdd(key, candidate);
        }

        Project[] projects = solution.Projects.ToArray();

        // 1. Standard declaration lookup across all projects in parallel via Roslyn's SymbolFinder declaration index.
        Task<IEnumerable<ISymbol>>[] directTasks = projects
            .Select(project => SymbolFinder.FindDeclarationsAsync(
                project,
                parsed.LookupName,
                ignoreCase: false,
                SymbolFilter.TypeAndMember,
                cancellationToken))
            .ToArray();

        // 2. Member lookup via the containing type (also in parallel). Roslyn's declaration index omits synthesized
        //    properties generated from positional record parameters (e.g. `record DeferredCatalogCommit(..., Kind = ...)`),
        //    primary constructor members, and constructors referenced via `.ctor`.
        string? parentLookupName = parsed.Segments.Length >= 2 ? StripGenericSuffix(parsed.Segments[^2]) : null;
        string? parentQualified = parsed.Segments.Length >= 2
            ? string.Join(".", parsed.Segments[..^1].Select(StripGenericSuffix))
            : null;
        Task<IEnumerable<ISymbol>>[] parentTasks = parentLookupName is not null
            ? projects
                .Select(project => SymbolFinder.FindDeclarationsAsync(
                    project,
                    parentLookupName,
                    ignoreCase: false,
                    SymbolFilter.Type,
                    cancellationToken))
                .ToArray()
            : Array.Empty<Task<IEnumerable<ISymbol>>>();

        IEnumerable<ISymbol>[] directResults = await Task.WhenAll(directTasks).ConfigureAwait(false);
        foreach (IEnumerable<ISymbol> declared in directResults)
        {
            foreach (ISymbol candidate in declared)
            {
                TryAddCandidate(candidate);
            }
        }

        if (parentTasks.Length > 0 && parentQualified is not null)
        {
            bool lookupCtor = string.Equals(parsed.LookupName, ".ctor", StringComparison.Ordinal) ||
                              string.Equals(parsed.LookupName, "ctor", StringComparison.OrdinalIgnoreCase);
            IEnumerable<ISymbol>[] parentResults = await Task.WhenAll(parentTasks).ConfigureAwait(false);
            foreach (IEnumerable<ISymbol> parentTypes in parentResults)
            {
                foreach (INamedTypeSymbol parentType in parentTypes.OfType<INamedTypeSymbol>())
                {
                    if (!parentType.Locations.Any(loc => loc.IsInSource))
                    {
                        continue;
                    }

                    if (!MatchesQualifiedSymbolName(parentType, parentQualified, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    IEnumerable<ISymbol> members = lookupCtor
                        ? parentType.InstanceConstructors
                        : parentType.GetMembers(parsed.LookupName);
                    foreach (ISymbol member in members)
                    {
                        TryAddCandidate(member);
                    }
                }
            }
        }
        else if (byKey.Count == 0)
        {
            // Unqualified lookup fallback for synthesized positional record properties (not in FindDeclarationsAsync).
            foreach (Project project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    continue;
                }

                foreach (ISymbol symbol in compilation.GetSymbolsWithName(
                             name => string.Equals(name, parsed.LookupName, StringComparison.Ordinal),
                             SymbolFilter.Member,
                             cancellationToken))
                {
                    TryAddCandidate(symbol);
                }
            }
        }

        return byKey.Values
            .OrderBy(s => BuildDottedSymbolName(s), StringComparer.Ordinal)
            .ThenBy(s => s.ToDisplayString(), StringComparer.Ordinal)
            .ToList();
    }

    public static (string? FilePath, int? Line, int? Column) GetPrimaryDeclarationLocation(ISymbol symbol)
    {
        Location? firstSourceLocation = SymbolResolution.GetDeclarationIdentifierLocation(symbol);
        if (firstSourceLocation is not { IsInSource: true, SourceTree: not null })
        {
            return (null, null, null);
        }

        FileLinePositionSpan span = firstSourceLocation.GetLineSpan();
        string? rawPath = !string.IsNullOrWhiteSpace(span.Path) ? span.Path : firstSourceLocation.SourceTree.FilePath;
        string? fullPath = string.IsNullOrWhiteSpace(rawPath) ? null : Path.GetFullPath(rawPath);
        return (
            fullPath,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1);
    }

    public static bool AreSameTypeMethodOverloads(IReadOnlyList<ISymbol> candidates)
    {
        if (candidates.Count <= 1 || candidates[0] is not IMethodSymbol first)
        {
            return false;
        }

        if (first.ContainingType is null)
        {
            return false;
        }

        for (int i = 1; i < candidates.Count; i++)
        {
            if (candidates[i] is not IMethodSymbol method ||
                !string.Equals(method.Name, first.Name, StringComparison.Ordinal) ||
                !SymbolEqualityComparer.Default.Equals(method.ContainingType, first.ContainingType))
            {
                return false;
            }
        }

        return true;
    }

    public static string BuildDottedSymbolName(ISymbol symbol)
    {
        Stack<string> parts = new();
        ISymbol? current = symbol;
        while (current is not null)
        {
            if (current is INamespaceSymbol ns)
            {
                if (!ns.IsGlobalNamespace && !string.IsNullOrEmpty(ns.Name))
                {
                    parts.Push(ns.Name);
                }
            }
            else if (!string.IsNullOrEmpty(current.Name))
            {
                parts.Push(current.Name);
            }

            current = current.ContainingSymbol;
        }

        return string.Join(".", parts);
    }

    public static object FormatSymbolCandidate(ISymbol symbol)
    {
        (string? filePath, int? line, int? column) = GetPrimaryDeclarationLocation(symbol);
        return new
        {
            symbol_display = symbol.ToDisplayString(),
            symbol_kind = symbol.Kind.ToString(),
            qualified_name = BuildDottedSymbolName(symbol),
            file_path = filePath,
            line,
            column,
        };
    }

    private static bool MatchesParsedQuery(ISymbol candidate, ParsedSymbolQuery parsed)
    {
        if (!MatchesQualifiedSymbolName(candidate, parsed.QualifiedName, StringComparison.Ordinal))
        {
            // Also allow `Type..ctor` or `Type.ctor` to match constructors of `Type`.
            bool isCtorAlias = candidate is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor &&
                               parsed.Segments.Length >= 2 &&
                               (string.Equals(parsed.LookupName, ".ctor", StringComparison.Ordinal) ||
                                string.Equals(parsed.LookupName, "ctor", StringComparison.OrdinalIgnoreCase)) &&
                               MatchesQualifiedSymbolName(
                                   ctor.ContainingType,
                                   string.Join(".", parsed.Segments[..^1].Select(StripGenericSuffix)),
                                   StringComparison.Ordinal);
            if (!isCtorAlias)
            {
                return false;
            }
        }

        if (parsed.GenericArity.HasValue)
        {
            int candidateArity = candidate switch
            {
                INamedTypeSymbol t => t.Arity,
                IMethodSymbol m => m.Arity,
                _ => -1,
            };
            if (candidateArity != parsed.GenericArity.Value)
            {
                return false;
            }
        }

        if (parsed.ParameterTypes is not null)
        {
            if (candidate is not IMethodSymbol method)
            {
                return false;
            }

            if (!MatchesParameterTypes(method, parsed.ParameterTypes))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesParameterTypes(IMethodSymbol method, IReadOnlyList<string> expectedTypes)
    {
        if (method.Parameters.Length != expectedTypes.Count)
        {
            return false;
        }

        for (int i = 0; i < expectedTypes.Count; i++)
        {
            if (!MatchesSingleParameterType(method.Parameters[i], expectedTypes[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesSingleParameterType(IParameterSymbol parameter, string rawExpected)
    {
        string expected = rawExpected.Trim();
        if (expected.StartsWith("params ", StringComparison.Ordinal))
        {
            expected = expected["params ".Length..].TrimStart();
        }

        if (expected.StartsWith("scoped ", StringComparison.Ordinal))
        {
            expected = expected["scoped ".Length..].TrimStart();
        }

        RefKind? requiredRefKind = null;
        if (expected.StartsWith("ref readonly ", StringComparison.Ordinal))
        {
            requiredRefKind = RefKind.RefReadOnlyParameter;
            expected = expected["ref readonly ".Length..].TrimStart();
        }
        else if (expected.StartsWith("ref ", StringComparison.Ordinal))
        {
            requiredRefKind = RefKind.Ref;
            expected = expected["ref ".Length..].TrimStart();
        }
        else if (expected.StartsWith("out ", StringComparison.Ordinal))
        {
            requiredRefKind = RefKind.Out;
            expected = expected["out ".Length..].TrimStart();
        }
        else if (expected.StartsWith("in ", StringComparison.Ordinal))
        {
            requiredRefKind = RefKind.In;
            expected = expected["in ".Length..].TrimStart();
        }

        if (requiredRefKind.HasValue && parameter.RefKind != requiredRefKind.Value)
        {
            return false;
        }

        if (expected.StartsWith("global::", StringComparison.Ordinal))
        {
            expected = expected["global::".Length..];
        }

        string normalizedExpected = NormalizeTypeWhitespace(expected);
        string minimal = NormalizeTypeWhitespace(parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        if (string.Equals(minimal, normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        string display = NormalizeTypeWhitespace(parameter.Type.ToDisplayString());
        if (string.Equals(display, normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        string full = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (full.StartsWith("global::", StringComparison.Ordinal))
        {
            full = full["global::".Length..];
        }

        string normalizedFull = NormalizeTypeWhitespace(full);
        if (string.Equals(normalizedFull, normalizedExpected, StringComparison.Ordinal) ||
            normalizedFull.EndsWith("." + normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(parameter.Type.Name) &&
            string.Equals(parameter.Type.Name, normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeTypeWhitespace(string value)
        => string.Join(string.Empty, value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool MatchesQualifiedSymbolName(ISymbol symbol, string query, StringComparison comparison)
    {
        string qualified = BuildDottedSymbolName(symbol);
        if (string.Equals(qualified, query, comparison))
        {
            return true;
        }

        return qualified.EndsWith("." + query, comparison);
    }

    private static ParsedSymbolQuery? ParseQuery(string symbolQuery)
    {
        string normalized = symbolQuery.Trim();
        if (normalized.StartsWith("global::", StringComparison.Ordinal))
        {
            normalized = normalized["global::".Length..];
        }

        IReadOnlyList<string>? parameterTypes = null;
        int parenIndex = normalized.IndexOf('(');
        if (parenIndex > 0)
        {
            int closeIndex = normalized.LastIndexOf(')');
            if (closeIndex > parenIndex)
            {
                string paramText = normalized[(parenIndex + 1)..closeIndex].Trim();
                parameterTypes = paramText.Length == 0
                    ? Array.Empty<string>()
                    : SplitTopLevelCommaSeparated(paramText);
            }

            normalized = normalized[..parenIndex].Trim();
        }

        // Preserve `..ctor` as a `.ctor` leaf segment.
        bool trailingDotCtor = normalized.EndsWith("..ctor", StringComparison.Ordinal);
        string splitSource = trailingDotCtor ? normalized[..^6] : normalized;
        string[] rawSegments = splitSource.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (trailingDotCtor)
        {
            rawSegments = rawSegments.Append(".ctor").ToArray();
        }

        if (rawSegments.Length == 0)
        {
            return null;
        }

        string leafSegment = rawSegments[^1];
        int? genericArity = ParseGenericArity(leafSegment);
        string lookupName = StripGenericSuffix(leafSegment);
        string qualifiedName = string.Join(".", rawSegments.Select(StripGenericSuffix));

        return new ParsedSymbolQuery(rawSegments, lookupName, qualifiedName, genericArity, parameterTypes);
    }

    private static int? ParseGenericArity(string segment)
    {
        int backtick = segment.IndexOf('`');
        if (backtick > 0 && int.TryParse(segment[(backtick + 1)..], out int arity))
        {
            return arity;
        }

        int lt = segment.IndexOf('<');
        int gt = segment.LastIndexOf('>');
        if (lt > 0 && gt > lt)
        {
            string inner = segment[(lt + 1)..gt].Trim();
            if (inner.Length == 0)
            {
                return 1;
            }

            return SplitTopLevelCommaSeparated(inner).Count;
        }

        return null;
    }

    private static string StripGenericSuffix(string segment)
    {
        int lt = segment.IndexOf('<');
        string result = lt > 0 ? segment[..lt] : segment;
        int backtick = result.IndexOf('`');
        if (backtick > 0)
        {
            result = result[..backtick];
        }

        return result;
    }

    private static List<string> SplitTopLevelCommaSeparated(string text)
    {
        List<string> parts = new();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(' or '[')
            {
                depth++;
            }
            else if (c is '>' or ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        if (start <= text.Length)
        {
            parts.Add(text[start..].Trim());
        }

        return parts;
    }

    private sealed record ParsedSymbolQuery(
        string[] Segments,
        string LookupName,
        string QualifiedName,
        int? GenericArity,
        IReadOnlyList<string>? ParameterTypes);
}
