using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Tests;

public sealed class WorkspaceHostProtocolContractTests
{
    [Fact]
    public void RequestContract_UsesStableSnakeCaseProtocolFields()
    {
        JsonElement input = JsonSerializer.SerializeToElement(new
        {
            file_path = "src/App/Foo.cs",
            symbol_name = "Foo",
            require_workspace = true,
        });
        WorkspaceHostRequest request = new(
            Id: "req-001",
            Method: WorkspaceHostProtocol.Method.ToolCall,
            WorkspaceAlias: "default",
            WorkspaceHandle: "ws_abc",
            AuthToken: "token-123",
            RefreshPolicy: WorkspaceHostProtocol.RefreshPolicy.Auto,
            CommandId: "nav.find_symbol",
            Input: input);

        string json = JsonSerializer.Serialize(request);

        Assert.Contains("\"id\":\"req-001\"", json);
        Assert.Contains("\"method\":\"tool/call\"", json);
        Assert.Contains("\"workspace_alias\":\"default\"", json);
        Assert.Contains("\"workspace_handle\":\"ws_abc\"", json);
        Assert.Contains("\"auth_token\":\"token-123\"", json);
        Assert.Contains("\"refresh_policy\":\"auto\"", json);
        Assert.Contains("\"command_id\":\"nav.find_symbol\"", json);
        Assert.Contains("\"file_path\":\"src/App/Foo.cs\"", json);
    }

    [Fact]
    public void ResponseContract_IncludesFreshnessAndWorkspaceMetadata()
    {
        WorkspaceHostResponse response = new(
            Id: "req-001",
            Ok: true,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: WorkspaceHostProtocol.Method.ToolCall,
            ElapsedMs: 12.5,
            Workspace: new WorkspaceHostWorkspaceMetadata(
                Alias: "default",
                WorkspaceHandle: "ws_abc",
                WorkspaceKind: "slnx",
                SolutionScoped: true,
                ResolutionSource: "workspace_handle",
                RefreshPolicy: WorkspaceHostProtocol.RefreshPolicy.Auto,
                RefreshAction: WorkspaceHostProtocol.RefreshAction.IncrementalDocumentUpdate,
                DirtyBefore: true,
                DirtyAfter: false,
                RequiresReload: false,
                RequiresDesignTimeBuild: false,
                WorkspaceFingerprint: "abc123",
                ProjectCount: 3,
                DocumentCount: 42,
                InvalidatedPaths: new[] { "src/App/Foo.cs" },
                WorkspaceDiagnostics: Array.Empty<string>()),
            Envelope: new CommandEnvelope(
                Ok: true,
                CommandId: "nav.find_symbol",
                Version: "1.0",
                Data: new { total_matches = 1 },
                Errors: Array.Empty<CommandError>(),
                TraceId: null));

        string json = JsonSerializer.Serialize(response);

        Assert.Contains("\"protocol_version\":\"1.0\"", json);
        Assert.Contains("\"workspace_kind\":\"slnx\"", json);
        Assert.Contains("\"solution_scoped\":true", json);
        Assert.Contains("\"resolution_source\":\"workspace_handle\"", json);
        Assert.Contains("\"refresh_action\":\"incremental_document_update\"", json);
        Assert.Contains("\"dirty_before\":true", json);
        Assert.Contains("\"dirty_after\":false", json);
        Assert.Contains("\"requires_reload\":false", json);
        Assert.Contains("\"invalidated_paths\":[\"src/App/Foo.cs\"]", json);
        Assert.Contains("\"CommandId\":\"nav.find_symbol\"", json);
    }

    [Fact]
    public void ProtocolConstants_CoverPlannedFailureModes()
    {
        Assert.Equal("1.0", WorkspaceHostProtocol.ProtocolVersion);
        Assert.Equal("workspace_handle_not_found", WorkspaceHostProtocol.ErrorCode.WorkspaceHandleNotFound);
        Assert.Equal("workspace_reload_required", WorkspaceHostProtocol.ErrorCode.WorkspaceReloadRequired);
        Assert.Equal("solution_required", WorkspaceHostProtocol.ErrorCode.SolutionRequired);
        Assert.Equal("daemon_protocol_mismatch", WorkspaceHostProtocol.ErrorCode.ProtocolMismatch);
        Assert.Equal("daemon_auth_failed", WorkspaceHostProtocol.ErrorCode.DaemonAuthFailed);
    }
}
