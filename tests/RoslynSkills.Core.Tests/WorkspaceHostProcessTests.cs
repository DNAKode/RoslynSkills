using System.Diagnostics;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.WorkspaceHost;

namespace RoslynSkills.Core.Tests;

public sealed class WorkspaceHostProcessTests
{
    [Fact]
    public async Task WorkspaceHost_RespondsToHandshakeAndShutdown()
    {
        string hostAssemblyPath = typeof(WorkspaceHostAssembly).Assembly.Location;
        using Process process = StartHostProcess(hostAssemblyPath);

        await process.StandardInput.WriteLineAsync("{\"id\":\"h1\",\"method\":\"host/handshake\"}");
        await process.StandardInput.WriteLineAsync("{\"id\":\"x1\",\"method\":\"shutdown\"}");

        string? handshakeLine = await ReadLineWithTimeoutAsync(process, "handshake");
        string? shutdownLine = await ReadLineWithTimeoutAsync(process, "shutdown");

        Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after shutdown.");
        Assert.Equal(0, process.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(handshakeLine));
        Assert.False(string.IsNullOrWhiteSpace(shutdownLine));

        using JsonDocument handshake = JsonDocument.Parse(handshakeLine);
        JsonElement handshakeRoot = handshake.RootElement;
        Assert.True(handshakeRoot.GetProperty("ok").GetBoolean());
        Assert.Equal(WorkspaceHostProtocol.ProtocolVersion, handshakeRoot.GetProperty("protocol_version").GetString());
        Assert.Equal(WorkspaceHostProtocol.Method.Handshake, handshakeRoot.GetProperty("method").GetString());
        Assert.Equal("stdio-jsonl", handshakeRoot.GetProperty("data").GetProperty("capabilities")[0].GetString());

        using JsonDocument shutdown = JsonDocument.Parse(shutdownLine);
        JsonElement shutdownRoot = shutdown.RootElement;
        Assert.True(shutdownRoot.GetProperty("ok").GetBoolean());
        Assert.Equal(WorkspaceHostProtocol.Method.Shutdown, shutdownRoot.GetProperty("method").GetString());
    }

    private static Process StartHostProcess(string hostAssemblyPath)
    {
        ProcessStartInfo startInfo = new("dotnet", $"\"{hostAssemblyPath}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start workspace host process.");
        return process;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(Process process, string responseName)
    {
        try
        {
            return await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            string stderr = await process.StandardError.ReadToEndAsync();
            throw new TimeoutException($"Timed out waiting for workspace host {responseName} response. Stderr: {stderr}");
        }
    }
}
