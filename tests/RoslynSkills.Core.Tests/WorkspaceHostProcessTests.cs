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

        using JsonDocument handshake = JsonDocument.Parse(handshakeLine!);
        JsonElement handshakeRoot = handshake.RootElement;
        Assert.True(handshakeRoot.GetProperty("ok").GetBoolean());
        Assert.Equal(WorkspaceHostProtocol.ProtocolVersion, handshakeRoot.GetProperty("protocol_version").GetString());
        Assert.Equal(WorkspaceHostProtocol.Method.Handshake, handshakeRoot.GetProperty("method").GetString());
        Assert.Equal("stdio-jsonl", handshakeRoot.GetProperty("data").GetProperty("capabilities")[0].GetString());

        using JsonDocument shutdown = JsonDocument.Parse(shutdownLine!);
        JsonElement shutdownRoot = shutdown.RootElement;
        Assert.True(shutdownRoot.GetProperty("ok").GetBoolean());
        Assert.Equal(WorkspaceHostProtocol.Method.Shutdown, shutdownRoot.GetProperty("method").GetString());
    }

    [Fact]
    public async Task WorkspaceHost_RejectsNamedPipeWithoutPipeName()
    {
        string hostAssemblyPath = typeof(WorkspaceHostAssembly).Assembly.Location;
        using Process process = StartHostProcess(hostAssemblyPath, "--transport", "named-pipe");

        Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after invalid named-pipe options.");
        Assert.Equal(2, process.ExitCode);
        string stderr = await process.StandardError.ReadToEndAsync();
        Assert.Contains("--transport named-pipe requires --pipe-name", stderr);
    }

    private static Process StartHostProcess(string hostAssemblyPath, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(hostAssemblyPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start workspace host process.");
        return process;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(Process process, string responseName)
        => await ReadLineWithTimeoutAsync(process.StandardOutput, process, responseName);

    private static async Task<string?> ReadLineWithTimeoutAsync(
        TextReader reader,
        Process process,
        string responseName)
    {
        try
        {
            return await reader
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
