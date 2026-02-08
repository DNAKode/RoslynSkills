using RoslynAgent.Core.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RoslynAgent.Benchmark.Rq1;

public sealed class Rq1BenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<Rq1BenchmarkReport> RunAsync(
        string scenarioPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scenarioPath))
        {
            throw new ArgumentException("Scenario path must be provided.", nameof(scenarioPath));
        }

        string fullScenarioPath = Path.GetFullPath(scenarioPath);
        if (!File.Exists(fullScenarioPath))
        {
            throw new FileNotFoundException("Scenario file not found.", fullScenarioPath);
        }

        Rq1Scenario[] scenarios = LoadScenarios(fullScenarioPath);
        if (scenarios.Length == 0)
        {
            throw new InvalidOperationException("No scenarios found in scenario file.");
        }

        string scenarioBaseDirectory = Path.GetDirectoryName(fullScenarioPath) ?? Directory.GetCurrentDirectory();
        FindSymbolCommand structuredCommand = new();
        List<Rq1ScenarioResult> caseResults = new();

        foreach (Rq1Scenario scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fixturePath = ResolveFixturePath(scenario.FixturePath, scenarioBaseDirectory);
            if (!File.Exists(fixturePath))
            {
                throw new FileNotFoundException($"Fixture file for scenario '{scenario.Id}' was not found.", fixturePath);
            }

            ScenarioEvaluation evaluation = await EvaluateScenarioAsync(
                scenario,
                fixturePath,
                structuredCommand,
                cancellationToken).ConfigureAwait(false);
            caseResults.Add(evaluation.Result);
        }

        Rq1Summary summary = BuildSummary(caseResults);
        Rq1BenchmarkReport report = new(
            BenchmarkType: "component-diagnostic",
            GeneratedUtc: DateTimeOffset.UtcNow,
            ScenarioSource: fullScenarioPath,
            Summary: summary,
            Results: caseResults,
            ArtifactPath: Path.GetFullPath(Path.Combine(outputDirectory, "rq1-report.json")));

        Directory.CreateDirectory(Path.GetDirectoryName(report.ArtifactPath)!);
        string reportJson = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(report.ArtifactPath, reportJson, cancellationToken).ConfigureAwait(false);

        return report;
    }

    private static Rq1Summary BuildSummary(IReadOnlyList<Rq1ScenarioResult> results)
    {
        int scenarioCount = results.Count;
        int structuredCorrectCount = results.Count(r => r.structured_correct);
        int grepCorrectCount = results.Count(r => r.grep_correct);

        double structuredAccuracy = scenarioCount == 0 ? 0 : (double)structuredCorrectCount / scenarioCount;
        double grepAccuracy = scenarioCount == 0 ? 0 : (double)grepCorrectCount / scenarioCount;

        double avgStructuredCandidates = scenarioCount == 0 ? 0 : results.Average(r => r.structured_candidate_count);
        double avgGrepCandidates = scenarioCount == 0 ? 0 : results.Average(r => r.grep_candidate_count);

        return new Rq1Summary(
            scenario_count: scenarioCount,
            structured_correct: structuredCorrectCount,
            grep_correct: grepCorrectCount,
            structured_accuracy: structuredAccuracy,
            grep_accuracy: grepAccuracy,
            accuracy_delta: structuredAccuracy - grepAccuracy,
            average_structured_candidates: avgStructuredCandidates,
            average_grep_candidates: avgGrepCandidates);
    }

    private static string ResolveFixturePath(string fixturePath, string scenarioBaseDirectory)
    {
        if (Path.IsPathRooted(fixturePath))
        {
            return fixturePath;
        }

        string direct = Path.GetFullPath(fixturePath);
        if (File.Exists(direct))
        {
            return direct;
        }

        return Path.GetFullPath(Path.Combine(scenarioBaseDirectory, fixturePath));
    }

    private static Rq1Scenario[] LoadScenarios(string scenarioPath)
    {
        string json = File.ReadAllText(scenarioPath);
        Rq1Scenario[]? scenarios = JsonSerializer.Deserialize<Rq1Scenario[]>(json, JsonOptions);
        return scenarios ?? Array.Empty<Rq1Scenario>();
    }

    private static async Task<ScenarioEvaluation> EvaluateScenarioAsync(
        Rq1Scenario scenario,
        string fixturePath,
        FindSymbolCommand structuredCommand,
        CancellationToken cancellationToken)
    {
        JsonElement input = ToJsonElement(new
        {
            file_path = fixturePath,
            symbol_name = scenario.SymbolName,
            context_lines = 2,
            max_results = 100,
        });

        var structuredExecution = await structuredCommand.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        if (!structuredExecution.Ok)
        {
            string errorSummary = string.Join("; ", structuredExecution.Errors.Select(e => $"{e.Code}:{e.Message}"));
            throw new InvalidOperationException($"Structured command failed for scenario '{scenario.Id}': {errorSummary}");
        }

        FindSymbolResponse structuredData = ParseStructuredResponse(structuredExecution.Data);
        IReadOnlyList<GrepMatch> grepMatches = GrepLikeSearch.Find(fixturePath, scenario.SymbolName);

        StructuredMatch? structuredSelection = StructuredSelection.Select(structuredData.matches, scenario);
        GrepMatch? grepSelection = GrepSelection.Select(grepMatches, scenario);

        bool structuredCorrect = IsCorrect(structuredSelection?.line, scenario.ExpectedLine);
        bool grepCorrect = IsCorrect(grepSelection?.line, scenario.ExpectedLine);

        return new ScenarioEvaluation(
            new Rq1ScenarioResult(
                scenario_id: scenario.Id,
                description: scenario.Description,
                fixture_path: fixturePath,
                symbol_name: scenario.SymbolName,
                expected_line: scenario.ExpectedLine,
                expected_kind: scenario.ExpectedKind,
                expected_containing_type: scenario.TargetContainingType,
                structured_selected_line: structuredSelection?.line,
                structured_selected_syntax_kind: structuredSelection?.syntax_kind,
                structured_correct: structuredCorrect,
                structured_candidate_count: structuredData.matches.Length,
                grep_selected_line: grepSelection?.line,
                grep_correct: grepCorrect,
                grep_candidate_count: grepMatches.Count,
                notes: scenario.Notes));
    }

    private static bool IsCorrect(int? selectedLine, int expectedLine)
        => selectedLine.HasValue && selectedLine.Value == expectedLine;

    private static FindSymbolResponse ParseStructuredResponse(object? data)
    {
        if (data is null)
        {
            throw new InvalidOperationException("Structured command returned null data.");
        }

        string json = JsonSerializer.Serialize(data, JsonOptions);
        FindSymbolResponse? response = JsonSerializer.Deserialize<FindSymbolResponse>(json, JsonOptions);
        if (response is null)
        {
            throw new InvalidOperationException("Failed to parse structured command response.");
        }

        return response;
    }

    private static JsonElement ToJsonElement(object value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed record ScenarioEvaluation(Rq1ScenarioResult Result);

    private sealed record FindSymbolResponse(
        FindSymbolQuery query,
        int total_matches,
        StructuredMatch[] matches);

    private sealed record FindSymbolQuery(
        string file_path,
        string symbol_name,
        int max_results,
        int context_lines);

    private sealed record StructuredMatch(
        string text,
        string syntax_kind,
        bool is_declaration,
        int line,
        int column,
        StructuredHierarchy hierarchy,
        StructuredContext context);

    private sealed record StructuredHierarchy(
        string? namespace_name,
        string[] containing_types,
        string? containing_member_kind);

    private sealed record StructuredContext(
        int start_line,
        int end_line,
        string snippet);

    private sealed record GrepMatch(
        int line,
        string line_text);

    private static class GrepLikeSearch
    {
        public static IReadOnlyList<GrepMatch> Find(string filePath, string symbolName)
        {
            Regex symbolPattern = new(@"\b" + Regex.Escape(symbolName) + @"\b", RegexOptions.CultureInvariant);
            string[] lines = File.ReadAllLines(filePath);
            List<GrepMatch> matches = new();

            for (int i = 0; i < lines.Length; i++)
            {
                if (symbolPattern.IsMatch(lines[i]))
                {
                    matches.Add(new GrepMatch(i + 1, lines[i]));
                }
            }

            return matches;
        }
    }

    private static class StructuredSelection
    {
        public static StructuredMatch? Select(IReadOnlyList<StructuredMatch> candidates, Rq1Scenario scenario)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            IEnumerable<StructuredMatch> query = candidates;
            if (scenario.ExpectDeclaration)
            {
                query = query.Where(c => c.is_declaration);
            }

            if (!string.IsNullOrWhiteSpace(scenario.TargetContainingType))
            {
                query = query.Where(c =>
                    c.hierarchy.containing_types.Length > 0 &&
                    string.Equals(
                        c.hierarchy.containing_types[^1],
                        scenario.TargetContainingType,
                        StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(scenario.ExpectedKind))
            {
                query = query.Where(c => string.Equals(
                    MapSyntaxKindToKind(c.syntax_kind),
                    scenario.ExpectedKind,
                    StringComparison.OrdinalIgnoreCase));
            }

            return query.FirstOrDefault() ?? candidates[0];
        }
    }

    private static class GrepSelection
    {
        public static GrepMatch? Select(IReadOnlyList<GrepMatch> candidates, Rq1Scenario scenario)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            if (scenario.ExpectDeclaration)
            {
                Regex declarationHint = BuildDeclarationHintPattern(scenario.SymbolName, scenario.ExpectedKind);
                GrepMatch? declarationMatch = candidates.FirstOrDefault(c => declarationHint.IsMatch(c.line_text));
                if (declarationMatch is not null)
                {
                    return declarationMatch;
                }
            }

            return candidates[0];
        }

        private static Regex BuildDeclarationHintPattern(string symbolName, string? expectedKind)
        {
            string escaped = Regex.Escape(symbolName);
            string pattern = expectedKind?.ToLowerInvariant() switch
            {
                "type" => $@"\b(class|struct|interface|record|enum)\s+{escaped}\b",
                "property" => $@"\b{escaped}\b\s*\{{",
                "method" => $@"\b{escaped}\s*\(",
                _ => $@"\b{escaped}\b",
            };

            return new Regex(pattern, RegexOptions.CultureInvariant);
        }
    }

    private static string MapSyntaxKindToKind(string syntaxKind)
    {
        return syntaxKind switch
        {
            "ClassDeclaration" or "StructDeclaration" or "InterfaceDeclaration" or "RecordDeclaration" or "EnumDeclaration"
                => "type",
            "MethodDeclaration" or "ConstructorDeclaration"
                => "method",
            "PropertyDeclaration" => "property",
            "FieldDeclaration" or "VariableDeclarator" => "field",
            _ => "other",
        };
    }
}

