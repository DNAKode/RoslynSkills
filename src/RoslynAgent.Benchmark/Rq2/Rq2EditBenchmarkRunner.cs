using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using RoslynAgent.Core.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RoslynAgent.Benchmark.Rq2;

public sealed class Rq2EditBenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<Rq2EditBenchmarkReport> RunAsync(
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

        Rq2EditScenario[] scenarios = LoadScenarios(fullScenarioPath);
        if (scenarios.Length == 0)
        {
            throw new InvalidOperationException("No scenarios found in scenario file.");
        }

        string scenarioBaseDirectory = Path.GetDirectoryName(fullScenarioPath) ?? Directory.GetCurrentDirectory();
        RenameSymbolCommand structuredRename = new();
        List<Rq2EditScenarioResult> results = new();

        foreach (Rq2EditScenario scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fixturePath = ResolveFixturePath(scenario.FixturePath, scenarioBaseDirectory);
            if (!File.Exists(fixturePath))
            {
                throw new FileNotFoundException($"Fixture file for scenario '{scenario.Id}' was not found.", fixturePath);
            }

            string source = await File.ReadAllTextAsync(fixturePath, cancellationToken).ConfigureAwait(false);
            string oldName = ExtractAnchorIdentifier(
                source,
                fixturePath,
                scenario.AnchorLine,
                scenario.AnchorColumn);

            string structuredOutput = await RunStructuredRenameAsync(
                structuredRename,
                source,
                fixturePath,
                scenario.AnchorLine,
                scenario.AnchorColumn,
                scenario.NewName,
                cancellationToken).ConfigureAwait(false);

            string textOutput = RunTextBaseline(source, oldName, scenario.NewName);
            AssertionResult structuredAssertions = EvaluateAssertions(structuredOutput, scenario);
            AssertionResult textAssertions = EvaluateAssertions(textOutput, scenario);

            results.Add(new Rq2EditScenarioResult(
                scenario_id: scenario.Id,
                description: scenario.Description,
                fixture_path: fixturePath,
                old_name: oldName,
                new_name: scenario.NewName,
                anchor_line: scenario.AnchorLine,
                anchor_column: scenario.AnchorColumn,
                structured_pass: structuredAssertions.pass,
                text_pass: textAssertions.pass,
                structured_required_missing: structuredAssertions.missing_required,
                structured_forbidden_present: structuredAssertions.forbidden_present,
                text_required_missing: textAssertions.missing_required,
                text_forbidden_present: textAssertions.forbidden_present,
                notes: scenario.Notes));
        }

        Rq2EditSummary summary = BuildSummary(results);
        Rq2EditBenchmarkReport report = new(
            BenchmarkType: "component-diagnostic",
            GeneratedUtc: DateTimeOffset.UtcNow,
            ScenarioSource: fullScenarioPath,
            Summary: summary,
            Results: results,
            ArtifactPath: Path.GetFullPath(Path.Combine(outputDirectory, "rq2-edit-report.json")));

        Directory.CreateDirectory(Path.GetDirectoryName(report.ArtifactPath)!);
        string reportJson = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(report.ArtifactPath, reportJson, cancellationToken).ConfigureAwait(false);

        return report;
    }

    private static Rq2EditSummary BuildSummary(IReadOnlyList<Rq2EditScenarioResult> results)
    {
        int scenarioCount = results.Count;
        int structuredPass = results.Count(r => r.structured_pass);
        int textPass = results.Count(r => r.text_pass);
        double structuredAccuracy = scenarioCount == 0 ? 0 : (double)structuredPass / scenarioCount;
        double textAccuracy = scenarioCount == 0 ? 0 : (double)textPass / scenarioCount;

        return new Rq2EditSummary(
            scenario_count: scenarioCount,
            structured_pass: structuredPass,
            text_pass: textPass,
            structured_accuracy: structuredAccuracy,
            text_accuracy: textAccuracy,
            accuracy_delta: structuredAccuracy - textAccuracy);
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

    private static Rq2EditScenario[] LoadScenarios(string scenarioPath)
    {
        string json = File.ReadAllText(scenarioPath);
        Rq2EditScenario[]? scenarios = JsonSerializer.Deserialize<Rq2EditScenario[]>(json, JsonOptions);
        return scenarios ?? Array.Empty<Rq2EditScenario>();
    }

    private static async Task<string> RunStructuredRenameAsync(
        RenameSymbolCommand structuredRename,
        string source,
        string fixturePath,
        int anchorLine,
        int anchorColumn,
        string newName,
        CancellationToken cancellationToken)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"rq2-edit-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(tempFile, source, cancellationToken).ConfigureAwait(false);

            JsonElement input = ToJsonElement(new
            {
                file_path = tempFile,
                line = anchorLine,
                column = anchorColumn,
                new_name = newName,
                apply = true,
                max_diagnostics = 200,
            });

            CommandExecutionResult result = await structuredRename.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                string errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}:{e.Message}"));
                throw new InvalidOperationException(
                    $"Structured rename failed for fixture '{fixturePath}' at {anchorLine}:{anchorColumn}: {errors}");
            }

            return await File.ReadAllTextAsync(tempFile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static string RunTextBaseline(string source, string oldName, string newName)
    {
        Regex symbolPattern = new(@"\b" + Regex.Escape(oldName) + @"\b", RegexOptions.CultureInvariant);
        return symbolPattern.Replace(source, newName);
    }

    private static string ExtractAnchorIdentifier(
        string source,
        string filePath,
        int anchorLine,
        int anchorColumn)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath);
        SyntaxNode root = syntaxTree.GetRoot();
        SourceText sourceText = syntaxTree.GetText();
        if (anchorLine < 1 || anchorLine > sourceText.Lines.Count)
        {
            throw new InvalidOperationException($"Anchor line '{anchorLine}' is out of range for '{filePath}'.");
        }

        TextLine line = sourceText.Lines[anchorLine - 1];
        int position = line.Start + Math.Min(Math.Max(anchorColumn - 1, 0), Math.Max(line.Span.Length - 1, 0));
        SyntaxToken token = root.FindToken(position);
        if (!token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken) && position > 0)
        {
            token = root.FindToken(position - 1);
        }

        if (!token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
        {
            throw new InvalidOperationException(
                $"Anchor location {anchorLine}:{anchorColumn} in '{filePath}' did not resolve to an identifier token.");
        }

        return token.ValueText;
    }

    private static AssertionResult EvaluateAssertions(string output, Rq2EditScenario scenario)
    {
        string[] missingRequired = scenario.RequiredSubstrings
            .Where(s => !output.Contains(s, StringComparison.Ordinal))
            .ToArray();
        string[] forbiddenPresent = scenario.ForbiddenSubstrings
            .Where(s => output.Contains(s, StringComparison.Ordinal))
            .ToArray();

        bool pass = missingRequired.Length == 0 && forbiddenPresent.Length == 0;
        return new AssertionResult(pass, missingRequired, forbiddenPresent);
    }

    private static JsonElement ToJsonElement(object value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed record AssertionResult(
        bool pass,
        string[] missing_required,
        string[] forbidden_present);
}

