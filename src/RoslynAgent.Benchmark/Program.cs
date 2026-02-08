using RoslynAgent.Benchmark.Rq1;

if (args.Length == 0 || IsHelp(args[0]))
{
    PrintHelp();
    return 0;
}

string verb = args[0];
if (!string.Equals(verb, "rq1", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Unknown command '{verb}'.");
    PrintHelp();
    return 1;
}

string? scenarioPath = TryGetOption(args, "--scenario");
if (string.IsNullOrWhiteSpace(scenarioPath))
{
    scenarioPath = Path.Combine("benchmarks", "scenarios", "rq1-structured-vs-grep.json");
}

string? outputDir = TryGetOption(args, "--output");
if (string.IsNullOrWhiteSpace(outputDir))
{
    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    outputDir = Path.Combine("artifacts", "rq1", stamp);
}

Rq1BenchmarkRunner runner = new();
Rq1BenchmarkReport report;
try
{
    report = await runner.RunAsync(scenarioPath, outputDir, CancellationToken.None).ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"RQ1 benchmark failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"RQ1 benchmark completed.");
Console.WriteLine($"Scenario count: {report.Summary.scenario_count}");
Console.WriteLine($"Structured accuracy: {report.Summary.structured_accuracy:P2}");
Console.WriteLine($"Grep accuracy: {report.Summary.grep_accuracy:P2}");
Console.WriteLine($"Accuracy delta: {report.Summary.accuracy_delta:P2}");
Console.WriteLine($"Artifact path: {report.ArtifactPath}");
return 0;

static bool IsHelp(string value)
    => string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
    || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase)
    || string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

static string? TryGetOption(IReadOnlyList<string> args, string optionName)
{
    for (int i = 1; i < args.Count; i++)
    {
        if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Count)
            {
                return args[i + 1];
            }

            return null;
        }
    }

    return null;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        RoslynAgent.Benchmark

        Commands:
          rq1 [--scenario <path>] [--output <dir>]

        Notes:
          - Default scenario file: benchmarks/scenarios/rq1-structured-vs-grep.json
          - Output is a JSON report written under the output directory.
        """);
}
