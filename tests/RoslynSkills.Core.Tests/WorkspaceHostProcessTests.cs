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

        try
        {
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
        finally
        {
            TryKillProcessTree(process);
        }
    }

    [Fact]
    public async Task WorkspaceHost_RejectsNamedPipeWithoutPipeName()
    {
        string hostAssemblyPath = typeof(WorkspaceHostAssembly).Assembly.Location;
        using Process process = StartHostProcess(hostAssemblyPath, "--transport", "named-pipe");

        try
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after invalid named-pipe options.");
            Assert.Equal(2, process.ExitCode);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
            string stderr = await stderrTask;
            Assert.Contains("--transport named-pipe requires --pipe-name", stderr);
        }
        finally
        {
            TryKillProcessTree(process);
        }
    }

    [Fact]
    public async Task WorkspaceHost_RequiresAuthTokenWhenConfigured()
    {
        string hostAssemblyPath = typeof(WorkspaceHostAssembly).Assembly.Location;
        using Process process = StartHostProcess(hostAssemblyPath, "--auth-token", "test-token");

        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":\"missing\",\"method\":\"daemon/status\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"ok\",\"method\":\"daemon/status\",\"auth_token\":\"test-token\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"shutdown\",\"method\":\"shutdown\",\"auth_token\":\"test-token\"}");

            string? missingLine = await ReadLineWithTimeoutAsync(process, "missing auth");
            string? okLine = await ReadLineWithTimeoutAsync(process, "authorized status");
            _ = await ReadLineWithTimeoutAsync(process, "shutdown");

            Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after authenticated shutdown.");

            using JsonDocument missing = JsonDocument.Parse(missingLine!);
            JsonElement missingRoot = missing.RootElement;
            Assert.False(missingRoot.GetProperty("ok").GetBoolean());
            Assert.Equal(WorkspaceHostProtocol.ErrorCode.DaemonAuthFailed, missingRoot.GetProperty("errors")[0].GetProperty("Code").GetString());

            using JsonDocument ok = JsonDocument.Parse(okLine!);
            Assert.True(ok.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(WorkspaceHostProtocol.Method.DaemonStatus, ok.RootElement.GetProperty("method").GetString());
        }
        finally
        {
            TryKillProcessTree(process);
        }
    }

    [Fact]
    public async Task WorkspaceHost_StructuredEditRefreshesHotWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-host-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string hostAssemblyPath = typeof(WorkspaceHostAssembly).Assembly.Location;
        using Process process = StartHostProcess(hostAssemblyPath);

        try
        {
            (string solutionPath, string filePath) = await CreateSingleProjectWorkspaceAsync(root);

            await WriteJsonLineAsync(
                process,
                new
                {
                    id = "preload",
                    method = WorkspaceHostProtocol.Method.WorkspacePreload,
                    workspace_alias = "default",
                    input = new
                    {
                        workspace_path = solutionPath,
                        require_solution = true,
                        max_files = 100,
                    },
                });
            using JsonDocument preload = JsonDocument.Parse((await ReadLineWithTimeoutAsync(process, "preload", timeoutSeconds: 60))!);
            JsonElement preloadRoot = preload.RootElement;
            Assert.True(preloadRoot.GetProperty("ok").GetBoolean());
            string handle = preloadRoot
                .GetProperty("envelope")
                .GetProperty("Data")
                .GetProperty("workspace_handle")
                .GetString()!;

            await WriteJsonLineAsync(
                process,
                new
                {
                    id = "rename",
                    method = WorkspaceHostProtocol.Method.ToolCall,
                    command_id = "edit.rename_symbol",
                    input = new
                    {
                        workspace_handle = handle,
                        file_path = filePath,
                        line = 5,
                        column = 26,
                        new_name = "Title",
                        apply = true,
                        require_workspace = true,
                    },
                });
            using JsonDocument rename = JsonDocument.Parse((await ReadLineWithTimeoutAsync(process, "rename"))!);
            Assert.True(rename.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("Title", await File.ReadAllTextAsync(filePath));

            await WriteJsonLineAsync(
                process,
                new
                {
                    id = "find",
                    method = WorkspaceHostProtocol.Method.ToolCall,
                    command_id = "nav.find_symbol",
                    input = new
                    {
                        workspace_handle = handle,
                        file_path = filePath,
                        symbol_name = "Title",
                        require_workspace = true,
                        brief = true,
                    },
                });
            using JsonDocument find = JsonDocument.Parse((await ReadLineWithTimeoutAsync(process, "find"))!);
            JsonElement findRoot = find.RootElement;
            Assert.True(findRoot.GetProperty("ok").GetBoolean());
            Assert.True(findRoot
                .GetProperty("envelope")
                .GetProperty("Data")
                .GetProperty("total_matches")
                .GetInt32() >= 1);

            await process.StandardInput.WriteLineAsync("{\"id\":\"shutdown\",\"method\":\"shutdown\"}");
            _ = await ReadLineWithTimeoutAsync(process, "shutdown");
            Assert.True(process.WaitForExit(10_000), "Workspace host did not exit after shutdown.");
        }
        finally
        {
            TryKillProcessTree(process);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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

    private static async Task<string?> ReadLineWithTimeoutAsync(
        Process process,
        string responseName,
        int timeoutSeconds = 10)
        => await ReadLineWithTimeoutAsync(process.StandardOutput, process, responseName, timeoutSeconds);

    private static async Task<string?> ReadLineWithTimeoutAsync(
        TextReader reader,
        Process process,
        string responseName,
        int timeoutSeconds)
    {
        try
        {
            return await reader
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch (TimeoutException)
        {
            TryKillProcessTree(process);
            string stderr = await TryReadToEndAsync(process.StandardError, TimeSpan.FromSeconds(5));
            throw new TimeoutException($"Timed out waiting for workspace host {responseName} response. Stderr: {stderr}");
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task<string> TryReadToEndAsync(StreamReader reader, TimeSpan timeout)
    {
        try
        {
            return await reader.ReadToEndAsync().WaitAsync(timeout);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task WriteJsonLineAsync(Process process, object request)
    {
        string json = JsonSerializer.Serialize(request);
        await process.StandardInput.WriteLineAsync(json);
    }

    private static async Task<(string solutionPath, string filePath)> CreateSingleProjectWorkspaceAsync(string root)
    {
        string projectDirectory = Path.Combine(root, "Lib");
        Directory.CreateDirectory(projectDirectory);
        string projectPath = Path.Combine(projectDirectory, "Lib.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        string filePath = Path.Combine(projectDirectory, "Helper.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            namespace Lib;

            public static class Helper
            {
                public static string Name => "Lib";
            }
            """);

        string solutionPath = Path.Combine(root, "Host.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """);
        return (solutionPath, filePath);
    }
}