public sealed record Rq2EditScenario(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("fixture_path")] string FixturePath,
    [property: JsonPropertyName("anchor_line")] int AnchorLine,
    [property: JsonPropertyName("anchor_column")] int AnchorColumn,
    [property: JsonPropertyName("new_name")] string NewName,
    [property: JsonPropertyName("required_substrings")] string[] RequiredSubstrings,
    [property: JsonPropertyName("forbidden_substrings")] string[] ForbiddenSubstrings,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record Rq2EditSummary(
    int scenario_count,
    int structured_pass,
    int text_pass,
    double structured_accuracy,
    double text_accuracy,
    double accuracy_delta);

public sealed record Rq2EditScenarioResult(
    string scenario_id,
    string description,
    string fixture_path,
    string old_name,
    string new_name,
    int anchor_line,
    int anchor_column,
    bool structured_pass,
    bool text_pass,
    string[] structured_required_missing,
    string[] structured_forbidden_present,
    string[] text_required_missing,
    string[] text_forbidden_present,
    string? notes);

public sealed record Rq2EditBenchmarkReport(
    string BenchmarkType,
    DateTimeOffset GeneratedUtc,
    string ScenarioSource,
    Rq2EditSummary Summary,
    IReadOnlyList<Rq2EditScenarioResult> Results,
    string ArtifactPath);
