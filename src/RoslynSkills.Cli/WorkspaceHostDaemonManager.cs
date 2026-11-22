using System.Diagnostics;
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

public sealed record WorkspaceHostFailure(
    string Reason,
    string Message,
    int? DaemonPid,
    string DaemonEndpoint,
    int? ExitCode,
    string? LogPath,
    string? LastLogLine)
{
    public object ToDetails()
        => new
        {
            reason = Reason,
            daemon_pid = DaemonPid,
            daemon_endpoint = DaemonEndpoint,
            exit_code = ExitCode,
            log_path = LogPath,
            last_log_line = LastLogLine,
        };
}

public sealed record WorkspaceHostSendResult(
    WorkspaceHostResponse? Response,
    WorkspaceHostFailure? Failure)
{
    public bool Succeeded => Response is not null;
}

public sealed class WorkspaceHostDaemonManager
{
    private const string ManifestVersion = "1.0";
    private const int MaxUnixSocketPathUtf8Bytes = 100;
    private static readonly TimeSpan LiveHostConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeadHostProbeTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MissingManifestProbeTimeout = TimeSpan.FromMilliseconds(500);
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

    public async Task<CommandEnvelope> StartAsync(
        string? repoRoot,
        string? hostPath,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = GetDefaultEndpoint(repoRoot);
        WorkspaceHostResponse? existing = await TryGetStatusAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (existing?.Ok == true)
        {
            WorkspaceHostDaemonManifest? existingManifest = TryReadManifest(endpoint.ManifestPath);
            return SuccessEnvelope(
                "daemon.start",
                new
                {
                    started = false,
                    already_running = true,
                    daemon_endpoint = FormatEndpoint(endpoint),
                    daemon_pid = existingManifest?.ProcessId,
                    manifest_path = endpoint.ManifestPath,
                    log_path = existingManifest?.LogPath ?? ResolveLogPath(endpoint, existingManifest),
                    launch_mode = existingManifest?.LaunchMode,
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
        string logPath = ResolveLogPath(endpoint, manifest: null);

        (Process process, string launchMode) = StartDaemonProcess(endpoint, resolvedHostPath, authToken, logPath);
        using (process)
        {
            WorkspaceHostDaemonManifest manifest = new(
                ManifestVersion,
                endpoint.Transport,
                endpoint.PipeName,
                endpoint.SocketPath,
                endpoint.RepoRoot,
                resolvedHostPath,
                authToken,
                process.Id,
                DateTimeOffset.UtcNow,
                LogPath: logPath,
                LaunchMode: launchMode);
            WriteManifest(endpoint.ManifestPath, manifest);

            WorkspaceHostResponse? status = await WaitForStatusAsync(endpoint, process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (status?.Ok != true)
            {
                int? exitCode = TryGetProcessExitCode(process);
                bool exitedBeforeReady = IsProcessExited(process);
                TryKillProcessTree(process);
                await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                exitCode ??= TryGetProcessExitCode(process);
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
                    Message: $"Started workspace host process {process.Id} ({launchMode}), but it did not answer daemon/status on endpoint '{FormatEndpoint(endpoint)}'{detailSuffix}.",
                    DaemonPid: process.Id,
                    DaemonEndpoint: FormatEndpoint(endpoint),
                    ExitCode: exitCode,
                    LogPath: logPath,
                    LastLogLine: lastLogLine);
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
                    daemon_endpoint = FormatEndpoint(endpoint),
                    daemon_pid = process.Id,
                    manifest_path = endpoint.ManifestPath,
                    log_path = logPath,
                    launch_mode = launchMode,
                    host_path = resolvedHostPath,
                    status = status.Data,
                });
        }
    }

    private static (Process Process, string LaunchMode) StartDaemonProcess(
        WorkspaceHostDaemonEndpoint endpoint,
        string resolvedHostPath,
        string authToken,
        string logPath)
    {
        string dotnetExecutable = ResolveDotnetExecutable();
        List<string> daemonArguments = BuildDaemonArguments(resolvedHostPath, endpoint, authToken, logPath);
        if (OperatingSystem.IsWindows())
        {
            return StartWindowsDetachedDaemon(dotnetExecutable, daemonArguments, endpoint.RepoRoot);
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
        foreach (string arg in daemonArguments)
        {
            unixStartInfo.ArgumentList.Add(arg);
        }

        Process unixProcess = Process.Start(unixStartInfo)
            ?? throw new InvalidOperationException("Failed to start RoslynSkills workspace host process.");
        return (unixProcess, "unix_sh_exec");
    }

    [SupportedOSPlatform("windows")]
    private static (Process Process, string LaunchMode) StartWindowsDetachedDaemon(
        string dotnetExecutable,
        IReadOnlyList<string> daemonArguments,
        string workingDirectory)
    {
        string commandLine = BuildWindowsCommandLine(dotnetExecutable, daemonArguments);

        // 1. Prefer Win32 CreateProcessW with CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW | DETACHED_PROCESS
        // and bInheritHandles = false. This inherits the full current environment block (e.g. test overrides),
        // avoids handle leaks, and escapes any parent job object that permits breakaway.
        if (TryStartWindowsProcessWithBreakaway(commandLine, workingDirectory, out Process? breakawayProcess) &&
            breakawayProcess is not null)
        {
            return (breakawayProcess, "create_process_breakaway");
        }

        // 2. When the parent shell runs inside a Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        // and without JOB_OBJECT_LIMIT_BREAKAWAY_OK (common in agent harnesses and CI runners), CreateProcessW
        // with CREATE_BREAKAWAY_FROM_JOB fails with ERROR_ACCESS_DENIED. Spawning through WMI Win32_Process.Create
        // launches the daemon under WmiPrvSE outside the caller's job object so it survives shell exit.
        if (TryStartWindowsProcessViaWmi(commandLine, workingDirectory, out Process? wmiProcess) &&
            wmiProcess is not null)
        {
            return (wmiProcess, "wmi_win32_process");
        }

        // 3. Final fallback if both Win32 breakaway and WMI are unavailable in a restricted environment.
        ProcessStartInfo fallbackStartInfo = new(dotnetExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string arg in daemonArguments)
        {
            fallbackStartInfo.ArgumentList.Add(arg);
        }

        Process fallbackProcess = Process.Start(fallbackStartInfo)
            ?? throw new InvalidOperationException("Failed to start RoslynSkills workspace host process.");
        return (fallbackProcess, "shell_execute_fallback");
    }

    private static List<string> BuildDaemonArguments(
        string resolvedHostPath,
        WorkspaceHostDaemonEndpoint endpoint,
        string authToken,
        string logPath)
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

    private static bool TryStartWindowsProcessWithBreakaway(
        string commandLine,
        string workingDirectory,
        out Process? process)
    {
        process = null;
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

        StringBuilder mutableCommandLine = new(commandLine);
        bool created = CreateProcessW(
            lpApplicationName: null,
            lpCommandLine: mutableCommandLine,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: CreateBreakawayFromJob | CreateNoWindow | DetachedProcess | CreateUnicodeEnvironment,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: workingDirectory,
            ref startupInfo,
            out ProcessInformation processInfo);
        if (!created)
        {
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
            int returnValue = returnValueObj is not null ? Convert.ToInt32(returnValueObj) : -1;
            int processId = processIdObj is not null ? Convert.ToInt32(processIdObj) : 0;
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
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
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
                LastLogLine: TryReadLastLogLine(ResolveLogPath(endpoint, manifest)));
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
            return new WorkspaceHostSendResult(
                Response: null,
                Failure: BuildConnectFailure(manifest, managedProcess, endpointLabel, logPath, connectTimeout, ex));
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
                return new WorkspaceHostSendResult(
                    Response: null,
                    Failure: BuildRequestFailure(manifest, managedProcess, endpointLabel, logPath, timeoutDuration, ex));
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

        if (hostExited)
        {
            string message = $"Workspace host process {manifest!.ProcessId} for endpoint '{endpointLabel}' is no longer running (exited){detailSuffix}.";
            return new WorkspaceHostFailure(
                Reason: "host_exited",
                Message: message,
                DaemonPid: manifest.ProcessId,
                DaemonEndpoint: endpointLabel,
                ExitCode: exitCode,
                LogPath: logPath,
                LastLogLine: lastLogLine);
        }

        if (manifest is null)
        {
            string message = $"No workspace host is running for endpoint '{endpointLabel}'. Start it with 'roscli workspace.use <solution-or-csproj>' or 'roscli daemon.start'{detailSuffix}.";
            return new WorkspaceHostFailure(
                Reason: "no_host",
                Message: message,
                DaemonPid: null,
                DaemonEndpoint: endpointLabel,
                ExitCode: null,
                LogPath: logPath,
                LastLogLine: lastLogLine);
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
                LastLogLine: lastLogLine);
        }

        return new WorkspaceHostFailure(
            Reason: "connect_failed",
            Message: $"Failed to connect to workspace host process {manifest.ProcessId} on endpoint '{endpointLabel}': {ex.Message}{detailSuffix}.",
            DaemonPid: manifest.ProcessId,
            DaemonEndpoint: endpointLabel,
            ExitCode: exitCode,
            LogPath: logPath,
            LastLogLine: lastLogLine);
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

        if (hostExited)
        {
            return new WorkspaceHostFailure(
                Reason: "host_exited",
                Message: $"Workspace host process {manifest!.ProcessId} exited while handling request on endpoint '{endpointLabel}'{detailSuffix}.",
                DaemonPid: manifest.ProcessId,
                DaemonEndpoint: endpointLabel,
                ExitCode: exitCode,
                LogPath: logPath,
                LastLogLine: lastLogLine);
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
                LastLogLine: lastLogLine);
        }

        return new WorkspaceHostFailure(
            Reason: "connection_closed",
            Message: $"Connection to workspace host on endpoint '{endpointLabel}' failed: {ex.Message}{detailSuffix}.",
            DaemonPid: manifest?.ProcessId,
            DaemonEndpoint: endpointLabel,
            ExitCode: exitCode,
            LogPath: logPath,
            LastLogLine: lastLogLine);
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
        string? LaunchMode = null);

    private readonly record struct ManagedProcessStopResult(
        bool StoppedProcess,
        bool ForcedKill,
        bool StillAlive);
}
