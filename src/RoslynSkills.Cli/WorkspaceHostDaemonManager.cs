using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    string RepoRoot,
    string? LogPath = null);

public sealed record WorkspaceHostLaunchMetadata(
    int? DaemonPid,
    string? LaunchMode,
    string? DaemonLifetime,
    string? DaemonLifetimeNote,
    string? CallerJobFlags,
    string? LaunchWarning,
    bool Reused = false);

public sealed record WorkspaceHostFailure(
    string Reason,
    string Message,
    int? DaemonPid,
    string DaemonEndpoint,
    int? ExitCode,
    string? LogPath,
    string? LastLogLine,
    string? DaemonLifetime = null,
    string? LaunchMode = null,
    string? CallerJobFlags = null)
{
    public object ToDetails()
        => new
        {
            reason = Reason,
            daemon_pid = DaemonPid,
            daemon_endpoint = DaemonEndpoint,
            daemon_lifetime = DaemonLifetime,
            launch_mode = LaunchMode,
            caller_job_flags = CallerJobFlags,
            exit_code = ExitCode,
            log_path = LogPath,
            last_log_line = LastLogLine,
        };
}

public sealed record WorkspaceHostConnectionInfo(
    WorkspaceHostDaemonEndpoint Endpoint,
    string? AuthToken,
    int? DaemonPid,
    DateTime ManifestLastWriteUtc);

public sealed record WorkspaceHostSendResult(
    WorkspaceHostResponse? Response,
    WorkspaceHostFailure? Failure)
{
    public bool Succeeded => Response is not null;
}

public sealed class WorkspaceHostDaemonManager
{
    public const string LifetimeDetached = "detached";
    public const string LifetimeCallerJob = "caller_job";
    public const string LifetimeForeground = "foreground";
    public const int DefaultMaxDaemonHosts = 8;
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    private const string ManifestVersion = "1.0";
    private const int MaxUnixSocketPathUtf8Bytes = 100;
    private static readonly TimeSpan LiveHostConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeadHostProbeTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MissingManifestProbeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string? GetDaemonLifetimeNote(string? daemonLifetime)
        => string.Equals(daemonLifetime, LifetimeCallerJob, StringComparison.OrdinalIgnoreCase)
            ? "The workspace host started inside the calling shell's Windows Job Object (KILL_ON_JOB_CLOSE without BREAKAWAY_OK) and ends when the calling shell exits. To keep the daemon alive across shells, start 'roscli daemon.run' in a long-lived runner (such as a background job) before running 'roscli workspace.use', or unset ROSCLI_DAEMON_LAUNCH=nodetach."
            : null;

    public static int ResolveMaxDaemonHosts()
    {
        string? raw = Environment.GetEnvironmentVariable("ROSLYNSKILLS_MAX_DAEMON_HOSTS");
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return DefaultMaxDaemonHosts;
    }

    public static TimeSpan? ResolveIdleTimeout(TimeSpan? explicitIdleTimeout)
    {
        if (explicitIdleTimeout.HasValue)
        {
            return explicitIdleTimeout.Value > TimeSpan.Zero ? explicitIdleTimeout.Value : null;
        }

        string? secondsEnv = Environment.GetEnvironmentVariable("ROSLYNSKILLS_DAEMON_IDLE_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(secondsEnv) &&
            double.TryParse(secondsEnv.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double envSeconds))
        {
            return envSeconds > 0 ? TimeSpan.FromSeconds(envSeconds) : null;
        }

        string? minutesEnv = Environment.GetEnvironmentVariable("ROSLYNSKILLS_DAEMON_IDLE_TIMEOUT_MINUTES");
        if (!string.IsNullOrWhiteSpace(minutesEnv) &&
            double.TryParse(minutesEnv.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double envMinutes))
        {
            return envMinutes > 0 ? TimeSpan.FromMinutes(envMinutes) : null;
        }

        return DefaultIdleTimeout;
    }

    public static bool IsNoDetachRequested(bool? explicitNoDetach)
    {
        if (explicitNoDetach.HasValue)
        {
            return explicitNoDetach.Value;
        }

        string? raw = Environment.GetEnvironmentVariable("ROSCLI_DAEMON_LAUNCH");
        return !string.IsNullOrWhiteSpace(raw) &&
               string.Equals(raw.Trim(), "nodetach", StringComparison.OrdinalIgnoreCase);
    }