public sealed record Rq1Scenario(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("fixture_path")] string FixturePath,
    [property: JsonPropertyName("symbol_name")] string SymbolName,
    [property: JsonPropertyName("expect_declaration")] bool ExpectDeclaration,
    [property: JsonPropertyName("expected_kind")] string ExpectedKind,
    [property: JsonPropertyName("target_containing_type")] string? TargetContainingType,
    [property: JsonPropertyName("expected_line")] int ExpectedLine,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record Rq1Summary(
    int scenario_count,
    int structured_correct,
    int grep_correct,
    double structured_accuracy,
    double grep_accuracy,
    double accuracy_delta,
    double average_structured_candidates,
    double average_grep_candidates);

public sealed record Rq1ScenarioResult(
    string scenario_id,
    string description,
    string fixture_path,
    string symbol_name,
    int expected_line,
    string expected_kind,
    string? expected_containing_type,
    int? structured_selected_line,
    string? structured_selected_syntax_kind,
    bool structured_correct,
    int structured_candidate_count,
    int? grep_selected_line,
    bool grep_correct,
    int grep_candidate_count,
    string? notes);

public sealed record Rq1BenchmarkReport(
    string BenchmarkType,
    DateTimeOffset GeneratedUtc,
    string ScenarioSource,
    Rq1Summary Summary,
    IReadOnlyList<Rq1ScenarioResult> Results,
    string ArtifactPath);
