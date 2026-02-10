namespace RoslynSkills.Benchmark.AgentEval;

public sealed class AgentEvalManifestValidator
{
    public Task<AgentEvalManifestValidationReport> ValidateAsync(
        string manifestPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentEvalManifest manifest = AgentEvalStorage.LoadManifest(manifestPath);
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();

        List<AgentEvalManifestValidationIssue> issues = new();
        foreach (AgentEvalTask task in manifest.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.RepoUrl))
            {
                issues.Add(new AgentEvalManifestValidationIssue(
                    severity: "warning",
                    task_id: task.Id,
                    message: "repo_url is missing; clone automation will need manual mapping."));
            }

            if (!string.IsNullOrWhiteSpace(task.TaskPromptFile))
            {
                string promptPath = ResolvePath(task.TaskPromptFile, manifestDirectory);
                if (!File.Exists(promptPath))
                {
                    issues.Add(new AgentEvalManifestValidationIssue(
                        severity: "error",
                        task_id: task.Id,
                        message: $"task_prompt_file was not found: {task.TaskPromptFile}"));
                }
            }
            else
            {
                issues.Add(new AgentEvalManifestValidationIssue(
                    severity: "warning",
                    task_id: task.Id,
                    message: "task_prompt_file is missing; agent task instruction source is unclear."));
            }

            if (!string.IsNullOrWhiteSpace(task.IssueUrl) && !Uri.IsWellFormedUriString(task.IssueUrl, UriKind.Absolute))
            {
                issues.Add(new AgentEvalManifestValidationIssue(
                    severity: "error",
                    task_id: task.Id,
                    message: $"issue_url is not a valid absolute URI: {task.IssueUrl}"));
            }
        }

        bool valid = issues.All(i => !string.Equals(i.severity, "error", StringComparison.OrdinalIgnoreCase));
        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-manifest-validation.json"));
        AgentEvalManifestValidationReport report = new(
            experiment_id: manifest.ExperimentId,
            generated_utc: DateTimeOffset.UtcNow,
            valid: valid,
            issue_count: issues.Count,
            issues: issues,
            output_path: outputPath);

        AgentEvalStorage.WriteJson(outputPath, report);
        return Task.FromResult(report);
    }

    private static string ResolvePath(string path, string baseDirectory)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }
}

