using RoslynAgent.Benchmark.Rq1;
using RoslynAgent.Benchmark.AgentEval;

if (args.Length == 0 || IsHelp(args[0]))
{
    PrintHelp();
    return 0;
}

string verb = args[0];
if (!string.Equals(verb, "rq1", StringComparison.OrdinalIgnoreCase))
{
    if (string.Equals(verb, "agent-eval-score", StringComparison.OrdinalIgnoreCase))
    {
        string? manifestPath = TryGetOption(args, "--manifest");
        string? runsPath = TryGetOption(args, "--runs");
        string? evalOutputDir = TryGetOption(args, "--output");

        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(runsPath))
        {
            Console.Error.WriteLine("Usage: agent-eval-score --manifest <path> --runs <dir> [--output <dir>]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(evalOutputDir))
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            evalOutputDir = Path.Combine("artifacts", "agent-eval", stamp);
        }

        AgentEvalScorer scorer = new();
        AgentEvalReport evalReport;
        try
        {
            evalReport = await scorer.ScoreAsync(manifestPath, runsPath, evalOutputDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Agent eval scoring failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Agent eval scoring completed.");
        Console.WriteLine($"Experiment: {evalReport.experiment_id}");
        Console.WriteLine($"Run count: {evalReport.total_runs}");
        Console.WriteLine($"Report path: {evalReport.output_path}");
        if (evalReport.primary_comparison is not null)
        {
            Console.WriteLine($"Control: {evalReport.primary_comparison.control_condition_id}");
            Console.WriteLine($"Treatment: {evalReport.primary_comparison.treatment_condition_id}");
            Console.WriteLine($"Success delta: {evalReport.primary_comparison.success_rate_delta:P2}");
            Console.WriteLine($"Compile delta: {evalReport.primary_comparison.compile_rate_delta:P2}");
            Console.WriteLine($"Tests delta: {evalReport.primary_comparison.tests_rate_delta:P2}");
            Console.WriteLine($"Roslyn use rate in treatment: {evalReport.primary_comparison.roslyn_used_rate_in_treatment:P2}");
        }

        return 0;
    }

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

Console.WriteLine($"RQ1 component benchmark completed.");
Console.WriteLine($"Scenario count: {report.Summary.scenario_count}");
Console.WriteLine($"Structured accuracy: {report.Summary.structured_accuracy:P2}");
Console.WriteLine($"Grep accuracy: {report.Summary.grep_accuracy:P2}");
Console.WriteLine($"Accuracy delta: {report.Summary.accuracy_delta:P2}");
Console.WriteLine($"Benchmark type: {report.BenchmarkType}");
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
          agent-eval-score --manifest <path> --runs <dir> [--output <dir>]

        Notes:
          - Default scenario file: benchmarks/scenarios/rq1-structured-vs-grep.json
          - Both commands write JSON reports under the output directory.
        """);
}
