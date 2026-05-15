using System.Text.Json;
using RoslynSkills.Cli;
using RoslynSkills.Contracts;

namespace RoslynSkills.Cli.Tests;

public sealed class WorkspaceHostClientTests
{
    [Fact]
    public async Task SendAsync_WritesRequestLineAndParsesResponse()
    {
        WorkspaceHostResponse response = new(
            Id: "req-1",
            Ok: true,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: WorkspaceHostProtocol.Method.Handshake,
            ElapsedMs: 1.25,
            Data: new WorkspaceHostHandshake(
                ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
                CliVersion: "1.0.0",
                HostVersion: "1.0.0",
                CommandSchemaVersion: "1.0",
                Capabilities: new[] { "named-pipe-jsonl" }));
        string responseLine = JsonSerializer.Serialize(response);
        using StringReader reader = new(responseLine);
        using StringWriter writer = new();
        await using WorkspaceHostClient client = WorkspaceHostClient.CreateForText(reader, writer);

        WorkspaceHostResponse parsed = await client.SendAsync(
            new WorkspaceHostRequest(
                Id: "req-1",
                Method: WorkspaceHostProtocol.Method.Handshake),
            CancellationToken.None);

        string requestLine = writer.ToString().Trim();
        using JsonDocument requestJson = JsonDocument.Parse(requestLine);
        Assert.Equal("req-1", requestJson.RootElement.GetProperty("id").GetString());
        Assert.Equal(WorkspaceHostProtocol.Method.Handshake, requestJson.RootElement.GetProperty("method").GetString());
        Assert.True(parsed.Ok);
        Assert.Equal("req-1", parsed.Id);
        Assert.Equal(WorkspaceHostProtocol.Method.Handshake, parsed.Method);
    }

    [Fact]
    public async Task SendAsync_FailsWhenHostClosesBeforeResponse()
    {
        using StringReader reader = new(string.Empty);
        using StringWriter writer = new();
        await using WorkspaceHostClient client = WorkspaceHostClient.CreateForText(reader, writer);

        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(
            new WorkspaceHostRequest(
                Id: "req-1",
                Method: WorkspaceHostProtocol.Method.Handshake),
            CancellationToken.None));
    }
}
