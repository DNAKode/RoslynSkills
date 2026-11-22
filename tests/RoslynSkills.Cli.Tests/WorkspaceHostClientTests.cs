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

    [Fact]
    public void ResolveUnixSocketPath_UsesStateDirectoryWhenShort_AndFallsBackUnderLimitWhenLong()
    {
        const string hash = "0123456789abcdef";

        string shortPath = WorkspaceHostDaemonManager.ResolveUnixSocketPath("/home/user/.local/share/rs", hash, "/tmp");
        Assert.Equal(Path.Combine("/home/user/.local/share/rs", $"{hash}.sock"), shortPath);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(shortPath) <= 100);

        string longStateDir = "/home/runner/work/_temp/6b4234fb-a1b9-4d57-8d2c-a312250cdf5f/.local/share/RoslynSkills/workspace-hosts";
        string fallbackToTemp = WorkspaceHostDaemonManager.ResolveUnixSocketPath(longStateDir, hash, "/tmp");
        Assert.Equal(Path.Combine("/tmp", "roscli-sock", $"{hash}.sock"), fallbackToTemp);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fallbackToTemp) <= 100);

        string longTempDir = "/var/folders/very/long/temporary/directory/path/that/would/exceed/the/unix/domain/socket/limit";
        string fallbackToRootTmp = WorkspaceHostDaemonManager.ResolveUnixSocketPath(longStateDir, hash, longTempDir);
        Assert.Equal($"/tmp/roscli-{hash}.sock", fallbackToRootTmp);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fallbackToRootTmp) <= 100);
    }
}
