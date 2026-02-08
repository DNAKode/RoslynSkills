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
            Console.WriteLine($"Control runs: {evalReport.primary_comparison.control_run_count}");
            Console.WriteLine($"Treatment runs: {evalReport.primary_comparison.treatment_run_count}");
            if (evalReport.primary_comparison.sufficient_data)
            {
                Console.WriteLine($"Success delta: {evalReport.primary_comparison.success_rate_delta:P2}");
                Console.WriteLine($"Compile delta: {evalReport.primary_comparison.compile_rate_delta:P2}");
                Console.WriteLine($"Tests delta: {evalReport.primary_comparison.tests_rate_delta:P2}");
                Console.WriteLine($"Roslyn use rate in treatment: {evalReport.primary_comparison.roslyn_used_rate_in_treatment:P2}");
            }
            else
            {
                Console.WriteLine("Deltas unavailable: insufficient data.");
                if (!string.IsNullOrWhiteSpace(evalReport.primary_comparison.note))
                {
                    Console.WriteLine($"Note: {evalReport.primary_comparison.note}");
                }
            }
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

    if (string.Equals(verb, "agent-eval-validate-manifest", StringComparison.OrdinalIgnoreCase))
    {
        string? manifestPath = TryGetOption(args, "--manifest");
        string? outputDir = TryGetOption(args, "--output");

        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            Console.Error.WriteLine("Usage: agent-eval-validate-manifest --manifest <path> [--output <dir>]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            outputDir = Path.Combine("artifacts", "agent-eval", stamp);
        }

        AgentEvalManifestValidator validator = new();
        AgentEvalManifestValidationReport report;
        try
        {
            report = await validator.ValidateAsync(manifestPath, outputDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Manifest validation failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Agent eval manifest validation completed.");
        Console.WriteLine($"Valid: {report.valid}");
        Console.WriteLine($"Issue count: {report.issue_count}");
        foreach (var issue in report.issues.Take(10))
        {
            Console.WriteLine($"- [{issue.severity}] {issue.task_id}: {issue.message}");
        }
        Console.WriteLine($"Validation path: {report.output_path}");
        return report.valid ? 0 : 2;
    }

    if (string.Equals(verb, "agent-eval-validate-runs", StringComparison.OrdinalIgnoreCase))
    {
        string? manifestPath = TryGetOption(args, "--manifest");
        string? runsPath = TryGetOption(args, "--runs");
        string? outputDir = TryGetOption(args, "--output");

        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(runsPath))
        {
            Console.Error.WriteLine("Usage: agent-eval-validate-runs --manifest <path> --runs <dir> [--output <dir>]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            outputDir = Path.Combine("artifacts", "agent-eval", stamp);
        }

        AgentEvalRunValidator validator = new();
        AgentEvalRunValidationReport report;
        try
        {
            report = await validator.ValidateAsync(manifestPath, runsPath, outputDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Run validation failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Agent eval run validation completed.");
        Console.WriteLine($"Valid: {report.valid}");
        Console.WriteLine($"Total runs: {report.total_runs}");
        Console.WriteLine($"Expected runs: {report.expected_runs}");
        Console.WriteLine($"Issue count: {report.issue_count} (errors={report.error_count}, warnings={report.warning_count})");
        Console.WriteLine($"Contaminated control runs: {report.contaminated_control_runs}");
        Console.WriteLine($"Treatment runs missing Roslyn offered: {report.treatment_runs_without_roslyn_offered}");
        Console.WriteLine($"Treatment runs missing Roslyn usage: {report.treatment_runs_without_roslyn_usage}");
        foreach (var issue in report.issues.Take(10))
        {
            Console.WriteLine($"- [{issue.severity}] run={issue.run_id} task={issue.task_id} cond={issue.condition_id} {issue.message}");
        }
        Console.WriteLine($"Validation path: {report.output_path}");
        return report.valid ? 0 : 2;
    }

    if (string.Equals(verb, "agent-eval-register-run", StringComparison.OrdinalIgnoreCase))
    {
        string? manifestPath = TryGetOption(args, "--manifest");
        string? runsPath = TryGetOption(args, "--runs");
        string? taskId = TryGetOption(args, "--task");
        string? conditionId = TryGetOption(args, "--condition");
        string? runId = TryGetOption(args, "--run-id") ?? string.Empty;
        string agent = TryGetOption(args, "--agent") ?? "unknown-agent";
        string model = TryGetOption(args, "--model") ?? "unknown-model";
        int replicate = ParseIntOption(args, "--replicate", 1, min: 1);
        bool succeeded = ParseBoolOption(args, "--succeeded", false);
        bool compilePassed = ParseBoolOption(args, "--compile-passed", false);
        bool testsPassed = ParseBoolOption(args, "--tests-passed", false);
        double durationSeconds = ParseDoubleOption(args, "--duration-seconds", 0);
        bool overwrite = ParseBoolOption(args, "--overwrite", false);
        int? roslynHelpfulness = ParseNullableIntOption(args, "--roslyn-helpfulness");
        string? summary = TryGetOption(args, "--summary");

        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(runsPath) ||
            string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(conditionId))
        {
            Console.Error.WriteLine(
                "Usage: agent-eval-register-run --manifest <path> --runs <dir> --task <id> --condition <id> " +
                "[--run-id <id>] [--replicate <n>] [--agent <id>] [--model <id>] " +
                "--succeeded <true|false> --compile-passed <true|false> --tests-passed <true|false> " +
                "[--duration-seconds <n>] [--tools-offered <a,b,c>] [--tool-calls <tool:true,tool2:false>] " +
                "[--summary <text>] [--helpful-tools <a,b>] [--unhelpful-tools <a,b>] " +
                "[--roslyn-helpfulness <1-5>] [--overwrite <true|false>]");
            return 1;
        }

        IReadOnlyList<string> toolsOffered = ParseCsvOption(args, "--tools-offered");
        IReadOnlyList<AgentToolCall> toolCalls = ParseToolCallsOption(args, "--tool-calls");
        IReadOnlyList<string> helpfulTools = ParseCsvOption(args, "--helpful-tools");
        IReadOnlyList<string> unhelpfulTools = ParseCsvOption(args, "--unhelpful-tools");

        AgentEvalRunRegistration registration = new(
            RunId: runId,
            TaskId: taskId,
            ConditionId: conditionId,
            Replicate: replicate,
            Agent: agent,
            Model: model,
            Succeeded: succeeded,
            CompilePassed: compilePassed,
            TestsPassed: testsPassed,
            DurationSeconds: durationSeconds,
            ToolsOffered: toolsOffered,
            ToolCalls: toolCalls,
            ReflectionSummary: summary,
            ReflectionHelpfulTools: helpfulTools,
            ReflectionUnhelpfulTools: unhelpfulTools,
            RoslynHelpfulnessScore: roslynHelpfulness);

        AgentEvalRunRegistrar registrar = new();
        string outputPath;
        try
        {
            outputPath = await registrar.RegisterAsync(
                manifestPath,
                runsPath,
                registration,
                overwrite,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Run registration failed: {ex.Message}");
            return 1;
        }

        string displayRunId = string.IsNullOrWhiteSpace(runId)
            ? $"{taskId}__{conditionId}__r{replicate:00}"
            : runId;

        Console.WriteLine("Agent eval run registration completed.");
        Console.WriteLine($"Run id: {displayRunId}");
        Console.WriteLine($"Output: {outputPath}");
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
          agent-eval-validate-manifest --manifest <path> [--output <dir>]
          agent-eval-validate-runs --manifest <path> --runs <dir> [--output <dir>]
          agent-eval-register-run --manifest <path> --runs <dir> --task <id> --condition <id> ...

        Notes:
          - Default scenario file: benchmarks/scenarios/rq1-structured-vs-grep.json
          - Both commands write JSON reports under the output directory.
        """);
}

static int ParseIntOption(IReadOnlyList<string> args, string optionName, int defaultValue, int min)
{
    string? value = TryGetOption(args, optionName);
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value, out int parsed))
    {
        return defaultValue;
    }

    return parsed < min ? min : parsed;
}

static double ParseDoubleOption(IReadOnlyList<string> args, string optionName, double defaultValue)
{
    string? value = TryGetOption(args, optionName);
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!double.TryParse(value, out double parsed))
    {
        return defaultValue;
    }

    return parsed;
}

static bool ParseBoolOption(IReadOnlyList<string> args, string optionName, bool defaultValue)
{
    string? value = TryGetOption(args, optionName);
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!bool.TryParse(value, out bool parsed))
    {
        return defaultValue;
    }

    return parsed;
}

static int? ParseNullableIntOption(IReadOnlyList<string> args, string optionName)
{
    string? value = TryGetOption(args, optionName);
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!int.TryParse(value, out int parsed))
    {
        return null;
    }

    return parsed;
}

static IReadOnlyList<string> ParseCsvOption(IReadOnlyList<string> args, string optionName)
{
    string? raw = TryGetOption(args, optionName);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Array.Empty<string>();
    }

    return raw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .ToArray();
}

static IReadOnlyList<AgentToolCall> ParseToolCallsOption(IReadOnlyList<string> args, string optionName)
{
    string? raw = TryGetOption(args, optionName);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Array.Empty<AgentToolCall>();
    }

    List<AgentToolCall> calls = new();
    string[] entries = raw.Split(
        [';', ','],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (string entry in entries)
    {
        string[] parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            continue;
        }

        bool ok = bool.TryParse(parts[1], out bool parsed) && parsed;
        calls.Add(new AgentToolCall(parts[0], ok));
    }

    return calls;
}
