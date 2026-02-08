using System.Text.Json.Serialization;

namespace RoslynAgent.Benchmark.AgentEval;

public sealed record AgentEvalManifest(
    [property: JsonPropertyName("experiment_id")] string ExperimentId,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("roslyn_tool_prefixes")] IReadOnlyList<string> RoslynToolPrefixes,
    [property: JsonPropertyName("conditions")] IReadOnlyList<AgentEvalCondition> Conditions,
    [property: JsonPropertyName("tasks")] IReadOnlyList<AgentEvalTask> Tasks,
    [property: JsonPropertyName("runs_per_cell")] int RunsPerCell = 1);

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
    [property: JsonPropertyName("acceptance_checks")] IReadOnlyList<string> AcceptanceChecks);

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
    [property: JsonPropertyName("post_run_reflection")] AgentPostRunReflection? PostRunReflection);

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
    double? average_roslyn_helpfulness_score);

public sealed record AgentEvalComparison(
    string control_condition_id,
    string treatment_condition_id,
    double success_rate_delta,
    double compile_rate_delta,
    double tests_rate_delta,
    double roslyn_used_rate_in_treatment);

public sealed record AgentEvalReport(
    string experiment_id,
    DateTimeOffset generated_utc,
    int total_runs,
    IReadOnlyList<AgentEvalConditionSummary> condition_summaries,
    AgentEvalComparison? primary_comparison,
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
