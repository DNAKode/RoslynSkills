namespace RoslynAgent.Benchmark.AgentEval;

public sealed class AgentEvalGateRunner
{
    public async Task<AgentEvalGateReport> RunAsync(
        string manifestPath,
        string runsDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string validationDir = Path.Combine(outputDirectory, "validation");
        string scoreDir = Path.Combine(outputDirectory, "score");
        string summaryDir = Path.Combine(outputDirectory, "summary");

        AgentEvalManifestValidator manifestValidator = new();
        AgentEvalRunValidator runValidator = new();
        AgentEvalScorer scorer = new();
        AgentEvalReportExporter exporter = new();

        AgentEvalManifestValidationReport manifestValidation = await manifestValidator.ValidateAsync(
            manifestPath,
            validationDir,
            cancellationToken).ConfigureAwait(false);
        AgentEvalRunValidationReport runValidation = await runValidator.ValidateAsync(
            manifestPath,
            runsDirectory,
            validationDir,
            cancellationToken).ConfigureAwait(false);
        AgentEvalReport scoreReport = await scorer.ScoreAsync(
            manifestPath,
            runsDirectory,
            scoreDir,
            cancellationToken).ConfigureAwait(false);
        string summaryPath = await exporter.ExportMarkdownAsync(
            scoreReport.output_path,
            runValidation.output_path,
            summaryDir,
            cancellationToken).ConfigureAwait(false);

        bool sufficientData = scoreReport.primary_comparison?.sufficient_data == true;
        bool gatePassed = manifestValidation.valid && runValidation.valid && sufficientData;

        List<string> notes = new();
        if (!manifestValidation.valid)
        {
            notes.Add("Manifest validation includes errors.");
        }

        if (!runValidation.valid)
        {
            notes.Add("Run validation includes errors.");
        }

        if (!sufficientData)
        {
            notes.Add("Primary comparison has insufficient control/treatment data.");
        }

        if (runValidation.warning_count > 0)
        {
            notes.Add($"Run validation reported {runValidation.warning_count} warning(s).");
        }

        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-gate-report.json"));
        AgentEvalGateReport report = new(
            experiment_id: manifestValidation.experiment_id,
            generated_utc: DateTimeOffset.UtcNow,
            manifest_valid: manifestValidation.valid,
            runs_valid: runValidation.valid,
            sufficient_data: sufficientData,
            gate_passed: gatePassed,
            run_validation_error_count: runValidation.error_count,
            run_validation_warning_count: runValidation.warning_count,
            manifest_validation_path: manifestValidation.output_path,
            run_validation_path: runValidation.output_path,
            score_report_path: scoreReport.output_path,
            summary_path: summaryPath,
            notes: notes,
            output_path: outputPath);

        AgentEvalStorage.WriteJson(outputPath, report);
        return report;
    }
}
