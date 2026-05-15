using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSkills.Contracts;

public static class WorkspaceHostProtocol
{
    public const string ProtocolVersion = "1.0";

    public static class Method
    {
        public const string Handshake = "host/handshake";
        public const string ToolList = "tool/list";
        public const string ToolCall = "tool/call";
        public const string WorkspacePreload = "workspace/preload";
        public const string WorkspaceStatus = "workspace/status";
        public const string WorkspaceRefresh = "workspace/refresh";
        public const string WorkspaceClose = "workspace/close";
        public const string WorkspaceList = "workspace/list";
        public const string DaemonStatus = "daemon/status";
        public const string Shutdown = "shutdown";
    }

    public static class RefreshPolicy
    {
        public const string None = "none";
        public const string Auto = "auto";
        public const string Strict = "strict";
    }

    public static class RefreshAction
    {
        public const string None = "none";
        public const string IncrementalDocumentUpdate = "incremental_document_update";
        public const string Reload = "reload";
        public const string Failed = "failed";
    }

    public static class ErrorCode
    {
        public const string DaemonUnavailable = "daemon_unavailable";
        public const string ProtocolMismatch = "daemon_protocol_mismatch";
        public const string WorkspaceHandleNotFound = "workspace_handle_not_found";
        public const string WorkspaceStale = "workspace_stale";
        public const string WorkspaceReloadRequired = "workspace_reload_required";
        public const string SolutionRequired = "solution_required";
    }
}

public sealed record WorkspaceHostHandshake(
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("cli_version")] string? CliVersion,
    [property: JsonPropertyName("host_version")] string? HostVersion,
    [property: JsonPropertyName("command_schema_version")] string? CommandSchemaVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

public sealed record WorkspaceHostRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("workspace_alias")] string? WorkspaceAlias = null,
    [property: JsonPropertyName("workspace_handle")] string? WorkspaceHandle = null,
    [property: JsonPropertyName("refresh_policy")] string? RefreshPolicy = null,
    [property: JsonPropertyName("command_id")] string? CommandId = null,
    [property: JsonPropertyName("input")] JsonElement? Input = null);

public sealed record WorkspaceHostResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("workspace")] WorkspaceHostWorkspaceMetadata? Workspace = null,
    [property: JsonPropertyName("envelope")] CommandEnvelope? Envelope = null,
    [property: JsonPropertyName("data")] object? Data = null,
    [property: JsonPropertyName("errors")] IReadOnlyList<CommandError>? Errors = null);

public sealed record WorkspaceHostWorkspaceMetadata(
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("workspace_handle")] string? WorkspaceHandle,
    [property: JsonPropertyName("workspace_kind")] string? WorkspaceKind,
    [property: JsonPropertyName("solution_scoped")] bool SolutionScoped,
    [property: JsonPropertyName("resolution_source")] string? ResolutionSource,
    [property: JsonPropertyName("refresh_policy")] string RefreshPolicy,
    [property: JsonPropertyName("refresh_action")] string RefreshAction,
    [property: JsonPropertyName("dirty_before")] bool DirtyBefore,
    [property: JsonPropertyName("dirty_after")] bool DirtyAfter,
    [property: JsonPropertyName("requires_reload")] bool RequiresReload,
    [property: JsonPropertyName("requires_design_time_build")] bool RequiresDesignTimeBuild,
    [property: JsonPropertyName("workspace_fingerprint")] string? WorkspaceFingerprint,
    [property: JsonPropertyName("project_count")] int ProjectCount,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("invalidated_paths")] IReadOnlyList<string> InvalidatedPaths,
    [property: JsonPropertyName("workspace_diagnostics")] IReadOnlyList<string> WorkspaceDiagnostics);
