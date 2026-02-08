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

    if (string.Equals(verb, "agent-eval-worklist", StringComparison.OrdinalIgnoreCase))
    {
        string? manifestPath = TryGetOption(args, "--manifest");
        string? runsPath = TryGetOption(args, "--runs");
        string? worklistOutputDir = TryGetOption(args, "--output");

        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(runsPath))
        {
            Console.Error.WriteLine("Usage: agent-eval-worklist --manifest <path> --runs <dir> [--output <dir>]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(worklistOutputDir))
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            worklistOutputDir = Path.Combine("artifacts", "agent-eval", stamp);
        }

        AgentEvalWorklistBuilder builder = new();
        AgentEvalWorklistReport worklistReport;
        try
        {
            worklistReport = await builder.BuildAsync(manifestPath, runsPath, worklistOutputDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Agent eval worklist build failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Agent eval worklist generated.");
        Console.WriteLine($"Experiment: {worklistReport.experiment_id}");
        Console.WriteLine($"Expected runs: {worklistReport.expected_runs}");
        Console.WriteLine($"Observed runs: {worklistReport.observed_runs}");
        Console.WriteLine($"Completion: {worklistReport.completion_rate:P2}");
        Console.WriteLine($"Pending runs: {worklistReport.pending_runs.Count}");
        Console.WriteLine($"Worklist path: {worklistReport.output_path}");
        return 0;
    }

    if (string.Equals(verb, "agent-eval-init-runs", StringComparison.OrdinalIgnoreCase))
    {
        string? manifestPath = TryGetOption(args, "--manifest");
        string? runsPath = TryGetOption(args, "--runs");
        string? initOutputDir = TryGetOption(args, "--output");

        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(runsPath))
        {
            Console.Error.WriteLine("Usage: agent-eval-init-runs --manifest <path> --runs <dir> [--output <dir>]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(initOutputDir))
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            initOutputDir = Path.Combine("artifacts", "agent-eval", stamp);
        }

        AgentEvalRunTemplateGenerator generator = new();
        AgentEvalTemplateGenerationReport templateReport;
        try
        {
            templateReport = await generator.GenerateAsync(manifestPath, runsPath, initOutputDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Agent eval run template generation failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Agent eval run template generation completed.");
        Console.WriteLine($"Experiment: {templateReport.experiment_id}");
        Console.WriteLine($"Pending runs: {templateReport.pending_count}");
        Console.WriteLine($"Template files created: {templateReport.template_files_created}");
        Console.WriteLine($"Templates directory: {templateReport.templates_directory}");
        Console.WriteLine($"Worklist path: {templateReport.worklist_path}");
        return 0;
    }

    if (string.Equals(verb, "agent-eval-preflight", StringComparison.OrdinalIgnoreCase))
    {
        string? preflightOutputDir = TryGetOption(args, "--output");
        if (string.IsNullOrWhiteSpace(preflightOutputDir))
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            preflightOutputDir = Path.Combine("artifacts", "agent-eval", stamp);
        }

        AgentEvalPreflightChecker checker = new();
        AgentEvalPreflightReport preflightReport;
        try
        {
            preflightReport = await checker.RunAsync(preflightOutputDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Agent eval preflight failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Agent eval preflight completed.");
        Console.WriteLine($"All required available: {preflightReport.all_required_available}");
        foreach (var item in preflightReport.checks)
        {
            Console.WriteLine($"- {item.command}: {(item.available ? "available" : "missing")} (required={item.required})");
        }
        Console.WriteLine($"Preflight path: {preflightReport.output_path}");
        return preflightReport.all_required_available ? 0 : 2;
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

string? rq1OutputDir = TryGetOption(args, "--output");
if (string.IsNullOrWhiteSpace(rq1OutputDir))
{
    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    rq1OutputDir = Path.Combine("artifacts", "rq1", stamp);
}

Rq1BenchmarkRunner runner = new();
Rq1BenchmarkReport rq1Report;
try
{
    rq1Report = await runner.RunAsync(scenarioPath, rq1OutputDir, CancellationToken.None).ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"RQ1 benchmark failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"RQ1 component benchmark completed.");
Console.WriteLine($"Scenario count: {rq1Report.Summary.scenario_count}");
Console.WriteLine($"Structured accuracy: {rq1Report.Summary.structured_accuracy:P2}");
Console.WriteLine($"Grep accuracy: {rq1Report.Summary.grep_accuracy:P2}");
Console.WriteLine($"Accuracy delta: {rq1Report.Summary.accuracy_delta:P2}");
Console.WriteLine($"Benchmark type: {rq1Report.BenchmarkType}");
Console.WriteLine($"Artifact path: {rq1Report.ArtifactPath}");
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
          agent-eval-worklist --manifest <path> --runs <dir> [--output <dir>]
          agent-eval-init-runs --manifest <path> --runs <dir> [--output <dir>]
          agent-eval-preflight [--output <dir>]

        Notes:
          - Default scenario file: benchmarks/scenarios/rq1-structured-vs-grep.json
          - Both commands write JSON reports under the output directory.
        """);
}
