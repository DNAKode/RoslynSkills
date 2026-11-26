using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSkills.Contracts;

public sealed record CommandParameterDescriptor(
    string Name,
    string Type,
    string Description,
    bool Required = false,
    string? ItemType = null,
    IReadOnlyList<string>? EnumValues = null,
    int? Minimum = null,
    int? Maximum = null,
    bool IsPath = false,
    bool AcceptCommaList = false,
    IReadOnlyList<CommandParameterDescriptor>? ItemProperties = null,
    IReadOnlyList<string>? Aliases = null);

public sealed record CommandInputSchema(
    IReadOnlyList<CommandParameterDescriptor> Parameters,
    bool AdditionalProperties = true);

public sealed record CommandDescriptor(
    string Id,
    string Summary,
    string InputSchemaVersion,
    string OutputSchemaVersion,
    bool MutatesState,
    string Maturity = CommandMaturity.Stable,
    IReadOnlyList<string>? Traits = null,
    bool RunsOnHotWorkspace = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HotWorkspaceOptional = false,
    [property: JsonIgnore] CommandInputSchema? InputSchema = null);

public static class CommandMaturity
{
    public const string Stable = "stable";
    public const string Advanced = "advanced";
    public const string Experimental = "experimental";
}

public static class CommandTrait
{
    public const string Heuristic = "heuristic";
    public const string PotentiallySlow = "potentially_slow";
    public const string DerivedAnalysis = "derived_analysis";
    public const string BatchOrchestration = "batch_orchestration";
}

public sealed record CommandError(
    string Code,
    string Message,
    object? Details = null);

public sealed record CommandTransportTimings(
    [property: JsonPropertyName("validate_ms")] double? ValidateMs = null,
    [property: JsonPropertyName("execute_ms")] double? ExecuteMs = null,
    [property: JsonPropertyName("host_elapsed_ms")] double? HostElapsedMs = null,
    [property: JsonPropertyName("roundtrip_ms")] double? RoundtripMs = null,
    [property: JsonPropertyName("total_ms")] double? TotalMs = null,
    [property: JsonPropertyName("host_freshness_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? HostFreshnessMs = null,
    [property: JsonPropertyName("host_execute_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? HostExecuteMs = null,
    [property: JsonPropertyName("host_freshness_mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HostFreshnessMode = null);

public sealed record CommandTransportMetadata(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("routed_alias")] string? RoutedAlias = null,
    [property: JsonPropertyName("workspace_handle")] string? WorkspaceHandle = null,
    [property: JsonPropertyName("cache_mode")] string? CacheMode = null,
    [property: JsonPropertyName("refresh_policy")] string? RefreshPolicy = null,
    [property: JsonPropertyName("refresh_action")] string? RefreshAction = null,
    [property: JsonPropertyName("daemon_endpoint")] string? DaemonEndpoint = null,
    [property: JsonPropertyName("timings")] CommandTransportTimings? Timings = null,
    [property: JsonPropertyName("workspace")] WorkspaceHostWorkspaceMetadata? Workspace = null,
    [property: JsonPropertyName("daemon_lifetime"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DaemonLifetime = null,
    [property: JsonPropertyName("daemon_lifetime_note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DaemonLifetimeNote = null,
    [property: JsonPropertyName("launch_mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LaunchMode = null,
    [property: JsonPropertyName("caller_job_flags"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CallerJobFlags = null,
    [property: JsonPropertyName("launch_warning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LaunchWarning = null);

public sealed record CommandEnvelope(
    [property: JsonPropertyOrder(-100)] bool Ok,
    [property: JsonPropertyOrder(-90)] string CommandId,
    [property: JsonPropertyOrder(-80)] string Version,
    [property: JsonPropertyOrder(-60)] object? Data,
    [property: JsonPropertyOrder(0)] IReadOnlyList<CommandError> Errors,
    [property: JsonPropertyOrder(90)] string? TraceId,
    [property: JsonPropertyOrder(-50)] object? Telemetry = null,
    [property: JsonPropertyOrder(-70)] string? Preview = null,
    [property: JsonPropertyOrder(100)] string? Summary = null,
    [property: JsonPropertyOrder(-45)] CommandTransportMetadata? Transport = null);

public sealed record CommandExecutionResult(
    object? Data,
    IReadOnlyList<CommandError> Errors,
    object? Telemetry = null)
{
    public bool Ok => Errors.Count == 0;
}

public interface IAgentCommand
{
    CommandDescriptor Descriptor { get; }

    IReadOnlyList<CommandError> Validate(JsonElement input);

    Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken);
}