    public WorkspaceHostDaemonEndpoint GetDefaultEndpoint(string? repoRoot = null)
    {
        string resolvedRepoRoot = RepositoryRootDiscovery.ResolveWorkspaceDaemonRoot(repoRoot);
        string hash = RepositoryRootDiscovery.ComputeWorkspaceStateHash(resolvedRepoRoot);
        string stateDirectory = RepositoryRootDiscovery.GetWorkspaceHostStateDirectory();
        Directory.CreateDirectory(stateDirectory);
        string manifestPath = RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(resolvedRepoRoot);
        string logPath = RepositoryRootDiscovery.GetWorkspaceDaemonLogPath(resolvedRepoRoot);

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
                RepoRoot: resolvedRepoRoot,
                LogPath: logPath);
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
            RepoRoot: resolvedRepoRoot,
            LogPath: logPath);
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

    public Task<CommandEnvelope> StartAsync(
        string? repoRoot,
        string? hostPath,
        CancellationToken cancellationToken)
        => StartAsync(repoRoot, hostPath, noDetach: null, idleTimeout: null, cancellationToken);

    public async Task<CommandEnvelope> StartAsync(
        string? repoRoot,
        string? hostPath,
        bool? noDetach,
        TimeSpan? idleTimeout,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        using IDisposable? startLock = await AcquireWorkspaceStartLockAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (startLock is null)
        {
            return ErrorEnvelope(
                "daemon.start",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"Timed out waiting for launch lock on workspace host endpoint '{FormatEndpoint(endpoint)}'.");
        }

        // Launch order (a) & (b): reuse any existing live host (detached or foreground daemon.run) for this workspace key.
        WorkspaceHostResponse? existing = await TryGetStatusAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (existing?.Ok == true)
        {
            WorkspaceHostDaemonManifest? existingManifest = TryReadManifest(endpoint.ManifestPath);
            string existingLifetime = existingManifest?.DaemonLifetime ?? LifetimeDetached;
            return SuccessEnvelope(
                "daemon.start",
                new
                {
                    started = false,
                    already_running = true,
                    reused = true,
                    daemon_endpoint = FormatEndpoint(endpoint),
                    daemon_pid = existingManifest?.ProcessId,
                    manifest_path = endpoint.ManifestPath,
                    log_path = existingManifest?.LogPath ?? ResolveLogPath(endpoint, existingManifest),
                    launch_mode = existingManifest?.LaunchMode,
                    daemon_lifetime = existingLifetime,
                    daemon_lifetime_note = GetDaemonLifetimeNote(existingLifetime),
                    caller_job_flags = existingManifest?.CallerJobFlags,
                    launch_warning = existingManifest?.LaunchWarning,
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

        // Enforce per-user host cap (and clean up stale dead-PID manifests across all workspace keys).
        CommandEnvelope? limitError = await CheckHostCapAsync("daemon.start", endpoint.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (limitError is not null)
        {
            return limitError;
        }

        string resolvedHostPath = ResolveHostPath(hostPath);
        string authToken = GenerateAuthToken();
        string logPath = ResolveLogPath(endpoint, manifest: null);
        TimeSpan? resolvedIdleTimeout = ResolveIdleTimeout(idleTimeout);
        bool forceNoDetach = IsNoDetachRequested(noDetach);

        LaunchedDaemonProcess launched = StartDaemonProcess(
            endpoint,
            resolvedHostPath,
            authToken,
            logPath,
            resolvedIdleTimeout,
            forceNoDetach);
        using (launched.Process)
        {
            WorkspaceHostDaemonManifest manifest = new(
                ManifestVersion,
                endpoint.Transport,
                endpoint.PipeName,
                endpoint.SocketPath,
                endpoint.RepoRoot,
                resolvedHostPath,
                authToken,
                launched.Process.Id,
                DateTimeOffset.UtcNow,
                LogPath: logPath,
                LaunchMode: launched.LaunchMode,
                DaemonLifetime: launched.DaemonLifetime,
                CallerJobFlags: launched.CallerJobFlags,
                LaunchWarning: launched.LaunchWarning);
            WriteManifest(endpoint.ManifestPath, manifest);

            WorkspaceHostResponse? status = await WaitForStatusAsync(endpoint, launched.Process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (status?.Ok != true)
            {
                int? exitCode = TryGetProcessExitCode(launched.Process);
                bool exitedBeforeReady = IsProcessExited(launched.Process);
                TryKillProcessTree(launched.Process);
                await WaitForProcessExitAsync(launched.Process, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                exitCode ??= TryGetProcessExitCode(launched.Process);
                string? lastLogLine = TryReadLastLogLine(logPath);
                TryDeleteManifest(endpoint.ManifestPath);
                if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
                {
                    TryDeleteFileOrSocket(endpoint.SocketPath);
                }

                string detailSuffix = BuildFailureDetailSuffix(exitCode, lastLogLine);
                string reason = exitedBeforeReady ? "host_exited_during_startup" : "startup_timeout";
                WorkspaceHostFailure failure = new(
                    Reason: reason,
                    Message: $"Started workspace host process {launched.Process.Id} ({launched.LaunchMode}), but it did not answer daemon/status on endpoint '{FormatEndpoint(endpoint)}'{detailSuffix}.",
                    DaemonPid: launched.Process.Id,
                    DaemonEndpoint: FormatEndpoint(endpoint),
                    ExitCode: exitCode,
                    LogPath: logPath,
                    LastLogLine: lastLogLine,
                    DaemonLifetime: launched.DaemonLifetime,
                    LaunchMode: launched.LaunchMode,
                    CallerJobFlags: launched.CallerJobFlags);
                return ErrorEnvelope(
                    "daemon.start",
                    WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                    failure.Message,
                    failure.ToDetails());
            }

            return SuccessEnvelope(
                "daemon.start",
                new
                {
                    started = true,
                    already_running = false,
                    reused = false,
                    daemon_endpoint = FormatEndpoint(endpoint),
                    daemon_pid = launched.Process.Id,
                    manifest_path = endpoint.ManifestPath,
                    log_path = logPath,
                    launch_mode = launched.LaunchMode,
                    daemon_lifetime = launched.DaemonLifetime,
                    daemon_lifetime_note = GetDaemonLifetimeNote(launched.DaemonLifetime),
                    caller_job_flags = launched.CallerJobFlags,
                    launch_warning = launched.LaunchWarning,
                    idle_timeout_seconds = resolvedIdleTimeout is { } t ? Math.Round(t.TotalSeconds, 1) : (double?)null,
                    host_path = resolvedHostPath,
                    status = status.Data,
                });
        }
    }

    public Task<int> RunForegroundAsync(
        string? repoRoot,
        string? hostPath,
        bool? exitOnStdinEof,
        Func<CommandEnvelope, Task> writeEnvelopeAsync,
        CancellationToken cancellationToken)
        => RunForegroundAsync(repoRoot, hostPath, exitOnStdinEof, idleTimeout: null, writeEnvelopeAsync, cancellationToken);

    public async Task<int> RunForegroundAsync(
        string? repoRoot,
        string? hostPath,
        bool? exitOnStdinEof,
        TimeSpan? idleTimeout,
        Func<CommandEnvelope, Task> writeEnvelopeAsync,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        IDisposable? startLock = await AcquireWorkspaceStartLockAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (startLock is null)
        {
            await writeEnvelopeAsync(ErrorEnvelope(
                "daemon.run",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"Timed out waiting for launch lock on workspace host endpoint '{FormatEndpoint(endpoint)}'.")).ConfigureAwait(false);
            return 1;
        }

        Process? hostProcess = null;
        int hostPid = 0;
        string resolvedHostPath;
        string logPath;
        bool watchStdin;
        TimeSpan? resolvedIdleTimeout;
        string? callerJobFlags = OperatingSystem.IsWindows() ? QueryCallerJobInfoWindows().FormattedFlags : null;
        WorkspaceHostResponse? readyStatus = null;

        try
        {
            WorkspaceHostResponse? existing = await TryGetStatusAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (existing?.Ok == true)
            {
                WorkspaceHostDaemonManifest? existingManifest = TryReadManifest(endpoint.ManifestPath);
                await writeEnvelopeAsync(ErrorEnvelope(
                    "daemon.run",
                    WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                    $"A workspace host daemon (pid {existingManifest?.ProcessId}) is already running on endpoint '{FormatEndpoint(endpoint)}'. Stop it first with 'roscli daemon.stop'.",
                    new
                    {
                        reason = "already_running",
                        daemon_pid = existingManifest?.ProcessId,
                        daemon_endpoint = FormatEndpoint(endpoint),
                        daemon_lifetime = existingManifest?.DaemonLifetime ?? LifetimeDetached,
                    })).ConfigureAwait(false);
                return 1;
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
                    await writeEnvelopeAsync(ErrorEnvelope(
                        "daemon.run",
                        WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                        $"Unresponsive workspace host process {staleManifest.ProcessId} on endpoint '{FormatEndpoint(endpoint)}' could not be terminated.")).ConfigureAwait(false);
                    return 1;
                }

                TryDeleteManifest(endpoint.ManifestPath);
            }

            if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
            {
                TryDeleteFileOrSocket(endpoint.SocketPath);
            }

            CommandEnvelope? limitError = await CheckHostCapAsync("daemon.run", endpoint.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (limitError is not null)
            {
                await writeEnvelopeAsync(limitError).ConfigureAwait(false);
                return 1;
            }

            resolvedHostPath = ResolveHostPath(hostPath);
            string authToken = GenerateAuthToken();
            logPath = ResolveLogPath(endpoint, manifest: null);
            watchStdin = exitOnStdinEof ?? ShouldWatchStdinForEof();
            resolvedIdleTimeout = ResolveIdleTimeout(idleTimeout);

            string dotnetExecutable = ResolveDotnetExecutable();
            List<string> daemonArguments = BuildDaemonArguments(
                resolvedHostPath,
                endpoint,
                authToken,
                logPath,
                resolvedIdleTimeout,
                launchWarning: null);
            daemonArguments.Add("--exit-on-stdin-eof");

            ProcessStartInfo startInfo = new(dotnetExecutable)
            {
                WorkingDirectory = ResolveHostWorkingDirectory(endpoint),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in daemonArguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            hostProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start RoslynSkills workspace host process.");
            hostPid = hostProcess.Id;
            _ = Task.Run(async () =>
            {
                try { await hostProcess.StandardOutput.ReadToEndAsync().ConfigureAwait(false); } catch { }
            });
            _ = Task.Run(async () =>
            {
                try { await hostProcess.StandardError.ReadToEndAsync().ConfigureAwait(false); } catch { }
            });

            WorkspaceHostDaemonManifest manifest = new(
                ManifestVersion,
                endpoint.Transport,
                endpoint.PipeName,
                endpoint.SocketPath,
                endpoint.RepoRoot,
                resolvedHostPath,
                authToken,
                hostPid,
                DateTimeOffset.UtcNow,
                LogPath: logPath,
                LaunchMode: "foreground",
                DaemonLifetime: LifetimeForeground,
                CallerJobFlags: callerJobFlags);
            WriteManifest(endpoint.ManifestPath, manifest);

            readyStatus = await WaitForStatusAsync(endpoint, hostProcess, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (readyStatus?.Ok != true)
            {
                int? exitCode = TryGetProcessExitCode(hostProcess);
                bool exitedBeforeReady = IsProcessExited(hostProcess);
                TryKillProcessTree(hostProcess);
                await WaitForProcessExitAsync(hostProcess, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                exitCode ??= TryGetProcessExitCode(hostProcess);
                string? lastLogLine = TryReadLastLogLine(logPath);
                TryDeleteManifest(endpoint.ManifestPath);
                if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
                {
                    TryDeleteFileOrSocket(endpoint.SocketPath);
                }

                string detailSuffix = BuildFailureDetailSuffix(exitCode, lastLogLine);
                string reason = exitedBeforeReady ? "host_exited_during_startup" : "startup_timeout";
                WorkspaceHostFailure failure = new(
                    Reason: reason,
                    Message: $"Started foreground workspace host process {hostPid}, but it did not answer daemon/status on endpoint '{FormatEndpoint(endpoint)}'{detailSuffix}.",
                    DaemonPid: hostPid,
                    DaemonEndpoint: FormatEndpoint(endpoint),
                    ExitCode: exitCode,
                    LogPath: logPath,
                    LastLogLine: lastLogLine,
                    DaemonLifetime: LifetimeForeground,
                    LaunchMode: "foreground",
                    CallerJobFlags: callerJobFlags);
                await writeEnvelopeAsync(ErrorEnvelope(
                    "daemon.run",
                    WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                    failure.Message,
                    failure.ToDetails())).ConfigureAwait(false);
                hostProcess.Dispose();
                return 1;
            }
        }
        finally
        {
            // Release the start lock as soon as startup finishes so subsequent workspace.use calls can reuse this host.
            startLock.Dispose();
        }

        using Process ownedHostProcess = hostProcess!;
        using CancellationTokenSource stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            try
            {
                stopCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        };
        PosixSignalRegistration? sigTermRegistration = null;
        PosixSignalRegistration? sigIntRegistration = null;
        bool stopRequested = false;

        try
        {
            Console.CancelKeyPress += cancelHandler;
            if (!OperatingSystem.IsWindows())
            {
                sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    try { stopCts.Cancel(); } catch (ObjectDisposedException) { }
                });
                sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                {
                    ctx.Cancel = true;
                    try { stopCts.Cancel(); } catch (ObjectDisposedException) { }
                });
            }

            await writeEnvelopeAsync(SuccessEnvelope(
                "daemon.run",
                new
                {
                    running = true,
                    foreground = true,
                    daemon_endpoint = FormatEndpoint(endpoint),
                    daemon_pid = hostPid,
                    manifest_path = endpoint.ManifestPath,
                    log_path = logPath,
                    launch_mode = "foreground",
                    daemon_lifetime = LifetimeForeground,
                    caller_job_flags = callerJobFlags,
                    exit_on_stdin_eof = watchStdin,
                    idle_timeout_seconds = resolvedIdleTimeout is { } t ? Math.Round(t.TotalSeconds, 1) : (double?)null,
                    host_path = resolvedHostPath,
                    status = readyStatus!.Data,
                })).ConfigureAwait(false);

            if (watchStdin)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using Stream stdin = Console.OpenStandardInput();
                        byte[] buffer = new byte[64];
                        while (!stopCts.IsCancellationRequested)
                        {
                            int read = await stdin.ReadAsync(buffer, stopCts.Token).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                    }

                    try
                    {
                        stopCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
            }

            Task hostExitTask = ownedHostProcess.WaitForExitAsync(CancellationToken.None);
            Task stopSignalTask = Task.Delay(Timeout.InfiniteTimeSpan, stopCts.Token);
            Task completed = await Task.WhenAny(hostExitTask, stopSignalTask).ConfigureAwait(false);

            if (!ReferenceEquals(completed, hostExitTask))
            {
                stopRequested = true;
                try
                {
                    // Closing child stdin triggers WorkspaceHost --exit-on-stdin-eof clean shutdown.
                    ownedHostProcess.StandardInput.Close();
                }
                catch
                {
                }

                if (!await WaitForProcessExitAsync(ownedHostProcess, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false))
                {
                    _ = await SendWithDiagnosticsAsync(
                        endpoint,
                        new WorkspaceHostRequest(
                            Id: Guid.NewGuid().ToString("N"),
                            Method: WorkspaceHostProtocol.Method.Shutdown),
                        TimeSpan.FromSeconds(2),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!await WaitForProcessExitAsync(ownedHostProcess, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false))
                    {
                        TryKillProcessTree(ownedHostProcess);
                        await WaitForProcessExitAsync(ownedHostProcess, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await hostExitTask.ConfigureAwait(false);
            }

            int? finalExitCode = TryGetProcessExitCode(ownedHostProcess);
            return stopRequested || finalExitCode == 0 ? 0 : (finalExitCode ?? 1);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            sigTermRegistration?.Dispose();
            sigIntRegistration?.Dispose();
            try
            {
                stopCts.Cancel();
            }
            catch
            {
            }

            if (!IsProcessExited(ownedHostProcess))
            {
                TryKillProcessTree(ownedHostProcess);
            }

            WorkspaceHostDaemonManifest? currentManifest = TryReadManifest(endpoint.ManifestPath);
            if (currentManifest is null || currentManifest.ProcessId == hostPid)
            {
                TryDeleteManifest(endpoint.ManifestPath);
                if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
                {
                    TryDeleteFileOrSocket(endpoint.SocketPath);
                }
            }
        }
    }

    private static LaunchedDaemonProcess StartDaemonProcess(
        WorkspaceHostDaemonEndpoint endpoint,
        string resolvedHostPath,
        string authToken,
        string logPath,
        TimeSpan? idleTimeout,
        bool forceNoDetach)
    {
        string dotnetExecutable = ResolveDotnetExecutable();
        if (OperatingSystem.IsWindows())
        {
            return StartWindowsDetachedDaemon(
                dotnetExecutable,
                resolvedHostPath,
                endpoint,
                authToken,
                logPath,
                idleTimeout,
                forceNoDetach);
        }

        List<string> daemonArguments = BuildDaemonArguments(
            resolvedHostPath,
            endpoint,
            authToken,
            logPath,
            idleTimeout,
            launchWarning: null);

        // On Unix, starting dotnet directly with UseShellExecute=false causes the background daemon (and any
        // MSBuild/restore children it spawns) to inherit roscli's stdout/stderr pipe descriptors, hanging any
        // caller reading roscli output until EOF. Launching through `/bin/sh` with `exec` detaches stdin/stdout/stderr
        // to /dev/null and ignores SIGHUP while keeping the exact daemon PID.
        ProcessStartInfo unixStartInfo = new("/bin/sh")
        {
            WorkingDirectory = ResolveHostWorkingDirectory(endpoint),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add("trap '' HUP; exec \"$@\" </dev/null >/dev/null 2>&1");
        unixStartInfo.ArgumentList.Add("roscli-daemon");
        unixStartInfo.ArgumentList.Add(dotnetExecutable);
        foreach (string arg in daemonArguments)
        {
            unixStartInfo.ArgumentList.Add(arg);
        }

        Process unixProcess = Process.Start(unixStartInfo)
            ?? throw new InvalidOperationException("Failed to start RoslynSkills workspace host process.");
        return new LaunchedDaemonProcess(
            Process: unixProcess,
            LaunchMode: "unix_sh_exec",
            DaemonLifetime: LifetimeDetached,
            CallerJobFlags: null,
            LaunchWarning: null);
    }

    private static string ResolveHostWorkingDirectory(WorkspaceHostDaemonEndpoint endpoint)
    {
        string? stateDir = Path.GetDirectoryName(endpoint.ManifestPath);
        if (!string.IsNullOrWhiteSpace(stateDir))
        {
            Directory.CreateDirectory(stateDir);
            return stateDir;
        }

        return Path.GetTempPath();
    }

    [SupportedOSPlatform("windows")]
    private static LaunchedDaemonProcess StartWindowsDetachedDaemon(
        string dotnetExecutable,
        string resolvedHostPath,
        WorkspaceHostDaemonEndpoint endpoint,
        string authToken,
        string logPath,
        TimeSpan? idleTimeout,
        bool forceNoDetach)
    {
        string hostWorkingDirectory = ResolveHostWorkingDirectory(endpoint);
        CallerJobInfo jobInfo = QueryCallerJobInfoWindows();
        List<string> baseArguments = BuildDaemonArguments(
            resolvedHostPath,
            endpoint,
            authToken,
            logPath,
            idleTimeout,
            launchWarning: null);
        string baseCommandLine = BuildWindowsCommandLine(dotnetExecutable, baseArguments);

        // Launch order (c): Prefer Win32 CreateProcessW with CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW | DETACHED_PROCESS
        // and bInheritHandles = false. Where the caller is in no job or its job allows breakaway, this detaches cleanly
        // without handle leaks while preserving the environment block.
        if (TryStartWindowsProcess(baseCommandLine, hostWorkingDirectory, breakawayFromJob: true, out Process? breakawayProcess, out int breakawayError) &&
            breakawayProcess is not null)
        {
            return new LaunchedDaemonProcess(
                Process: breakawayProcess,
                LaunchMode: "create_process_breakaway",
                DaemonLifetime: LifetimeDetached,
                CallerJobFlags: jobInfo.FormattedFlags,
                LaunchWarning: null);
        }

        // Launch order (d): Breakaway was refused by the caller's Windows Job Object. Build an explicit warning with
        // the caller job flags and process chain, and pass it to the host so it is written to both the answer and <hash>.log.
        string callerChain = GetWindowsCallerProcessChain();
        string jobFlagsLabel = jobInfo.FormattedFlags ?? $"win32_error={breakawayError}";
        string idleLabel = idleTimeout is { } t ? $"{Math.Round(t.TotalMinutes, 1):G}m" : "disabled";
        bool callerJobKillsOnClose = jobInfo.KillOnJobClose || (jobInfo.InJob && breakawayError == 5);

        if (forceNoDetach)
        {
            string noDetachWarning =
                $"Breakaway from Windows Job Object was refused (job_flags={jobFlagsLabel}, caller_chain={callerChain}); " +
                $"ROSCLI_DAEMON_LAUNCH=nodetach requested, so the host started inside the caller job (daemon_lifetime={LifetimeCallerJob}) " +
                $"and ends when the calling shell exits. Run 'roscli daemon.run' in a long-lived runner to keep it alive across shells.";
            List<string> noDetachArgs = BuildDaemonArguments(resolvedHostPath, endpoint, authToken, logPath, idleTimeout, noDetachWarning);
            string noDetachCmd = BuildWindowsCommandLine(dotnetExecutable, noDetachArgs);
            if (TryStartWindowsProcess(noDetachCmd, hostWorkingDirectory, breakawayFromJob: false, out Process? inJobProcess, out _) &&
                inJobProcess is not null)
            {
                return new LaunchedDaemonProcess(
                    Process: inJobProcess,
                    LaunchMode: callerJobKillsOnClose ? "caller_job" : "create_process_detached",
                    DaemonLifetime: callerJobKillsOnClose ? LifetimeCallerJob : LifetimeDetached,
                    CallerJobFlags: jobInfo.FormattedFlags,
                    LaunchWarning: noDetachWarning);
            }
        }
        else
        {
            string wmiWarning =
                $"Breakaway from Windows Job Object was refused (job_flags={jobFlagsLabel}, caller_chain={callerChain}); " +
                $"launched detached host via WMI Win32_Process.Create (idle timeout {idleLabel}, bounded by per-user host cap). " +
                $"Prefer 'roscli daemon.run' under a long-lived runner where available, or set ROSCLI_DAEMON_LAUNCH=nodetach to stay inside the caller job.";
            List<string> wmiArgs = BuildDaemonArguments(resolvedHostPath, endpoint, authToken, logPath, idleTimeout, wmiWarning);
            string wmiCmd = BuildWindowsCommandLine(dotnetExecutable, wmiArgs);
            if (TryStartWindowsProcessViaWmi(wmiCmd, hostWorkingDirectory, out Process? wmiProcess) &&
                wmiProcess is not null)
            {
                return new LaunchedDaemonProcess(
                    Process: wmiProcess,
                    LaunchMode: "wmi_win32_process",
                    DaemonLifetime: LifetimeDetached,
                    CallerJobFlags: jobInfo.FormattedFlags,
                    LaunchWarning: wmiWarning);
            }
        }

        string fallbackWarning =
            $"Breakaway from Windows Job Object was refused (job_flags={jobFlagsLabel}, caller_chain={callerChain}) and detached WMI launch was unavailable or disabled; " +
            $"started inside the caller job (daemon_lifetime={(callerJobKillsOnClose ? LifetimeCallerJob : LifetimeDetached)}).";
        List<string> fallbackArgs = BuildDaemonArguments(resolvedHostPath, endpoint, authToken, logPath, idleTimeout, fallbackWarning);
        string fallbackCmd = BuildWindowsCommandLine(dotnetExecutable, fallbackArgs);
        if (TryStartWindowsProcess(fallbackCmd, hostWorkingDirectory, breakawayFromJob: false, out Process? directInJob, out _) &&
            directInJob is not null)
        {
            return new LaunchedDaemonProcess(
                Process: directInJob,
                LaunchMode: callerJobKillsOnClose ? "caller_job" : "create_process_detached",
                DaemonLifetime: callerJobKillsOnClose ? LifetimeCallerJob : LifetimeDetached,
                CallerJobFlags: jobInfo.FormattedFlags,
                LaunchWarning: fallbackWarning);
        }

        ProcessStartInfo fallbackStartInfo = new(dotnetExecutable)
        {
            WorkingDirectory = hostWorkingDirectory,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string arg in fallbackArgs)
        {
            fallbackStartInfo.ArgumentList.Add(arg);
        }

        Process fallbackProcess = Process.Start(fallbackStartInfo)
            ?? throw new InvalidOperationException("Failed to start RoslynSkills workspace host process.");
        return new LaunchedDaemonProcess(
            Process: fallbackProcess,
            LaunchMode: callerJobKillsOnClose ? "caller_job" : "shell_execute_fallback",
            DaemonLifetime: callerJobKillsOnClose ? LifetimeCallerJob : LifetimeDetached,
            CallerJobFlags: jobInfo.FormattedFlags,
            LaunchWarning: fallbackWarning);
    }

    [SupportedOSPlatform("windows")]
    private static CallerJobInfo QueryCallerJobInfoWindows()
    {
        const uint JobObjectLimitBreakawayOk = 0x00000800;
        const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
        const uint JobObjectLimitKillOnJobClose = 0x00002000;
        const int JobObjectExtendedLimitInformation = 9;

        try
        {
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool inJob) || !inJob)
            {
                return new CallerJobInfo(InJob: false, LimitFlags: null, FormattedFlags: null, KillOnJobClose: false, BreakawayOk: true);
            }

            int size = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation, buffer, (uint)size, out _))
                {
                    JobObjectExtendedLimitInfo info = Marshal.PtrToStructure<JobObjectExtendedLimitInfo>(buffer);
                    uint flags = info.BasicLimitInformation.LimitFlags;
                    bool killOnClose = (flags & JobObjectLimitKillOnJobClose) != 0;
                    bool breakawayOk = (flags & (JobObjectLimitBreakawayOk | JobObjectLimitSilentBreakawayOk)) != 0;
                    return new CallerJobInfo(
                        InJob: true,
                        LimitFlags: flags,
                        FormattedFlags: FormatJobLimitFlags(flags),
                        KillOnJobClose: killOnClose,
                        BreakawayOk: breakawayOk);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new CallerJobInfo(InJob: true, LimitFlags: null, FormattedFlags: "in_job(unreadable_flags)", KillOnJobClose: false, BreakawayOk: false);
        }
        catch
        {
            return new CallerJobInfo(InJob: false, LimitFlags: null, FormattedFlags: null, KillOnJobClose: false, BreakawayOk: false);
        }
    }

    private static string FormatJobLimitFlags(uint flags)
    {
        List<string> names = new();
        if ((flags & 0x00000800) != 0) names.Add("BREAKAWAY_OK");
        if ((flags & 0x00001000) != 0) names.Add("SILENT_BREAKAWAY_OK");
        if ((flags & 0x00002000) != 0) names.Add("KILL_ON_JOB_CLOSE");
        if ((flags & 0x00000100) != 0) names.Add("PROCESS_MEMORY");
        if ((flags & 0x00000200) != 0) names.Add("JOB_MEMORY");
        if ((flags & 0x00000400) != 0) names.Add("DIE_ON_UNHANDLED_EXCEPTION");
        string hex = $"0x{flags:X8}";
        return names.Count == 0 ? hex : $"{hex} ({string.Join(" | ", names)})";
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsCallerProcessChain()
    {
        const uint Th32csSnapProcess = 0x00000002;
        IntPtr invalidHandle = new(-1);
        IntPtr snapshot = IntPtr.Zero;
        try
        {
            snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == invalidHandle)
            {
                return $"pid({Environment.ProcessId})";
            }

            Dictionary<int, (int ParentPid, string Name)> processMap = new();
            ProcessEntry32W entry = new() { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    processMap[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile ?? "unknown");
                }
                while (Process32NextW(snapshot, ref entry));
            }

            List<string> chain = new();
            HashSet<int> visited = new();
            int currentPid = Environment.ProcessId;
            for (int depth = 0; depth < 6 && currentPid > 0 && visited.Add(currentPid); depth++)
            {
                if (!processMap.TryGetValue(currentPid, out (int ParentPid, string Name) info))
                {
                    chain.Add($"pid({currentPid})");
                    break;
                }

                string shortName = Path.GetFileNameWithoutExtension(info.Name);
                chain.Add($"{shortName}({currentPid})");
                if (info.ParentPid <= 0 || info.ParentPid == currentPid)
                {
                    break;
                }

                currentPid = info.ParentPid;
            }

            return chain.Count > 0 ? string.Join(" <- ", chain) : $"pid({Environment.ProcessId})";
        }
        catch
        {
            return $"pid({Environment.ProcessId})";
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != invalidHandle)
            {
                CloseHandle(snapshot);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryStartWindowsProcessViaWmi(
        string commandLine,
        string workingDirectory,
        out Process? process)
    {
        process = null;
        object? locator = null;
        object? services = null;
        object? startupClass = null;
        object? startupInstance = null;
        object? processClass = null;
        object? methods = null;
        object? createMethod = null;
        object? inParamsClass = null;
        object? inParams = null;
        object? result = null;
        try
        {
            Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
            {
                return false;
            }

            locator = Activator.CreateInstance(locatorType);
            if (locator is null)
            {
                return false;
            }

            services = InvokeComMethod(locator, "ConnectServer", ".", @"root\cimv2");
            if (services is null)
            {
                return false;
            }

            startupClass = InvokeComMethod(services, "Get", "Win32_ProcessStartup");
            startupInstance = startupClass is not null ? InvokeComMethod(startupClass, "SpawnInstance_") : null;
            if (startupInstance is not null)
            {
                SetComProperty(startupInstance, "ShowWindow", (ushort)0);
            }

            processClass = InvokeComMethod(services, "Get", "Win32_Process");
            if (processClass is null)
            {
                return false;
            }

            methods = GetComProperty(processClass, "Methods_");
            createMethod = methods is not null ? InvokeComMethod(methods, "Item", "Create") : null;
            inParamsClass = createMethod is not null ? GetComProperty(createMethod, "InParameters") : null;
            inParams = inParamsClass is not null ? InvokeComMethod(inParamsClass, "SpawnInstance_") : null;
            if (inParams is null)
            {
                return false;
            }

            SetComProperty(inParams, "CommandLine", commandLine);
            SetComProperty(inParams, "CurrentDirectory", workingDirectory);
            if (startupInstance is not null)
            {
                SetComProperty(inParams, "ProcessStartupInformation", startupInstance);
            }

            result = InvokeComMethod(processClass, "ExecMethod_", "Create", inParams);
            if (result is null)
            {
                return false;
            }

            object? returnValueObj = GetWmiPropertyValue(result, "ReturnValue");
            object? processIdObj = GetWmiPropertyValue(result, "ProcessId");
            int returnValue = returnValueObj is not null ? Convert.ToInt32(returnValueObj, CultureInfo.InvariantCulture) : -1;
            int processId = processIdObj is not null ? Convert.ToInt32(processIdObj, CultureInfo.InvariantCulture) : 0;
            if (returnValue != 0 || processId <= 0)
            {
                return false;
            }

            process = Process.GetProcessById(processId);
            return true;
        }
        catch
        {
            process = null;
            return false;
        }
        finally
        {
            ReleaseComObject(result);
            ReleaseComObject(inParams);
            ReleaseComObject(inParamsClass);
            ReleaseComObject(createMethod);
            ReleaseComObject(methods);
            ReleaseComObject(processClass);
            ReleaseComObject(startupInstance);
            ReleaseComObject(startupClass);
            ReleaseComObject(services);
            ReleaseComObject(locator);
        }
    }

    private static object? InvokeComMethod(object target, string methodName, params object?[] args)
        => target.GetType().InvokeMember(methodName, BindingFlags.InvokeMethod, binder: null, target: target, args: args);

    private static object? GetComProperty(object target, string propertyName)
        => target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, binder: null, target: target, args: null);

    private static void SetComProperty(object target, string propertyName, object? value)
        => target.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, binder: null, target: target, args: new[] { value });

    [SupportedOSPlatform("windows")]
    private static object? GetWmiPropertyValue(object wmiObject, string propertyName)
    {
        object? properties = null;
        object? propertyItem = null;
        try
        {
            properties = GetComProperty(wmiObject, "Properties_");
            if (properties is null)
            {
                return null;
            }

            propertyItem = InvokeComMethod(properties, "Item", propertyName);
            return propertyItem is not null ? GetComProperty(propertyItem, "Value") : null;
        }
        finally
        {
            ReleaseComObject(propertyItem);
            ReleaseComObject(properties);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                _ = Marshal.FinalReleaseComObject(value);
            }
            catch
            {
            }
        }
    }

    private static bool ShouldWatchStdinForEof()
    {
        if (!Console.IsInputRedirected)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            const int StdInputHandle = -10;
            const uint FileTypePipe = 0x0003;
            try
            {
                IntPtr handle = GetStdHandle(StdInputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                return GetFileType(handle) == FileTypePipe;
            }
            catch
            {
                return false;
            }
        }

        // On Linux/Unix, background runners frequently attach stdin to /dev/null (immediate EOF).
        // Only treat redirected stdin as a lifetime pipe when fd 0 does not resolve to /dev/null.
        try
        {
            if (File.Exists("/proc/self/fd/0"))
            {
                FileSystemInfo? target = File.ResolveLinkTarget("/proc/self/fd/0", returnFinalTarget: false);
                if (target is not null)
                {
                    string targetPath = target.FullName;
                    if (string.Equals(targetPath, "/dev/null", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    return targetPath.Contains("pipe:", StringComparison.Ordinal) ||
                           targetPath.Contains("socket:", StringComparison.Ordinal);
                }
            }
        }
        catch
        {
        }

        return true;
    }

    private static List<string> BuildDaemonArguments(
        string resolvedHostPath,
        WorkspaceHostDaemonEndpoint endpoint,
        string authToken,
        string logPath,
        TimeSpan? idleTimeout,
        string? launchWarning)
    {
        List<string> args = new()
        {
            resolvedHostPath,
            "--transport",
            endpoint.Transport,
            "--auth-token",
            authToken,
            "--log-path",
            logPath,
            "--manifest-path",
            endpoint.ManifestPath,
        };
        if (endpoint.Transport == "named-pipe")
        {
            args.Add("--pipe-name");
            args.Add(endpoint.PipeName!);
        }
        else
        {
            args.Add("--socket-path");
            args.Add(endpoint.SocketPath!);
        }

        double idleSeconds = idleTimeout is { } t && t > TimeSpan.Zero ? t.TotalSeconds : 0.0;
        args.Add("--idle-timeout-seconds");
        args.Add(idleSeconds.ToString("G", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(launchWarning))
        {
            args.Add("--launch-warning");
            args.Add(launchWarning);
        }

        return args;
    }

    internal static string BuildWindowsCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new();
        AppendWindowsArgument(builder, executable);
        foreach (string argument in arguments)
        {
            builder.Append(' ');
            AppendWindowsArgument(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendWindowsArgument(StringBuilder builder, string argument)
    {
        if (argument.Length == 0)
        {
            builder.Append("\"\"");
            return;
        }

        bool needsQuotes = false;
        foreach (char ch in argument)
        {
            if (char.IsWhiteSpace(ch) || ch == '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        int backslashes = 0;
        foreach (char ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
            }
            else if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(ch);
            }
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }

        builder.Append('"');
    }

    [SupportedOSPlatform("windows")]
    private static bool TryStartWindowsProcess(
        string commandLine,
        string workingDirectory,
        bool breakawayFromJob,
        out Process? process,
        out int lastWin32Error)
    {
        process = null;
        lastWin32Error = 0;
        const uint DetachedProcess = 0x00000008;
        const uint CreateBreakawayFromJob = 0x01000000;
        const uint CreateNoWindow = 0x08000000;
        const uint CreateUnicodeEnvironment = 0x00000400;
        const uint StartfUseShowWindow = 0x00000001;
        const short SwHide = 0;

        StartupInfoW startupInfo = new()
        {
            cb = Marshal.SizeOf<StartupInfoW>(),
            dwFlags = StartfUseShowWindow,
            wShowWindow = SwHide,
        };

        uint creationFlags = CreateNoWindow | DetachedProcess | CreateUnicodeEnvironment;
        if (breakawayFromJob)
        {
            creationFlags |= CreateBreakawayFromJob;
        }

        StringBuilder mutableCommandLine = new(commandLine);
        bool created = CreateProcessW(
            lpApplicationName: null,
            lpCommandLine: mutableCommandLine,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: creationFlags,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: workingDirectory,
            ref startupInfo,
            out ProcessInformation processInfo);
        if (!created)
        {
            lastWin32Error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            process = Process.GetProcessById((int)processInfo.dwProcessId);
            return true;
        }
        catch
        {
            process = null;
            return false;
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero)
            {
                CloseHandle(processInfo.hThread);
            }

            if (processInfo.hProcess != IntPtr.Zero)
            {
                CloseHandle(processInfo.hProcess);
            }
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(dotnetRoot))
            {
                string candidate = Path.Combine(dotnetRoot, "dotnet.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (string segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(segment, "dotnet.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            string programFilesCandidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet",
                "dotnet.exe");
            if (File.Exists(programFilesCandidate))
            {
                return programFilesCandidate;
            }
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
        WorkspaceHostSendResult result = await SendWithDiagnosticsAsync(
            endpoint,
            new WorkspaceHostRequest(
                Id: Guid.NewGuid().ToString("N"),
                Method: WorkspaceHostProtocol.Method.DaemonStatus),
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        if (result.Response?.Ok != true)
        {
            WorkspaceHostFailure failure = result.Failure ?? new WorkspaceHostFailure(
                Reason: "daemon_unavailable",
                Message: $"No workspace host answered on endpoint '{FormatEndpoint(endpoint)}'.",
                DaemonPid: manifest?.ProcessId,
                DaemonEndpoint: FormatEndpoint(endpoint),
                ExitCode: null,
                LogPath: ResolveLogPath(endpoint, manifest),
                LastLogLine: TryReadLastLogLine(ResolveLogPath(endpoint, manifest)));
            return ErrorEnvelope(
                "daemon.status",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                failure.Message,
                failure.ToDetails());
        }

        string daemonLifetime = manifest?.DaemonLifetime ?? LifetimeDetached;
        return SuccessEnvelope(
            "daemon.status",
            new
            {
                running = true,
                daemon_endpoint = FormatEndpoint(endpoint),
                daemon_pid = manifest?.ProcessId,
                manifest_path = endpoint.ManifestPath,
                log_path = manifest?.LogPath ?? ResolveLogPath(endpoint, manifest),
                launch_mode = manifest?.LaunchMode,
                daemon_lifetime = daemonLifetime,
                daemon_lifetime_note = GetDaemonLifetimeNote(daemonLifetime),
                caller_job_flags = manifest?.CallerJobFlags,
                launch_warning = manifest?.LaunchWarning,
                status = result.Response.Data,
            });
    }

    public async Task<CommandEnvelope> StopAsync(
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        WorkspaceHostSendResult sendResult = await SendWithDiagnosticsAsync(
            endpoint,
            new WorkspaceHostRequest(
                Id: Guid.NewGuid().ToString("N"),
                Method: WorkspaceHostProtocol.Method.Shutdown),
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        WorkspaceHostResponse? response = sendResult.Response;

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
            WorkspaceHostFailure failure = sendResult.Failure ?? new WorkspaceHostFailure(
                Reason: "no_host",
                Message: $"No workspace host answered shutdown on endpoint '{FormatEndpoint(endpoint)}'.",
                DaemonPid: manifest?.ProcessId,
                DaemonEndpoint: FormatEndpoint(endpoint),
                ExitCode: null,
                LogPath: ResolveLogPath(endpoint, manifest),
                LastLogLine: TryReadLastLogLine(ResolveLogPath(endpoint, manifest)),
                DaemonLifetime: manifest?.DaemonLifetime,
                LaunchMode: manifest?.LaunchMode,
                CallerJobFlags: manifest?.CallerJobFlags);
            return ErrorEnvelope(
                "daemon.stop",
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                failure.Message,
                failure.ToDetails());
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

    public async Task<CommandEnvelope> ListAsync(CancellationToken cancellationToken)
    {
        string stateDirectory = RepositoryRootDiscovery.GetWorkspaceHostStateDirectory();
        (List<ManagedHostSnapshot> liveHosts, List<string> removedStaleManifests) = await ScanManagedHostsAsync(
            stateDirectory,
            excludeManifestPath: null,
            deleteDeadManifests: true,
            cancellationToken).ConfigureAwait(false);

        return SuccessEnvelope(
            "daemon.list",
            new
            {
                state_directory = stateDirectory,
                active_count = liveHosts.Count,
                max_hosts = ResolveMaxDaemonHosts(),
                cleaned_stale_manifests = removedStaleManifests.Count,
                hosts = liveHosts.Select(h => h.ToPublicObject()).ToArray(),
            });
    }

    public async Task<CommandEnvelope> PruneAsync(
        bool pruneAll,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        string stateDirectory = RepositoryRootDiscovery.GetWorkspaceHostStateDirectory();
        (List<ManagedHostSnapshot> liveHosts, List<string> staleManifests) = await ScanManagedHostsAsync(
            stateDirectory,
            excludeManifestPath: null,
            deleteDeadManifests: !dryRun,
            cancellationToken).ConfigureAwait(false);

        List<object> stoppedHosts = new();
        List<ManagedHostSnapshot> remainingHosts = new();

        foreach (ManagedHostSnapshot host in liveHosts)
        {
            bool folderMissing = !host.RepoRootExists;
            bool shouldStop = pruneAll || folderMissing;
            if (!shouldStop)
            {
                remainingHosts.Add(host);
                continue;
            }

            string reason = folderMissing ? "worktree_missing" : "prune_all";
            if (!dryRun)
            {
                WorkspaceHostDaemonEndpoint hostEndpoint = new(
                    Transport: host.Manifest.Transport,
                    PipeName: host.Manifest.PipeName,
                    SocketPath: host.Manifest.SocketPath,
                    ManifestPath: host.ManifestPath,
                    RepoRoot: host.Manifest.RepoRoot,
                    LogPath: host.Manifest.LogPath);
                _ = await SendWithDiagnosticsAsync(
                    hostEndpoint,
                    new WorkspaceHostRequest(
                        Id: Guid.NewGuid().ToString("N"),
                        Method: WorkspaceHostProtocol.Method.Shutdown),
                    TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
                _ = await StopManagedProcessAsync(
                    host.Manifest,
                    graceTimeout: TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
                TryDeleteManifest(host.ManifestPath);
                if (!string.IsNullOrWhiteSpace(host.Manifest.SocketPath))
                {
                    TryDeleteFileOrSocket(host.Manifest.SocketPath);
                }

                TryDeleteFileOrSocket($"{host.ManifestPath}.lock");
            }

            stoppedHosts.Add(new
            {
                workspace_key = host.WorkspaceKey,
                repo_root = host.Manifest.RepoRoot,
                daemon_pid = host.Manifest.ProcessId,
                launch_mode = host.Manifest.LaunchMode,
                daemon_lifetime = host.Manifest.DaemonLifetime ?? LifetimeDetached,
                reason,
                stopped = !dryRun,
            });
        }

        return SuccessEnvelope(
            "daemon.prune",
            new
            {
                dry_run = dryRun,
                prune_all = pruneAll,
                state_directory = stateDirectory,
                removed_stale_manifests = staleManifests.Count,
                stopped_count = stoppedHosts.Count,
                stopped_hosts = stoppedHosts,
                remaining_live_hosts = remainingHosts.Count,
                remaining_hosts = remainingHosts.Select(h => h.ToPublicObject()).ToArray(),
            });
    }

    public async Task<WorkspaceHostResponse?> SendRequestAsync(
        string? repoRoot,
        WorkspaceHostRequest request,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        WorkspaceHostSendResult result = await SendRequestWithDiagnosticsAsync(
            repoRoot,
            request,
            timeoutDuration,
            cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    public async Task<WorkspaceHostSendResult> SendRequestWithDiagnosticsAsync(
        string? repoRoot,
        WorkspaceHostRequest request,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        return await SendWithDiagnosticsAsync(endpoint, request, timeoutDuration, cancellationToken).ConfigureAwait(false);
    }

    public WorkspaceHostConnectionInfo? TryGetConnectionInfo(string? repoRoot)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        if (manifest is null)
        {
            return null;
        }

        DateTime lastWriteUtc = DateTime.MinValue;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(endpoint.ManifestPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new WorkspaceHostConnectionInfo(
            Endpoint: endpoint,
            AuthToken: manifest.AuthToken,
            DaemonPid: manifest.ProcessId,
            ManifestLastWriteUtc: lastWriteUtc);
    }

    public int? TryGetDaemonProcessId(string? repoRoot)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        return TryReadManifest(endpoint.ManifestPath)?.ProcessId;
    }

    public string? TryGetDaemonLifetime(string? repoRoot)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        return TryReadManifest(endpoint.ManifestPath)?.DaemonLifetime;
    }

    public WorkspaceHostLaunchMetadata? TryGetLaunchMetadata(string? repoRoot)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        if (manifest is null)
        {
            return null;
        }

        string lifetime = manifest.DaemonLifetime ?? LifetimeDetached;
        return new WorkspaceHostLaunchMetadata(
            DaemonPid: manifest.ProcessId,
            LaunchMode: manifest.LaunchMode,
            DaemonLifetime: lifetime,
            DaemonLifetimeNote: GetDaemonLifetimeNote(lifetime),
            CallerJobFlags: manifest.CallerJobFlags,
            LaunchWarning: manifest.LaunchWarning);
    }

    public static string FormatEndpoint(WorkspaceHostDaemonEndpoint endpoint)
        => endpoint.Transport == "named-pipe"
            ? $"pipe:{endpoint.PipeName}"
            : $"unix:{endpoint.SocketPath}";

    private static async Task<IDisposable?> AcquireWorkspaceStartLockAsync(
        WorkspaceHostDaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        string lockPath = $"{endpoint.ManifestPath}.lock";
        string? directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StartLockTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<CommandEnvelope?> CheckHostCapAsync(
        string commandId,
        string? excludeManifestPath,
        CancellationToken cancellationToken)
    {
        int maxHosts = ResolveMaxDaemonHosts();
        string stateDirectory = RepositoryRootDiscovery.GetWorkspaceHostStateDirectory();
        (List<ManagedHostSnapshot> liveHosts, _) = await ScanManagedHostsAsync(
            stateDirectory,
            excludeManifestPath,
            deleteDeadManifests: true,
            cancellationToken).ConfigureAwait(false);
        if (liveHosts.Count < maxHosts)
        {
            return null;
        }

        string message =
            $"Refused to start a new workspace host: {liveHosts.Count} live workspace hosts already running (per-user cap: {maxHosts}). " +
            "Stop unused hosts with 'roscli daemon.prune' (or 'roscli daemon.prune --all'), inspect them with 'roscli daemon.list', or raise ROSLYNSKILLS_MAX_DAEMON_HOSTS.";
        return ErrorEnvelope(
            commandId,
            "daemon_host_limit_reached",
            message,
            new
            {
                reason = "host_limit_reached",
                max_hosts = maxHosts,
                active_count = liveHosts.Count,
                hosts = liveHosts.Select(h => h.ToPublicObject()).ToArray(),
            });
    }

    private async Task<(List<ManagedHostSnapshot> LiveHosts, List<string> StaleManifests)> ScanManagedHostsAsync(
        string stateDirectory,
        string? excludeManifestPath,
        bool deleteDeadManifests,
        CancellationToken cancellationToken)
    {
        List<ManagedHostSnapshot> liveHosts = new();
        List<string> staleManifests = new();
        if (!Directory.Exists(stateDirectory))
        {
            return (liveHosts, staleManifests);
        }

        string? normalizedExclude = string.IsNullOrWhiteSpace(excludeManifestPath)
            ? null
            : Path.GetFullPath(excludeManifestPath);

        string[] manifestFiles;
        try
        {
            manifestFiles = Directory.GetFiles(stateDirectory, "*.json");
        }
        catch
        {
            return (liveHosts, staleManifests);
        }

        foreach (string manifestPath in manifestFiles)
        {
            string fileName = Path.GetFileName(manifestPath);
            if (fileName.EndsWith(".workspaces.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalizedExclude is not null &&
                string.Equals(Path.GetFullPath(manifestPath), normalizedExclude, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            WorkspaceHostDaemonManifest? manifest = TryReadManifest(manifestPath);
            if (manifest is null)
            {
                continue;
            }

            using Process? proc = TryGetManagedDaemonProcess(manifest);
            if (proc is null || IsProcessExited(proc))
            {
                staleManifests.Add(manifestPath);
                if (deleteDeadManifests)
                {
                    TryDeleteManifest(manifestPath);
                    if (!string.IsNullOrWhiteSpace(manifest.SocketPath))
                    {
                        TryDeleteFileOrSocket(manifest.SocketPath);
                    }

                    TryDeleteFileOrSocket($"{manifestPath}.lock");
                }

                continue;
            }

            double? memoryMb = TryGetProcessMemoryMb(proc);
            double? idleSeconds = null;
            double? idleTimeoutSeconds = null;

            WorkspaceHostDaemonEndpoint hostEndpoint = new(
                Transport: manifest.Transport,
                PipeName: manifest.PipeName,
                SocketPath: manifest.SocketPath,
                ManifestPath: manifestPath,
                RepoRoot: manifest.RepoRoot,
                LogPath: manifest.LogPath);
            WorkspaceHostSendResult statusProbe = await SendWithDiagnosticsAsync(
                hostEndpoint,
                new WorkspaceHostRequest(
                    Id: Guid.NewGuid().ToString("N"),
                    Method: WorkspaceHostProtocol.Method.DaemonStatus),
                timeoutDuration: TimeSpan.FromMilliseconds(500),
                cancellationToken,
                connectTimeoutOverride: TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            if (statusProbe.Response?.Ok == true && statusProbe.Response.Data is not null)
            {
                TryExtractStatusMetrics(statusProbe.Response.Data, out idleSeconds, out idleTimeoutSeconds, out double? statusMemoryMb);
                memoryMb ??= statusMemoryMb;
            }

            string workspaceKey = Path.GetFileNameWithoutExtension(manifestPath);
            bool repoRootExists = !string.IsNullOrWhiteSpace(manifest.RepoRoot) && Directory.Exists(manifest.RepoRoot);
            liveHosts.Add(new ManagedHostSnapshot(
                WorkspaceKey: workspaceKey,
                ManifestPath: manifestPath,
                Manifest: manifest,
                EndpointLabel: FormatEndpoint(hostEndpoint),
                RepoRootExists: repoRootExists,
                IdleSeconds: idleSeconds,
                IdleTimeoutSeconds: idleTimeoutSeconds,
                MemoryMb: memoryMb));
        }

        return (liveHosts, staleManifests);
    }

    private static double? TryGetProcessMemoryMb(Process process)
    {
        try
        {
            process.Refresh();
            return Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 1);
        }
        catch
        {
            return null;
        }
    }

    private static void TryExtractStatusMetrics(
        object data,
        out double? idleSeconds,
        out double? idleTimeoutSeconds,
        out double? memoryMb)
    {
        idleSeconds = null;
        idleTimeoutSeconds = null;
        memoryMb = null;
        try
        {
            JsonElement element = data is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(data);
            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (element.TryGetProperty("idle_seconds", out JsonElement idleEl) && idleEl.ValueKind == JsonValueKind.Number)
            {
                idleSeconds = idleEl.GetDouble();
            }

            if (element.TryGetProperty("idle_timeout_seconds", out JsonElement timeoutEl) && timeoutEl.ValueKind == JsonValueKind.Number)
            {
                idleTimeoutSeconds = timeoutEl.GetDouble();
            }

            if (element.TryGetProperty("memory_mb", out JsonElement memEl) && memEl.ValueKind == JsonValueKind.Number)
            {
                memoryMb = memEl.GetDouble();
            }
        }
        catch
        {
        }
    }

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

            WorkspaceHostSendResult statusResult = await SendWithDiagnosticsAsync(
                endpoint,
                new WorkspaceHostRequest(
                    Id: Guid.NewGuid().ToString("N"),
                    Method: WorkspaceHostProtocol.Method.DaemonStatus),
                timeoutDuration: TimeSpan.FromSeconds(2),
                cancellationToken,
                connectTimeoutOverride: TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            if (statusResult.Response?.Ok == true)
            {
                return statusResult.Response;
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

    private static int? TryGetProcessExitCode(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
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

    private static async Task<WorkspaceHostResponse?> TryGetStatusAsync(
        WorkspaceHostDaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        WorkspaceHostSendResult result = await SendWithDiagnosticsAsync(
            endpoint,
            new WorkspaceHostRequest(
                Id: Guid.NewGuid().ToString("N"),
                Method: WorkspaceHostProtocol.Method.DaemonStatus),
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    private static async Task<WorkspaceHostSendResult> SendWithDiagnosticsAsync(
        WorkspaceHostDaemonEndpoint endpoint,
        WorkspaceHostRequest request,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken,
        TimeSpan? connectTimeoutOverride = null)
    {
        WorkspaceHostDaemonManifest? manifest = TryReadManifest(endpoint.ManifestPath);
        string endpointLabel = FormatEndpoint(endpoint);
        string logPath = ResolveLogPath(endpoint, manifest);
        using Process? managedProcess = manifest is not null ? TryGetManagedDaemonProcess(manifest) : null;

        TimeSpan defaultConnectTimeout = manifest is null
            ? MissingManifestProbeTimeout
            : managedProcess is null
                ? DeadHostProbeTimeout
                : LiveHostConnectTimeout;
        TimeSpan connectTimeout = connectTimeoutOverride ?? defaultConnectTimeout;
        if (timeoutDuration < connectTimeout)
        {
            connectTimeout = timeoutDuration;
        }

        WorkspaceHostClientOptions options = endpoint.Transport == "named-pipe"
            ? WorkspaceHostClientOptions.NamedPipe(endpoint.PipeName!)
            : WorkspaceHostClientOptions.UnixSocket(endpoint.SocketPath!);
        WorkspaceHostRequest authorizedRequest = AddAuthToken(manifest, request);

        WorkspaceHostClient client;
        try
        {
            client = await WorkspaceHostClient.ConnectAsync(
                options,
                connectTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or UnauthorizedAccessException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            WorkspaceHostFailure failure = BuildConnectFailure(manifest, managedProcess, endpointLabel, logPath, connectTimeout, ex);
            if (string.Equals(failure.Reason, "host_exited", StringComparison.Ordinal) && manifest is not null)
            {
                TryCleanupDeadManifest(endpoint, manifest.ProcessId);
            }

            return new WorkspaceHostSendResult(Response: null, Failure: failure);
        }

        await using (client)
        {
            using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeoutDuration);
            try
            {
                WorkspaceHostResponse response = await SendWhileMonitoringHostAsync(
                    client,
                    authorizedRequest,
                    managedProcess,
                    requestTimeout.Token).ConfigureAwait(false);
                WorkspaceHostResponse checkedResponse = IsProtocolCompatible(response.ProtocolVersion)
                    ? response
                    : ProtocolMismatchResponse(request, response.ProtocolVersion);
                return new WorkspaceHostSendResult(checkedResponse, Failure: null);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or SocketException or UnauthorizedAccessException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                WorkspaceHostFailure failure = BuildRequestFailure(manifest, managedProcess, endpointLabel, logPath, timeoutDuration, ex);
                if (string.Equals(failure.Reason, "host_exited", StringComparison.Ordinal) && manifest is not null)
                {
                    TryCleanupDeadManifest(endpoint, manifest.ProcessId);
                }

                return new WorkspaceHostSendResult(Response: null, Failure: failure);
            }
        }
    }

    private static void TryCleanupDeadManifest(WorkspaceHostDaemonEndpoint endpoint, int deadPid)
    {
        WorkspaceHostDaemonManifest? current = TryReadManifest(endpoint.ManifestPath);
        if (current is not null && current.ProcessId == deadPid)
        {
            TryDeleteManifest(endpoint.ManifestPath);
            if (!string.IsNullOrWhiteSpace(endpoint.SocketPath))
            {
                TryDeleteFileOrSocket(endpoint.SocketPath);
            }
        }
    }

    private static async Task<WorkspaceHostResponse> SendWhileMonitoringHostAsync(
        WorkspaceHostClient client,
        WorkspaceHostRequest request,
        Process? managedProcess,
        CancellationToken cancellationToken)
    {
        if (managedProcess is null)
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<WorkspaceHostResponse> sendTask = client.SendAsync(request, sendCts.Token);
        Task exitTask = WaitForProcessExitIndefinitelyAsync(managedProcess, sendCts.Token);

        Task completed = await Task.WhenAny(sendTask, exitTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, sendTask))
        {
            sendCts.Cancel();
            return await sendTask.ConfigureAwait(false);
        }

        // The host exited while the request was in flight. Give the pipe read a brief grace window to drain
        // any final response bytes (for example on daemon/shutdown) before cancelling it.
        Task completedAfterExit = await Task.WhenAny(sendTask, Task.Delay(150, cancellationToken)).ConfigureAwait(false);
        if (ReferenceEquals(completedAfterExit, sendTask) && sendTask.IsCompletedSuccessfully)
        {
            return await sendTask.ConfigureAwait(false);
        }

        sendCts.Cancel();
        try
        {
            return await sendTask.ConfigureAwait(false);
        }
        catch (Exception) when (IsProcessExited(managedProcess))
        {
            throw new IOException($"Workspace host process {managedProcess.Id} exited while handling request '{request.Method}'.");
        }
    }

    private static async Task WaitForProcessExitIndefinitelyAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Fall back to never completing if we cannot wait on the handle; normal send timeout/IO handles it.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }

    private static WorkspaceHostFailure BuildConnectFailure(
        WorkspaceHostDaemonManifest? manifest,
        Process? managedProcess,
        string endpointLabel,
        string logPath,
        TimeSpan connectTimeout,
        Exception ex)
    {
        int? exitCode = TryGetProcessExitCode(managedProcess);
        bool hostExited = manifest is not null && (managedProcess is null || IsProcessExited(managedProcess));
        string? lastLogLine = TryReadLastLogLine(logPath);
        string detailSuffix = BuildFailureDetailSuffix(exitCode, lastLogLine);
        string? daemonLifetime = manifest?.DaemonLifetime;
        string? launchMode = manifest?.LaunchMode;
        string? callerJobFlags = manifest?.CallerJobFlags;

        if (hostExited)
        {
            string jobFlagsPart = string.IsNullOrWhiteSpace(callerJobFlags) ? string.Empty : $" (job_flags={callerJobFlags})";
            string jobNote = string.Equals(daemonLifetime, LifetimeCallerJob, StringComparison.OrdinalIgnoreCase)
                ? $"; started with daemon_lifetime=caller_job inside a Windows KILL_ON_JOB_CLOSE job object without BREAKAWAY_OK{jobFlagsPart} so it terminates when the launching shell exits — run 'roscli daemon.run' in a long-lived runner such as a background job to keep it alive across shells"
                : string.Empty;
            string message = $"Workspace host process {manifest!.ProcessId} for endpoint '{endpointLabel}' is no longer running (exited{jobNote}){detailSuffix}.";
            return new WorkspaceHostFailure(
                Reason: "host_exited",
                Message: message,
                DaemonPid: manifest.ProcessId,
                DaemonEndpoint: endpointLabel,
                ExitCode: exitCode,
                LogPath: logPath,
                LastLogLine: lastLogLine,
                DaemonLifetime: daemonLifetime,
                LaunchMode: launchMode,
                CallerJobFlags: callerJobFlags);
        }

        if (manifest is null)
        {
            string message = $"No workspace host is running for endpoint '{endpointLabel}'. Start it with 'roscli workspace.use <solution-or-csproj>', 'roscli daemon.start', or 'roscli daemon.run'{detailSuffix}.";
            return new WorkspaceHostFailure(
                Reason: "no_host",
                Message: message,
                DaemonPid: null,
                DaemonEndpoint: endpointLabel,
                ExitCode: null,
                LogPath: logPath,
                LastLogLine: lastLogLine,
                DaemonLifetime: null);
        }

        if (ex is TimeoutException)
        {
            string message = $"Workspace host process {manifest.ProcessId} on endpoint '{endpointLabel}' did not accept a connection within {(int)Math.Ceiling(connectTimeout.TotalMilliseconds)} ms{detailSuffix}.";
            return new WorkspaceHostFailure(
                Reason: "connect_timeout",
                Message: message,
                DaemonPid: manifest.ProcessId,
                DaemonEndpoint: endpointLabel,
                ExitCode: exitCode,
                LogPath: logPath,
                LastLogLine: lastLogLine,
                DaemonLifetime: daemonLifetime,
                LaunchMode: launchMode,
                CallerJobFlags: callerJobFlags);
        }

        return new WorkspaceHostFailure(
            Reason: "connect_failed",
            Message: $"Failed to connect to workspace host process {manifest.ProcessId} on endpoint '{endpointLabel}': {ex.Message}{detailSuffix}.",
            DaemonPid: manifest.ProcessId,
            DaemonEndpoint: endpointLabel,
            ExitCode: exitCode,
            LogPath: logPath,
            LastLogLine: lastLogLine,
            DaemonLifetime: daemonLifetime,
            LaunchMode: launchMode,
            CallerJobFlags: callerJobFlags);
    }

    private static WorkspaceHostFailure BuildRequestFailure(
        WorkspaceHostDaemonManifest? manifest,
        Process? managedProcess,
        string endpointLabel,
        string logPath,
        TimeSpan timeoutDuration,
        Exception ex)
    {
        int? exitCode = TryGetProcessExitCode(managedProcess);
        bool hostExited = manifest is not null && (managedProcess is null || IsProcessExited(managedProcess));
        string? lastLogLine = TryReadLastLogLine(logPath);
        string detailSuffix = BuildFailureDetailSuffix(exitCode, lastLogLine);
        string? daemonLifetime = manifest?.DaemonLifetime;
        string? launchMode = manifest?.LaunchMode;
        string? callerJobFlags = manifest?.CallerJobFlags;

        if (hostExited)
        {
            string jobFlagsPart = string.IsNullOrWhiteSpace(callerJobFlags) ? string.Empty : $", job_flags={callerJobFlags}";
            string jobNote = string.Equals(daemonLifetime, LifetimeCallerJob, StringComparison.OrdinalIgnoreCase)
                ? $" (started with daemon_lifetime=caller_job inside a Windows KILL_ON_JOB_CLOSE job object without BREAKAWAY_OK{jobFlagsPart})"
                : string.Empty;
            return new WorkspaceHostFailure(
                Reason: "host_exited",
                Message: $"Workspace host process {manifest!.ProcessId} exited while handling request on endpoint '{endpointLabel}'{jobNote}{detailSuffix}.",
                DaemonPid: manifest.ProcessId,
                DaemonEndpoint: endpointLabel,
                ExitCode: exitCode,
                LogPath: logPath,
                LastLogLine: lastLogLine,
                DaemonLifetime: daemonLifetime,
                LaunchMode: launchMode,
                CallerJobFlags: callerJobFlags);
        }

        if (ex is OperationCanceledException or TimeoutException)
        {
            return new WorkspaceHostFailure(
                Reason: "request_timeout",
                Message: $"Workspace host process {manifest?.ProcessId} on endpoint '{endpointLabel}' did not respond within {(int)Math.Ceiling(timeoutDuration.TotalSeconds)} s{detailSuffix}.",
                DaemonPid: manifest?.ProcessId,
                DaemonEndpoint: endpointLabel,
                ExitCode: exitCode,
                LogPath: logPath,
                LastLogLine: lastLogLine,
                DaemonLifetime: daemonLifetime,
                LaunchMode: launchMode,
                CallerJobFlags: callerJobFlags);
        }

        return new WorkspaceHostFailure(
            Reason: "connection_closed",
            Message: $"Connection to workspace host on endpoint '{endpointLabel}' failed: {ex.Message}{detailSuffix}.",
            DaemonPid: manifest?.ProcessId,
            DaemonEndpoint: endpointLabel,
            ExitCode: exitCode,
            LogPath: logPath,
            LastLogLine: lastLogLine,
            DaemonLifetime: daemonLifetime,
            LaunchMode: launchMode,
            CallerJobFlags: callerJobFlags);
    }

    private static string ResolveLogPath(WorkspaceHostDaemonEndpoint endpoint, WorkspaceHostDaemonManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.LogPath))
        {
            return manifest.LogPath;
        }

        if (!string.IsNullOrWhiteSpace(endpoint.LogPath))
        {
            return endpoint.LogPath;
        }

        return RepositoryRootDiscovery.GetWorkspaceDaemonLogPath(endpoint.RepoRoot);
    }

    private static string? TryReadLastLogLine(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxTailBytes = 8192;
            if (stream.Length > maxTailBytes)
            {
                stream.Seek(-maxTailBytes, SeekOrigin.End);
            }

            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? lastNonEmpty = null;
            while (reader.ReadLine() is string line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lastNonEmpty = line.Trim();
                }
            }

            return lastNonEmpty;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildFailureDetailSuffix(int? exitCode, string? lastLogLine)
    {
        List<string> parts = new();
        if (exitCode.HasValue)
        {
            parts.Add($"exit_code={exitCode.Value}");
        }

        if (!string.IsNullOrWhiteSpace(lastLogLine))
        {
            string trimmed = lastLogLine.Length <= 240 ? lastLogLine : lastLogLine[..240] + "...";
            parts.Add($"last_log='{trimmed}'");
        }

        return parts.Count == 0 ? string.Empty : $" ({string.Join("; ", parts)})";
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

    private static WorkspaceHostRequest AddAuthToken(WorkspaceHostDaemonManifest? manifest, WorkspaceHostRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AuthToken))
        {
            return request;
        }

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

    private static CommandEnvelope ErrorEnvelope(string commandId, string code, string message, object? details = null)
        => new(
            Ok: false,
            CommandId: commandId,
            Version: "1.0",
            Data: null,
            Errors: new[] { new CommandError(code, message, details) },
            TraceId: null);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoW lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr hProcess, IntPtr hJob, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed record WorkspaceHostDaemonManifest(
        string ManifestVersion,
        string Transport,
        string? PipeName,
        string? SocketPath,
        string RepoRoot,
        string HostPath,
        string? AuthToken,
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        string? LogPath = null,
        string? LaunchMode = null,
        string? DaemonLifetime = null,
        string? CallerJobFlags = null,
        string? LaunchWarning = null);

    private sealed record LaunchedDaemonProcess(
        Process Process,
        string LaunchMode,
        string DaemonLifetime,
        string? CallerJobFlags,
        string? LaunchWarning);

    private readonly record struct CallerJobInfo(
        bool InJob,
        uint? LimitFlags,
        string? FormattedFlags,
        bool KillOnJobClose,
        bool BreakawayOk);

    private sealed record ManagedHostSnapshot(
        string WorkspaceKey,
        string ManifestPath,
        WorkspaceHostDaemonManifest Manifest,
        string EndpointLabel,
        bool RepoRootExists,
        double? IdleSeconds,
        double? IdleTimeoutSeconds,
        double? MemoryMb)
    {
        public object ToPublicObject() => new
        {
            workspace_key = WorkspaceKey,
            repo_root = Manifest.RepoRoot,
            repo_root_exists = RepoRootExists,
            daemon_pid = Manifest.ProcessId,
            launch_mode = Manifest.LaunchMode,
            daemon_lifetime = Manifest.DaemonLifetime ?? LifetimeDetached,
            caller_job_flags = Manifest.CallerJobFlags,
            daemon_endpoint = EndpointLabel,
            idle_seconds = IdleSeconds,
            idle_timeout_seconds = IdleTimeoutSeconds,
            memory_mb = MemoryMb,
            started_utc = Manifest.StartedAtUtc,
            manifest_path = ManifestPath,
            log_path = Manifest.LogPath,
        };
    }

    private readonly record struct ManagedProcessStopResult(
        bool StoppedProcess,
        bool ForcedKill,
        bool StillAlive);
}
