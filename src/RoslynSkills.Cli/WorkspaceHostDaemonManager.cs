using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;

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
    private const int MaxUnixSocketPathUtf8Bytes = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public WorkspaceHostDaemonEndpoint GetDefaultEndpoint(string? repoRoot = null)
    {
        string resolvedRepoRoot = RepositoryRootDiscovery.ResolveWorkspaceDaemonRoot(repoRoot);
        string hash = RepositoryRootDiscovery.ComputeWorkspaceStateHash(resolvedRepoRoot);
        string stateDirectory = RepositoryRootDiscovery.GetWorkspaceHostStateDirectory();
        Directory.CreateDirectory(stateDirectory);
        string manifestPath = RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(resolvedRepoRoot);

        string transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "named-pipe"
            : "unix-socket";
        if (transport == "named-pipe")
        {
            return new WorkspaceHostDaemonEndpoint(
                Transport: transport,
                PipeName: $"roslynskills-{hash}",
                SocketPath: null,
                ManifestPath: manifestPath,
                RepoRoot: resolvedRepoRoot);
        }

        string socketPath = ResolveUnixSocketPath(stateDirectory, hash);
        string? socketDirectory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrWhiteSpace(socketDirectory))
        {
            Directory.CreateDirectory(socketDirectory);
        }

        return new WorkspaceHostDaemonEndpoint(
            Transport: transport,
            PipeName: null,
            SocketPath: socketPath,
            ManifestPath: manifestPath,
            RepoRoot: resolvedRepoRoot);
    }

    public static string ResolveUnixSocketPath(string stateDirectory, string hash, string? tempDirectory = null)
    {
        string candidate = Path.Combine(stateDirectory, $"{hash}.sock");
        if (Encoding.UTF8.GetByteCount(candidate) <= MaxUnixSocketPathUtf8Bytes)
        {
            return candidate;
        }

        string fallbackTempRoot = string.IsNullOrWhiteSpace(tempDirectory) ? Path.GetTempPath() : tempDirectory;
        string tempCandidate = Path.Combine(fallbackTempRoot, "roscli-sock", $"{hash}.sock");
        if (Encoding.UTF8.GetByteCount(tempCandidate) <= MaxUnixSocketPathUtf8Bytes)
        {
            return tempCandidate;
        }

        return $"/tmp/roscli-{hash}.sock";
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

        WorkspaceHostDaemonManifest? staleManifest = TryReadManifest(endpoint.ManifestPath);
        if (staleManifest is not null)
        {
            ManagedProcessStopResult staleStop = await StopManagedProcessAsync(
                staleManifest,
                graceTimeout: TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
            if (staleStop.StillAlive)
            {
                return ErrorEnvelope(
                    "daemon.start",
                    WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                    $"Unresponsive workspace host process {staleManifest.ProcessId} on endpoint '{FormatEndpoint(endpoint)}' could not be terminated.");
            }

            TryDeleteManifest(endpoint.ManifestPath);
        }

        if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
        {
            // No live daemon answered on this socket path; remove any stale socket left by a killed/crashed daemon.
            TryDeleteFileOrSocket(endpoint.SocketPath);
        }

        string resolvedHostPath = ResolveHostPath(hostPath);
        string authToken = GenerateAuthToken();
        ProcessStartInfo startInfo = BuildDaemonStartInfo(endpoint, resolvedHostPath, authToken);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start RoslynSkills workspace host process.");

        WorkspaceHostDaemonManifest manifest = new(
            ManifestVersion,
            endpoint.Transport,
            endpoint.PipeName,
            endpoint.SocketPath,
            endpoint.RepoRoot,
            resolvedHostPath,
            authToken,
            process.Id,
            DateTimeOffset.UtcNow);
        WriteManifest(endpoint.ManifestPath, manifest);

        WorkspaceHostResponse? status = await WaitForStatusAsync(endpoint, process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (status?.Ok != true)
        {
            TryKillProcessTree(process);
            await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            TryDeleteManifest(endpoint.ManifestPath);
            if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
            {
                TryDeleteFileOrSocket(endpoint.SocketPath);
            }

            return ErrorEnvelope(
                "daemon.start",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"Started workspace host process {process.Id}, but it did not answer daemon/status on endpoint '{FormatEndpoint(endpoint)}'.");
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

    private static ProcessStartInfo BuildDaemonStartInfo(
        WorkspaceHostDaemonEndpoint endpoint,
        string resolvedHostPath,
        string authToken)
    {
        string dotnetExecutable = ResolveDotnetExecutable();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ProcessStartInfo windowsStartInfo = new(dotnetExecutable)
            {
                WorkingDirectory = endpoint.RepoRoot,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            AppendDaemonArguments(windowsStartInfo, resolvedHostPath, endpoint, authToken);
            return windowsStartInfo;
        }

        // On Unix, starting dotnet directly with UseShellExecute=false causes the background daemon (and any
        // MSBuild/restore children it spawns) to inherit roscli's stdout/stderr pipe descriptors, hanging any
        // caller reading roscli output until EOF. Launching through `/bin/sh` with `exec` detaches stdin/stdout/stderr
        // to /dev/null and ignores SIGHUP while keeping the exact daemon PID.
        ProcessStartInfo unixStartInfo = new("/bin/sh")
        {
            WorkingDirectory = endpoint.RepoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add("trap '' HUP; exec \"$@\" </dev/null >/dev/null 2>&1");
        unixStartInfo.ArgumentList.Add("roscli-daemon");
        unixStartInfo.ArgumentList.Add(dotnetExecutable);
        AppendDaemonArguments(unixStartInfo, resolvedHostPath, endpoint, authToken);
        return unixStartInfo;
    }

    private static void AppendDaemonArguments(
        ProcessStartInfo startInfo,
        string resolvedHostPath,
        WorkspaceHostDaemonEndpoint endpoint,
        string authToken)
    {
        startInfo.ArgumentList.Add(resolvedHostPath);
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add(endpoint.Transport);
        startInfo.ArgumentList.Add("--auth-token");
        startInfo.ArgumentList.Add(authToken);
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
    }

    private static string ResolveDotnetExecutable()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
        {
            return processPath;
        }

        return "dotnet";
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

    private static void TryDeleteFileOrSocket(string path)
    {
        try
        {
            if (Path.Exists(path))
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
        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        WorkspaceHostResponse? response = await SendAsync(
            endpoint,
            new WorkspaceHostRequest(
                Id: Guid.NewGuid().ToString("N"),
                Method: WorkspaceHostProtocol.Method.Shutdown),
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);

        bool gracefulAnswered = response?.Ok == true;
        ManagedProcessStopResult processStop = await StopManagedProcessAsync(
            manifest,
            graceTimeout: gracefulAnswered ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(1),
            cancellationToken).ConfigureAwait(false);

        if (processStop.StillAlive)
        {
            return ErrorEnvelope(
                "daemon.stop",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"Workspace host process {manifest!.ProcessId} on endpoint '{FormatEndpoint(endpoint)}' did not exit after shutdown and forced termination.");
        }

        TryDeleteManifest(endpoint.ManifestPath);
        if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
        {
            TryDeleteFileOrSocket(endpoint.SocketPath);
        }

        if (!gracefulAnswered && !processStop.StoppedProcess)
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
                forced_kill = processStop.ForcedKill,
                daemon_pid = manifest?.ProcessId,
                daemon_endpoint = FormatEndpoint(endpoint),
                manifest_path = endpoint.ManifestPath,
                response = response?.Data,
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
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (IsProcessExited(process))
            {
                return null;
            }

            WorkspaceHostResponse? status = await TryGetStatusAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (status?.Ok == true)
            {
                return status;
            }

            if (IsProcessExited(process))
            {
                return null;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<ManagedProcessStopResult> StopManagedProcessAsync(
        WorkspaceHostDaemonManifest? manifest,
        TimeSpan graceTimeout,
        CancellationToken cancellationToken)
    {
        if (manifest is null)
        {
            return new ManagedProcessStopResult(StoppedProcess: false, ForcedKill: false, StillAlive: false);
        }

        using Process? process = TryGetManagedDaemonProcess(manifest);
        if (process is null)
        {
            return new ManagedProcessStopResult(StoppedProcess: false, ForcedKill: false, StillAlive: false);
        }

        if (await WaitForProcessExitAsync(process, graceTimeout, cancellationToken).ConfigureAwait(false))
        {
            return new ManagedProcessStopResult(StoppedProcess: true, ForcedKill: false, StillAlive: false);
        }

        TryKillProcessTree(process);
        bool exitedAfterKill = await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return new ManagedProcessStopResult(
            StoppedProcess: exitedAfterKill,
            ForcedKill: true,
            StillAlive: !exitedAfterKill);
    }

    private static Process? TryGetManagedDaemonProcess(WorkspaceHostDaemonManifest manifest)
    {
        if (manifest.ProcessId <= 0 || manifest.ProcessId == Environment.ProcessId)
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(manifest.ProcessId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            if (!IsExpectedDaemonProcessName(process.ProcessName))
            {
                process.Dispose();
                return null;
            }

            DateTime startTimeUtc = process.StartTime.ToUniversalTime();
            if (Math.Abs((startTimeUtc - manifest.StartedAtUtc.UtcDateTime).TotalSeconds) > 30)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (InvalidOperationException)
        {
            process.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            return null;
        }
    }

    private static bool IsExpectedDaemonProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        if (string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "RoslynSkills.WorkspaceHost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
               (string.Equals(processName, "sh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "dash", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "bash", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProcessExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IsProcessExited(process))
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IsProcessExited(process);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return IsProcessExited(process);
        }
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
            WorkspaceHostRequest authorizedRequest = AddAuthToken(endpoint, request);
            await using WorkspaceHostClient client = await WorkspaceHostClient.ConnectAsync(
                options,
                timeout.Token).ConfigureAwait(false);
            WorkspaceHostResponse response = await client.SendAsync(authorizedRequest, timeout.Token).ConfigureAwait(false);
            return IsProtocolCompatible(response.ProtocolVersion)
                ? response
                : ProtocolMismatchResponse(request, response.ProtocolVersion);
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

    private static void WriteManifest(string path, WorkspaceHostDaemonManifest manifest)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static WorkspaceHostRequest AddAuthToken(WorkspaceHostDaemonEndpoint endpoint, WorkspaceHostRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AuthToken))
        {
            return request;
        }

        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        return string.IsNullOrWhiteSpace(manifest?.AuthToken)
            ? request
            : request with { AuthToken = manifest.AuthToken };
    }

    private static string GenerateAuthToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsProtocolCompatible(string? protocolVersion)
    {
        if (string.IsNullOrWhiteSpace(protocolVersion))
        {
            return false;
        }

        string expectedMajor = WorkspaceHostProtocol.ProtocolVersion.Split('.')[0];
        string actualMajor = protocolVersion.Split('.')[0];
        return string.Equals(expectedMajor, actualMajor, StringComparison.Ordinal);
    }

    private static WorkspaceHostResponse ProtocolMismatchResponse(WorkspaceHostRequest request, string? actualProtocolVersion)
    {
        CommandError error = new(
            WorkspaceHostProtocol.ErrorCode.ProtocolMismatch,
            $"Workspace host protocol '{actualProtocolVersion ?? "unknown"}' is not compatible with client protocol '{WorkspaceHostProtocol.ProtocolVersion}'. Restart the daemon.");
        return new WorkspaceHostResponse(
            Id: request.Id,
            Ok: false,
            ProtocolVersion: actualProtocolVersion ?? "unknown",
            Method: request.Method,
            ElapsedMs: 0,
            CommandId: request.CommandId ?? request.Method,
            Version: "1.0",
            Data: null,
            Errors: new[] { error },
            Summary: error.Message);
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
        string? AuthToken,
        int ProcessId,
        DateTimeOffset StartedAtUtc);

    private readonly record struct ManagedProcessStopResult(
        bool StoppedProcess,
        bool ForcedKill,
        bool StillAlive);
}
