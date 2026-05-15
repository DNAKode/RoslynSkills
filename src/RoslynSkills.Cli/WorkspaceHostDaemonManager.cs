using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;

namespace RoslynSkills.Cli;

public sealed record WorkspaceHostDaemonEndpoint(
    string Transport,
    string? PipeName,
    string? SocketPath,
    string ManifestPath,
    string RepoRoot);

public sealed class WorkspaceHostDaemonManager
{
    private const string ManifestVersion = "1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public WorkspaceHostDaemonEndpoint GetDefaultEndpoint(string? repoRoot = null)
    {
        string resolvedRepoRoot = Path.GetFullPath(repoRoot ?? Directory.GetCurrentDirectory());
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resolvedRepoRoot)))
            .ToLowerInvariant()[..16];
        string stateDirectory = GetStateDirectory();
        Directory.CreateDirectory(stateDirectory);

        string transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "named-pipe"
            : "unix-socket";
        return transport == "named-pipe"
            ? new WorkspaceHostDaemonEndpoint(
                Transport: transport,
                PipeName: $"roslynskills-{hash}",
                SocketPath: null,
                ManifestPath: Path.Combine(stateDirectory, $"{hash}.json"),
                RepoRoot: resolvedRepoRoot)
            : new WorkspaceHostDaemonEndpoint(
                Transport: transport,
                PipeName: null,
                SocketPath: Path.Combine(stateDirectory, $"{hash}.sock"),
                ManifestPath: Path.Combine(stateDirectory, $"{hash}.json"),
                RepoRoot: resolvedRepoRoot);
    }

    public async Task<CommandEnvelope> StartAsync(
        string? repoRoot,
        string? hostPath,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        WorkspaceHostResponse? existing = await TryGetStatusAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (existing?.Ok == true)
        {
            return SuccessEnvelope(
                "daemon.start",
                new
                {
                    started = false,
                    already_running = true,
                    daemon_endpoint = FormatEndpoint(endpoint),
                    daemon_pid = TryReadManifest(endpoint.ManifestPath)?.ProcessId,
                    manifest_path = endpoint.ManifestPath,
                    status = existing.Data,
                });
        }

        string resolvedHostPath = ResolveHostPath(hostPath);
        bool useShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = endpoint.RepoRoot,
            UseShellExecute = useShellExecute,
            CreateNoWindow = !useShellExecute,
        };
        startInfo.ArgumentList.Add(resolvedHostPath);
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add(endpoint.Transport);
        if (endpoint.Transport == "named-pipe")
        {
            startInfo.ArgumentList.Add("--pipe-name");
            startInfo.ArgumentList.Add(endpoint.PipeName!);
        }
        else
        {
            startInfo.ArgumentList.Add("--socket-path");
            startInfo.ArgumentList.Add(endpoint.SocketPath!);
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start RoslynSkills workspace host process.");

        WorkspaceHostDaemonManifest manifest = new(
            ManifestVersion,
            endpoint.Transport,
            endpoint.PipeName,
            endpoint.SocketPath,
            endpoint.RepoRoot,
            resolvedHostPath,
            process.Id,
            DateTimeOffset.UtcNow);
        WriteManifest(endpoint.ManifestPath, manifest);

        WorkspaceHostResponse? status = await WaitForStatusAsync(endpoint, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (status?.Ok != true)
        {
            return ErrorEnvelope(
                "daemon.start",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"Started workspace host process {process.Id}, but it did not answer daemon/status on pipe '{endpoint.PipeName}'.");
        }

        return SuccessEnvelope(
            "daemon.start",
            new
            {
                started = true,
                already_running = false,
                daemon_endpoint = FormatEndpoint(endpoint),
                daemon_pid = process.Id,
                manifest_path = endpoint.ManifestPath,
                host_path = resolvedHostPath,
                status = status.Data,
            });
    }

    public async Task<CommandEnvelope> StatusAsync(
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        WorkspaceHostResponse? status = await TryGetStatusAsync(endpoint, cancellationToken).ConfigureAwait(false);
        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        if (status?.Ok != true)
        {
            return ErrorEnvelope(
                "daemon.status",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"No workspace host answered on endpoint '{FormatEndpoint(endpoint)}'.");
        }

        return SuccessEnvelope(
            "daemon.status",
            new
            {
                running = true,
                daemon_endpoint = FormatEndpoint(endpoint),
                daemon_pid = manifest?.ProcessId,
                manifest_path = endpoint.ManifestPath,
                status = status.Data,
            });
    }

    public async Task<CommandEnvelope> StopAsync(
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        WorkspaceHostResponse? response = await SendAsync(
            endpoint,
            new WorkspaceHostRequest(
                Id: Guid.NewGuid().ToString("N"),
                Method: WorkspaceHostProtocol.Method.Shutdown),
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);

        TryDeleteManifest(endpoint.ManifestPath);
        if (response?.Ok != true)
        {
            return ErrorEnvelope(
                "daemon.stop",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"No workspace host answered shutdown on endpoint '{FormatEndpoint(endpoint)}'.");
        }

        return SuccessEnvelope(
            "daemon.stop",
            new
            {
                stopped = true,
                daemon_endpoint = FormatEndpoint(endpoint),
                manifest_path = endpoint.ManifestPath,
                response = response.Data,
            });
    }

    public async Task<WorkspaceHostResponse?> SendRequestAsync(
        string? repoRoot,
        WorkspaceHostRequest request,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        return await SendAsync(endpoint, request, timeoutDuration, cancellationToken).ConfigureAwait(false);
    }

    public int? TryGetDaemonProcessId(string? repoRoot)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        return TryReadManifest(endpoint.ManifestPath)?.ProcessId;
    }

    public static string FormatEndpoint(WorkspaceHostDaemonEndpoint endpoint)
        => endpoint.Transport == "named-pipe"
            ? $"pipe:{endpoint.PipeName}"
            : $"unix:{endpoint.SocketPath}";

    private static async Task<WorkspaceHostResponse?> WaitForStatusAsync(
        WorkspaceHostDaemonEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            WorkspaceHostResponse? status = await TryGetStatusAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (status?.Ok == true)
            {
                return status;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static Task<WorkspaceHostResponse?> TryGetStatusAsync(
        WorkspaceHostDaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            endpoint,
            new WorkspaceHostRequest(
                Id: Guid.NewGuid().ToString("N"),
                Method: WorkspaceHostProtocol.Method.DaemonStatus),
            TimeSpan.FromSeconds(2),
            cancellationToken);
    }

    private static async Task<WorkspaceHostResponse?> SendAsync(
        WorkspaceHostDaemonEndpoint endpoint,
        WorkspaceHostRequest request,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutDuration);
            WorkspaceHostClientOptions options = endpoint.Transport == "named-pipe"
                ? WorkspaceHostClientOptions.NamedPipe(endpoint.PipeName!)
                : WorkspaceHostClientOptions.UnixSocket(endpoint.SocketPath!);
            await using WorkspaceHostClient client = await WorkspaceHostClient.ConnectAsync(
                options,
                timeout.Token).ConfigureAwait(false);
            return await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveHostPath(string? explicitHostPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitHostPath))
        {
            string fullPath = Path.GetFullPath(explicitHostPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Workspace host assembly '{fullPath}' does not exist.", fullPath);
            }

            return fullPath;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string sibling = Path.Combine(baseDirectory, "RoslynSkills.WorkspaceHost.dll");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        string? repoCandidate = FindRepoRoot(baseDirectory);
        if (repoCandidate is not null)
        {
            string configuration = IsDebugAssembly() ? "Debug" : "Release";
            string host = Path.Combine(
                repoCandidate,
                "src",
                "RoslynSkills.WorkspaceHost",
                "bin",
                configuration,
                "net10.0",
                "RoslynSkills.WorkspaceHost.dll");
            if (File.Exists(host))
            {
                return host;
            }
        }

        throw new FileNotFoundException("Could not locate RoslynSkills.WorkspaceHost.dll. Pass --host-path to daemon.start.");
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RoslynSkills.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsDebugAssembly()
    {
        object[] attributes = typeof(CliApplication).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), inherit: false);
        return attributes.OfType<DebuggableAttribute>().Any(a => a.IsJITTrackingEnabled);
    }

    private static string GetStateDirectory()
    {
        string? root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "RoslynSkills", "workspace-hosts");
    }

    private static void WriteManifest(string path, WorkspaceHostDaemonManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static WorkspaceHostDaemonManifest? TryReadManifest(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WorkspaceHostDaemonManifest>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDeleteManifest(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static CommandEnvelope SuccessEnvelope(string commandId, object data)
        => new(
            Ok: true,
            CommandId: commandId,
            Version: "1.0",
            Data: data,
            Errors: Array.Empty<CommandError>(),
            TraceId: null);

    private static CommandEnvelope ErrorEnvelope(string commandId, string code, string message)
        => new(
            Ok: false,
            CommandId: commandId,
            Version: "1.0",
            Data: null,
            Errors: new[] { new CommandError(code, message) },
            TraceId: null);

    private sealed record WorkspaceHostDaemonManifest(
        string ManifestVersion,
        string Transport,
        string? PipeName,
        string? SocketPath,
        string RepoRoot,
        string HostPath,
        int ProcessId,
        DateTimeOffset StartedAtUtc);
}
