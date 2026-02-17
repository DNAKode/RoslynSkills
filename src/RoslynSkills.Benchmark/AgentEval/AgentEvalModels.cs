using System.Text.Json.Serialization;

namespace RoslynSkills.Benchmark.AgentEval;

public sealed record AgentEvalManifest(
    [property: JsonPropertyName("experiment_id")] string ExperimentId,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("roslyn_tool_prefixes")] IReadOnlyList<string> RoslynToolPrefixes,
    [property: JsonPropertyName("conditions")] IReadOnlyList<AgentEvalCondition> Conditions,
    [property: JsonPropertyName("tasks")] IReadOnlyList<AgentEvalTask> Tasks,
    [property: JsonPropertyName("runs_per_cell")] int RunsPerCell = 1,
    [property: JsonPropertyName("primary_control_condition_id")] string? PrimaryControlConditionId = null,
    [property: JsonPropertyName("primary_treatment_condition_id")] string? PrimaryTreatmentConditionId = null);

public sealed record AgentEvalCondition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("roslyn_tools_enabled")] bool RoslynToolsEnabled,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record AgentEvalTask(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("acceptance_checks")] IReadOnlyList<string> AcceptanceChecks,
    [property: JsonPropertyName("repo_url")] string? RepoUrl = null,
    [property: JsonPropertyName("issue_url")] string? IssueUrl = null,
    [property: JsonPropertyName("task_prompt_file")] string? TaskPromptFile = null,
    [property: JsonPropertyName("setup_commands")] IReadOnlyList<string>? SetupCommands = null);

public sealed record AgentEvalRun(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("condition_id")] string ConditionId,
    [property: JsonPropertyName("replicate")] int? Replicate,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("compile_passed")] bool CompilePassed,
    [property: JsonPropertyName("tests_passed")] bool TestsPassed,
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    [property: JsonPropertyName("tools_offered")] IReadOnlyList<string> ToolsOffered,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<AgentToolCall> ToolCalls,
    [property: JsonPropertyName("context")] AgentEvalRunContext? Context,
    [property: JsonPropertyName("post_run_reflection")] AgentPostRunReflection? PostRunReflection,
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens = null,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens = null,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens = null);

public sealed record AgentEvalRunContext(
    [property: JsonPropertyName("task_title")] string TaskTitle,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("repo_url")] string? RepoUrl,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("acceptance_checks")] IReadOnlyList<string> AcceptanceChecks,
    [property: JsonPropertyName("task_prompt_file")] string? TaskPromptFile);

public sealed record AgentToolCall(
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("ok")] bool Ok);

public sealed record AgentPostRunReflection(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("helpful_tools")] IReadOnlyList<string> HelpfulTools,
    [property: JsonPropertyName("unhelpful_tools")] IReadOnlyList<string> UnhelpfulTools,
    [property: JsonPropertyName("roslyn_helpfulness_score")] int? RoslynHelpfulnessScore);

public sealed record AgentEvalConditionSummary(
    string condition_id,
    string condition_name,
    int run_count,
    double success_rate,
    double compile_rate,
    double tests_rate,
    double average_duration_seconds,
    int roslyn_used_runs,
    double roslyn_used_rate,
    double roslyn_call_share,
    double? average_roslyn_helpfulness_score,
    int runs_with_token_counts = 0,
    double? average_total_tokens = null,
    double? median_total_tokens = null);

public sealed record AgentEvalComparison(
    bool sufficient_data,
    string control_condition_id,
    string treatment_condition_id,
    int control_run_count,
    int treatment_run_count,
    double? success_rate_delta,
    double? compile_rate_delta,
    double? tests_rate_delta,
    double? roslyn_used_rate_in_treatment,
    double? average_total_tokens_control = null,
    double? average_total_tokens_treatment = null,
    double? average_total_tokens_delta = null,
    double? token_reduction_ratio = null,
    string? note = null);

public sealed record AgentEvalTaskComparison(
    string task_id,
    string task_title,
    bool sufficient_data,
    int control_run_count,
    int treatment_run_count,
    double? success_rate_delta,
    double? compile_rate_delta,
    double? tests_rate_delta,
    double? treatment_roslyn_used_rate,
    double? average_total_tokens_delta = null,
    string? note = null);

public sealed record AgentEvalReport(
    string experiment_id,
    DateTimeOffset generated_utc,
    int total_runs,
    IReadOnlyList<AgentEvalConditionSummary> condition_summaries,
    AgentEvalComparison? primary_comparison,
    IReadOnlyList<AgentEvalTaskComparison> task_comparisons,
    string output_path);

public sealed record AgentEvalCellSummary(
    string task_id,
    string condition_id,
    int observed_runs,
    int target_runs,
    int missing_runs);

public sealed record AgentEvalPendingRun(
    string task_id,
    string condition_id,
    int replicate,
    string suggested_run_id);

public sealed record AgentEvalWorklistReport(
    string experiment_id,
    DateTimeOffset generated_utc,
    int runs_per_cell,
    int expected_runs,
    int observed_runs,
    double completion_rate,
    IReadOnlyList<AgentEvalCellSummary> cells,
    IReadOnlyList<AgentEvalPendingRun> pending_runs,
    string output_path);

public sealed record AgentEvalManifestValidationIssue(
    string severity,
    string task_id,
    string message);

public sealed record AgentEvalManifestValidationReport(
    string experiment_id,
    DateTimeOffset generated_utc,
    bool valid,
    int issue_count,
    IReadOnlyList<AgentEvalManifestValidationIssue> issues,
    string output_path);

public sealed record AgentEvalRunValidationIssue(
    string severity,
    string run_id,
    string task_id,
    string condition_id,
    string message);

public sealed record AgentEvalRunValidationReport(
    string experiment_id,
    DateTimeOffset generated_utc,
    bool valid,
    int total_runs,
    int expected_runs,
    int issue_count,
    int error_count,
    int warning_count,
    int contaminated_control_runs,
    int treatment_runs_without_roslyn_offered,
    int treatment_runs_without_roslyn_usage,
    IReadOnlyList<AgentEvalRunValidationIssue> issues,
    string output_path,
    int runs_with_token_counts = 0,
    int runs_missing_token_counts = 0);

public sealed record AgentEvalGateReport(
    string experiment_id,
    DateTimeOffset generated_utc,
    bool manifest_valid,
    bool runs_valid,
    bool sufficient_data,
    bool gate_passed,
    int run_validation_error_count,
    int run_validation_warning_count,
    bool fail_on_run_warnings,
    string manifest_validation_path,
    string run_validation_path,
    string score_report_path,
    string summary_path,
    IReadOnlyList<string> notes,
    string output_path);

