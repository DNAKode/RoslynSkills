#nullable enable
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.WorkspaceHost;

internal static class Program
{
    private const string EnvelopeVersion = "1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static string CurrentTransportName = "stdio-jsonl";
    private static string? ExpectedAuthToken;
    private static string? LogFilePath;
    private static readonly object LogGate = new();
    private const long MaxLogFileBytes = 256 * 1024;
    private static int ActiveRequestCount;
    private static int NonStatusActiveRequestCount;
    private static long LastActivityTicksUtc = DateTimeOffset.UtcNow.UtcTicks;
    private static TimeSpan? ConfiguredIdleTimeout;
    private static string? ConfiguredManifestPath;
    private static readonly Dictionary<string, string> WorkspaceAliases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AliasGate = new();

    private static readonly IReadOnlyDictionary<string, string> WorkspaceMethodCommandMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkspaceHostProtocol.Method.WorkspacePreload] = "workspace.preload",
            [WorkspaceHostProtocol.Method.WorkspaceStatus] = "workspace.status",
            [WorkspaceHostProtocol.Method.WorkspaceRefresh] = "workspace.refresh",
            [WorkspaceHostProtocol.Method.WorkspaceClose] = "workspace.close",
            [WorkspaceHostProtocol.Method.WorkspaceList] = "workspace.list",
        };

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseOptions(args, out WorkspaceHostOptions? options, out string? error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            return 2;
        }

        InitializeLog(options.LogPath);
        RegisterLifecycleLogging();
        ConfiguredIdleTimeout = options.IdleTimeout;
        ConfiguredManifestPath = options.ManifestPath;
        Interlocked.Exchange(ref LastActivityTicksUtc, DateTimeOffset.UtcNow.UtcTicks);

        string endpointLabel = options.Transport switch
        {
            "named-pipe" => $"pipe:{options.PipeName}",
            "unix-socket" => $"unix:{options.SocketPath}",
            _ => "stdio",
        };
        string idleLabel = options.IdleTimeout is { } idle ? $"{idle.TotalSeconds:F0}s" : "disabled";
        if (!string.IsNullOrWhiteSpace(options.LaunchWarning))
        {
            Log($"launch_warning {SanitizeLogText(options.LaunchWarning)}");
        }

        Log($"host_started pid={Environment.ProcessId} transport={options.Transport} endpoint={endpointLabel} idle_timeout={idleLabel} version={GetAssemblyVersion()} cwd={Environment.CurrentDirectory}");

        ICommandRegistry registry = DefaultRegistryFactory.Create();
        ExpectedAuthToken = string.IsNullOrWhiteSpace(options.AuthToken) ? null : options.AuthToken;

        int exitCode;
        try
        {
            exitCode = options.Transport switch
            {
                "stdio" => await RunStdioAsync(registry).ConfigureAwait(false),
                "named-pipe" => await RunNamedPipeAsync(registry, options.PipeName!, options.ExitOnStdinEof).ConfigureAwait(false),
                "unix-socket" => await RunUnixSocketAsync(registry, options.SocketPath!, options.ExitOnStdinEof).ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (Exception ex)
        {
            Log($"host_fatal_exception type={ex.GetType().FullName} message={SanitizeLogText(ex.Message)}");
            throw;
        }
        finally
        {
            TryCleanupOwnManifestOnExit();
        }

        Log($"host_exiting pid={Environment.ProcessId} exit_code={exitCode}");
        return exitCode;
    }

    private static async Task<int> RunStdioAsync(ICommandRegistry registry)
    {
        CurrentTransportName = "stdio-jsonl";
        using CancellationTokenSource shutdownCts = new();
        _ = await RunJsonLinesAsync(registry, Console.In, Console.Out, shutdownCts, singleRequest: false).ConfigureAwait(false);
        return 0;
    }

    private static async Task<bool> RunJsonLinesAsync(
        ICommandRegistry registry,
        TextReader input,
        TextWriter output,
        CancellationTokenSource shutdownCts,
        bool singleRequest = false)
    {
        while (!shutdownCts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            bool shouldExit = await HandleRequestLineAsync(registry, output, line, shutdownCts.Token).ConfigureAwait(false);
            if (shouldExit)
            {
                return true;
            }

            if (singleRequest)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task<int> RunNamedPipeAsync(ICommandRegistry registry, string pipeName, bool exitOnStdinEof)
    {
        CurrentTransportName = "named-pipe-jsonl";
        await using NamedPipeServerStream? instanceLock = await TryAcquireNamedPipeLockAsync(pipeName).ConfigureAwait(false);
        if (instanceLock is null)
        {
            string conflict = $"Another workspace host already owns pipe '{pipeName}'.";
            Log($"pipe_lock_conflict pipe={pipeName} exit_code=3");
            await Console.Error.WriteLineAsync(conflict).ConfigureAwait(false);
            return 3;
        }

        using CancellationTokenSource shutdownCts = new();
        if (exitOnStdinEof)
        {
            StartStdinEofMonitor(shutdownCts);
        }

        StartIdleTimeoutMonitor(shutdownCts);

        TaskCompletionSource shutdownResponseSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        while (!shutdownCts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (shutdownCts.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(50, shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            _ = HandleNamedPipeConnectionAsync(registry, pipe, shutdownCts, shutdownResponseSent);
        }

        await WaitForShutdownFlushAsync(shutdownResponseSent).ConfigureAwait(false);
        return 0;
    }

    private static async Task<NamedPipeServerStream?> TryAcquireNamedPipeLockAsync(string pipeName)
    {
        string lockPipeName = $"roslynskills-lock-{pipeName}";
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new NamedPipeServerStream(
                    lockPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(2))
            {
                return null;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static async Task HandleNamedPipeConnectionAsync(
        ICommandRegistry registry,
        NamedPipeServerStream pipe,
        CancellationTokenSource shutdownCts,
        TaskCompletionSource shutdownResponseSent)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                using StreamReader reader = new(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using StreamWriter writer = new(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                bool shouldExit = await RunJsonLinesAsync(
                    registry,
                    reader,
                    writer,
                    shutdownCts,
                    singleRequest: true).ConfigureAwait(false);
                if (shouldExit)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
                            pipe.WaitForPipeDrain();
                        }
                        catch
                        {
                        }
                    }

                    shutdownResponseSent.TrySetResult();
                    try
                    {
                        shutdownCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static void StartStdinEofMonitor(CancellationTokenSource shutdownCts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using Stream stdin = Console.OpenStandardInput();
                byte[] buffer = new byte[64];
                while (!shutdownCts.IsCancellationRequested)
                {
                    int read = await stdin.ReadAsync(buffer, shutdownCts.Token).ConfigureAwait(false);
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

            if (!shutdownCts.IsCancellationRequested)
            {
                Log("stdin_eof_shutdown");
                try
                {
                    shutdownCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        });
    }

    private static void StartIdleTimeoutMonitor(CancellationTokenSource shutdownCts)
    {
        TimeSpan? timeout = ConfiguredIdleTimeout;
        if (timeout is null || timeout.Value <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan idleLimit = timeout.Value;
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(idleLimit.TotalMilliseconds / 4.0, 100.0, 15_000.0));

        _ = Task.Run(async () =>
        {
            try
            {
                while (!shutdownCts.IsCancellationRequested)
                {
                    await Task.Delay(pollInterval, shutdownCts.Token).ConfigureAwait(false);
                    if (Volatile.Read(ref NonStatusActiveRequestCount) > 0)
                    {
                        continue;
                    }

                    DateTimeOffset lastActivity = new(Interlocked.Read(ref LastActivityTicksUtc), TimeSpan.Zero);
                    TimeSpan idleElapsed = DateTimeOffset.UtcNow - lastActivity;
                    if (idleElapsed >= idleLimit)
                    {
                        Log($"host_idle_timeout pid={Environment.ProcessId} idle_seconds={idleElapsed.TotalSeconds:F1} timeout_seconds={idleLimit.TotalSeconds:F1} action=shutdown");
                        try
                        {
                            shutdownCts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }

                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private static void TryCleanupOwnManifestOnExit()
    {
        string? manifestPath = ConfiguredManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("ProcessId", out JsonElement pidElement) &&
                pidElement.ValueKind == JsonValueKind.Number &&
                pidElement.TryGetInt32(out int manifestPid) &&
                manifestPid == Environment.ProcessId)
            {
                File.Delete(manifestPath);
            }
        }
        catch
        {
        }
    }

    private static async Task<int> RunUnixSocketAsync(ICommandRegistry registry, string socketPath, bool exitOnStdinEof)
    {
        CurrentTransportName = "unix-socket-jsonl";
        string? socketDirectory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrWhiteSpace(socketDirectory))
        {
            Directory.CreateDirectory(socketDirectory);
        }

        if (Path.Exists(socketPath) && !IsUnixSocketListening(socketPath))
        {
            TryDeleteSocketPath(socketPath);
        }

        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bool bound = false;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            bound = true;
            listener.Listen(backlog: 32);

            using CancellationTokenSource shutdownCts = new();
            if (exitOnStdinEof)
            {
                StartStdinEofMonitor(shutdownCts);
            }

            StartIdleTimeoutMonitor(shutdownCts);

            TaskCompletionSource shutdownResponseSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

            while (!shutdownCts.IsCancellationRequested)
            {
                Socket connection;
                try
                {
                    connection = await listener.AcceptAsync(shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (shutdownCts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = HandleUnixSocketConnectionAsync(registry, connection, shutdownCts, shutdownResponseSent);
            }

            await WaitForShutdownFlushAsync(shutdownResponseSent).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (bound)
            {
                TryDeleteSocketPath(socketPath);
            }
        }
    }

    private static async Task HandleUnixSocketConnectionAsync(
        ICommandRegistry registry,
        Socket connection,
        CancellationTokenSource shutdownCts,
        TaskCompletionSource shutdownResponseSent)
    {
        try
        {
            using (connection)
            await using (NetworkStream stream = new(connection, ownsSocket: false))
            {
                using StreamReader reader = new(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using StreamWriter writer = new(stream, Utf8NoBom, bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                bool shouldExit = await RunJsonLinesAsync(
                    registry,
                    reader,
                    writer,
                    shutdownCts,
                    singleRequest: true).ConfigureAwait(false);
                if (shouldExit)
                {
                    try
                    {
                        connection.Shutdown(SocketShutdown.Send);
                    }
                    catch
                    {
                    }

                    shutdownResponseSent.TrySetResult();
                    try
                    {
                        shutdownCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static async Task WaitForShutdownFlushAsync(TaskCompletionSource shutdownResponseSent)
    {
        try
        {
            await shutdownResponseSent.Task.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static bool IsUnixSocketListening(string socketPath)
    {
        try
        {
            using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryParseOptions(
        string[] args,
        out WorkspaceHostOptions options,
        out string? error)
    {
        string transport = "stdio";
        string? pipeName = null;
        string? socketPath = null;
        string? authToken = Environment.GetEnvironmentVariable("ROSLYNSKILLS_WORKSPACE_HOST_TOKEN");
        string? logPath = Environment.GetEnvironmentVariable("ROSLYNSKILLS_WORKSPACE_HOST_LOG");
        string? manifestPath = null;
        string? launchWarning = null;
        bool exitOnStdinEof = false;
        TimeSpan? idleTimeout = ResolveDefaultIdleTimeout();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? value = null;
            int equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex >= 0)
            {
                value = arg[(equalsIndex + 1)..];
                arg = arg[..equalsIndex];
            }

            switch (arg)
            {
                case "--transport":
                    if (!TryReadOptionValue(args, ref i, value, out transport, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    transport = NormalizeTransport(transport);
                    break;

                case "--pipe-name":
                    if (!TryReadOptionValue(args, ref i, value, out pipeName, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    break;

                case "--socket-path":
                    if (!TryReadOptionValue(args, ref i, value, out socketPath, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    break;

                case "--auth-token":
                    if (!TryReadOptionValue(args, ref i, value, out authToken, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    break;

                case "--log-path":
                    if (!TryReadOptionValue(args, ref i, value, out logPath, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    break;

                case "--manifest-path":
                    if (!TryReadOptionValue(args, ref i, value, out manifestPath, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    break;

                case "--launch-warning":
                    if (!TryReadOptionValue(args, ref i, value, out launchWarning, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    break;

                case "--idle-timeout-minutes":
                    if (!TryReadOptionValue(args, ref i, value, out string minutesRaw, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    if (!double.TryParse(minutesRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minutes))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        error = $"Invalid numeric value '{minutesRaw}' for --idle-timeout-minutes.";
                        return false;
                    }

                    idleTimeout = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
                    break;

                case "--idle-timeout-seconds":
                    if (!TryReadOptionValue(args, ref i, value, out string secondsRaw, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        return false;
                    }

                    if (!double.TryParse(secondsRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null, null, null);
                        error = $"Invalid numeric value '{secondsRaw}' for --idle-timeout-seconds.";
                        return false;
                    }

                    idleTimeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
                    break;

                case "--exit-on-stdin-eof":
                    if (value is not null)
                    {
                        if (!bool.TryParse(value, out exitOnStdinEof))
                        {
                            options = new WorkspaceHostOptions("stdio", null, null, null, null);
                            error = $"Invalid boolean value '{value}' for --exit-on-stdin-eof.";
                            return false;
                        }
                    }
                    else if (i + 1 < args.Length && bool.TryParse(args[i + 1], out bool parsedBool))
                    {
                        exitOnStdinEof = parsedBool;
                        i++;
                    }
                    else
                    {
                        exitOnStdinEof = true;
                    }

                    break;

                default:
                    options = new WorkspaceHostOptions("stdio", null, null, null, null);
                    error = $"Unknown workspace host option '{arg}'. Supported options: --transport, --pipe-name, --socket-path, --auth-token, --log-path, --manifest-path, --launch-warning, --idle-timeout-minutes, --idle-timeout-seconds, --exit-on-stdin-eof.";
                    return false;
            }
        }

        if (transport == "named-pipe" && string.IsNullOrWhiteSpace(pipeName))
        {
            options = new WorkspaceHostOptions("stdio", null, null, null, null);
            error = "--transport named-pipe requires --pipe-name.";
            return false;
        }

        if (transport == "unix-socket" && string.IsNullOrWhiteSpace(socketPath))
        {
            options = new WorkspaceHostOptions("stdio", null, null, null, null);
            error = "--transport unix-socket requires --socket-path.";
            return false;
        }

        if (transport is not ("stdio" or "named-pipe" or "unix-socket"))
        {
            options = new WorkspaceHostOptions("stdio", null, null, null, null);
            error = $"Unsupported transport '{transport}'. Supported transports: stdio, named-pipe, unix-socket.";
            return false;
        }

        options = new WorkspaceHostOptions(
            transport,
            pipeName,
            socketPath,
            authToken,
            logPath,
            exitOnStdinEof,
            idleTimeout,
            manifestPath,
            launchWarning);
        error = null;
        return true;
    }

    private static TimeSpan? ResolveDefaultIdleTimeout()
    {
        string? secondsEnv = Environment.GetEnvironmentVariable("ROSLYNSKILLS_DAEMON_IDLE_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(secondsEnv) &&
            double.TryParse(secondsEnv.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double envSeconds))
        {
            return envSeconds > 0 ? TimeSpan.FromSeconds(envSeconds) : null;
        }

        string? minutesEnv = Environment.GetEnvironmentVariable("ROSLYNSKILLS_DAEMON_IDLE_TIMEOUT_MINUTES");
        if (!string.IsNullOrWhiteSpace(minutesEnv) &&
            double.TryParse(minutesEnv.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double envMinutes))
        {
            return envMinutes > 0 ? TimeSpan.FromMinutes(envMinutes) : null;
        }

        return TimeSpan.FromMinutes(30);
    }

    private static bool TryReadOptionValue(
        string[] args,
        ref int index,
        string? inlineValue,
        out string value,
        out string? error)
    {
        if (!string.IsNullOrWhiteSpace(inlineValue))
        {
            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            error = $"Option '{args[index]}' requires a value.";
            return false;
        }

        index++;
        value = args[index];
        error = null;
        return true;
    }

    private static string NormalizeTransport(string transport)
        => transport.ToLowerInvariant() switch
        {
            "stdio" => "stdio",
            "pipe" => "named-pipe",
            "named-pipe" => "named-pipe",
            "unix" => "unix-socket",
            "unix-socket" => "unix-socket",
            _ => transport,
        };

    private static void TryDeleteSocketPath(string socketPath)
    {
        try
        {
            if (Path.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<bool> HandleRequestLineAsync(
        ICommandRegistry registry,
        TextWriter writer,
        string line,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string requestId = string.Empty;
        string method = string.Empty;
        Interlocked.Increment(ref ActiveRequestCount);
        bool countedAsActivity = false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            requestId = GetStringProperty(root, "id") ?? string.Empty;
            method = ResolveMethod(root);
            if (string.IsNullOrWhiteSpace(method))
            {
                await WriteErrorResponseAsync(
                    writer,
                    requestId,
                    method,
                    "invalid_request",
                    "Request must include 'method' or a top-level 'command_id'.",
                    "host/handshake",
                    stopwatch).ConfigureAwait(false);
                return false;
            }

            if (!IsAuthorized(root, method))
            {
                await WriteErrorResponseAsync(
                    writer,
                    requestId,
                    method,
                    WorkspaceHostProtocol.ErrorCode.DaemonAuthFailed,
                    "Workspace host request did not include the expected auth token.",
                    "host/handshake",
                    stopwatch).ConfigureAwait(false);
                return false;
            }

            if (!string.Equals(method, WorkspaceHostProtocol.Method.DaemonStatus, StringComparison.Ordinal) &&
                !string.Equals(method, WorkspaceHostProtocol.Method.Handshake, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref NonStatusActiveRequestCount);
                Interlocked.Exchange(ref LastActivityTicksUtc, DateTimeOffset.UtcNow.UtcTicks);
                countedAsActivity = true;
            }

            switch (method)
            {
                case WorkspaceHostProtocol.Method.Handshake:
                    await HandleHandshakeAsync(writer, requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                case WorkspaceHostProtocol.Method.DaemonStatus:
                    await HandleDaemonStatusAsync(writer, requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                case WorkspaceHostProtocol.Method.Shutdown:
                    Log($"shutdown_requested pid={Environment.ProcessId}");
                    await WriteSuccessResponseAsync(
                        writer,
                        requestId,
                        method,
                        stopwatch,
                        data: new
                        {
                            shutting_down = true,
                        }).ConfigureAwait(false);
                    return true;

                case WorkspaceHostProtocol.Method.ToolList:
                    await HandleToolListAsync(registry, writer, requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                case WorkspaceHostProtocol.Method.ToolCall:
                    await HandleToolCallAsync(
                        registry,
                        writer,
                        root,
                        requestId,
                        method,
                        ResolveCommandId(root, method),
                        stopwatch,
                        cancellationToken).ConfigureAwait(false);
                    return false;

                default:
                    if (WorkspaceMethodCommandMap.TryGetValue(method, out string? workspaceCommandId))
                    {
                        await HandleToolCallAsync(
                            registry,
                            writer,
                            root,
                            requestId,
                            method,
                            workspaceCommandId,
                            stopwatch,
                            cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    await WriteErrorResponseAsync(
                        writer,
                        requestId,
                        method,
                        "unknown_method",
                        $"Unsupported method '{method}'. Supported methods: {string.Join(", ", SupportedMethods())}.",
                        "host/handshake",
                        stopwatch).ConfigureAwait(false);
                    return false;
            }
        }
        catch (JsonException ex)
        {
            await WriteErrorResponseAsync(
                writer,
                requestId,
                method,
                "invalid_json",
                $"Request JSON parse failed: {ex.Message}",
                "host/handshake",
                stopwatch).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteErrorResponseAsync(
                writer,
                requestId,
                method,
                "host_shutting_down",
                "Workspace host is shutting down.",
                "daemon/status",
                stopwatch).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            Log($"host_error method={method} type={ex.GetType().FullName} message={SanitizeLogText(ex.Message)}");
            await WriteErrorResponseAsync(
                writer,
                requestId,
                method,
                "host_error",
                ex.Message,
                "daemon/status",
                stopwatch).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (countedAsActivity)
            {
                Interlocked.Exchange(ref LastActivityTicksUtc, DateTimeOffset.UtcNow.UtcTicks);
                Interlocked.Decrement(ref NonStatusActiveRequestCount);
            }

            Interlocked.Decrement(ref ActiveRequestCount);
        }
    }

    private static Task HandleHandshakeAsync(TextWriter writer, string requestId, string method, Stopwatch stopwatch)
    {
        WorkspaceHostHandshake handshake = new(
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            CliVersion: null,
            HostVersion: GetAssemblyVersion(),
            CommandSchemaVersion: EnvelopeVersion,
            Capabilities: new[]
            {
                "stdio-jsonl",
                "named-pipe-jsonl",
                "unix-socket-jsonl",
                "host.handshake",
                "daemon.status",
                "tool.list",
                "tool.call",
                "workspace.preload",
                "workspace.status",
                "workspace.refresh",
                "workspace.close",
                "workspace.list",
                "shutdown",
            });

        return WriteSuccessResponseAsync(writer, requestId, method, stopwatch, handshake);
    }

    private static Task HandleDaemonStatusAsync(TextWriter writer, string requestId, string method, Stopwatch stopwatch)
    {
        DateTimeOffset lastActivityUtc = new(Interlocked.Read(ref LastActivityTicksUtc), TimeSpan.Zero);
        double idleSeconds = Math.Round(Math.Max(0.0, (DateTimeOffset.UtcNow - lastActivityUtc).TotalSeconds), 1);
        double? idleTimeoutSeconds = ConfiguredIdleTimeout is { } timeout ? Math.Round(timeout.TotalSeconds, 1) : null;
        double memoryMb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0), 1);
        object data = new
        {
            protocol_version = WorkspaceHostProtocol.ProtocolVersion,
            host_version = GetAssemblyVersion(),
            process_id = Environment.ProcessId,
            transport = CurrentTransportName,
            workspace_store = "process_hot",
            active_requests = Volatile.Read(ref ActiveRequestCount),
            idle_seconds = idleSeconds,
            idle_timeout_seconds = idleTimeoutSeconds,
            last_activity_utc = lastActivityUtc,
            memory_mb = memoryMb,
            supported_methods = SupportedMethods(),
        };

        return WriteSuccessResponseAsync(writer, requestId, method, stopwatch, data);
    }

    private static Task HandleToolListAsync(
        ICommandRegistry registry,
        TextWriter writer,
        string requestId,
        string method,
        Stopwatch stopwatch)
    {
        IReadOnlyList<CommandDescriptor> commands = registry.ListCommands();
        object data = new
        {
            total = commands.Count,
            commands,
        };

        return WriteSuccessResponseAsync(writer, requestId, method, stopwatch, data);
    }

    private static async Task HandleToolCallAsync(
        ICommandRegistry registry,
        TextWriter writer,
        JsonElement root,
        string requestId,
        string method,
        string? commandId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            await WriteErrorResponseAsync(
                writer,
                requestId,
                method,
                "invalid_request",
                "tool/call requires 'command_id'.",
                "tool/list",
                stopwatch).ConfigureAwait(false);
            return;
        }

        if (!registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            await WriteErrorResponseAsync(
                writer,
                requestId,
                method,
                "command_not_found",
                $"Command '{commandId}' was not found.",
                "tool/list",
                stopwatch,
                commandId).ConfigureAwait(false);
            return;
        }

        JsonElement input = GetInputElement(root, commandId, command.Descriptor.RunsOnHotWorkspace);

        IReadOnlyList<CommandError> validationErrors = command.Validate(input);
        if (validationErrors.Count > 0)
        {
            await WriteResponseAsync(writer, new WorkspaceHostResponse(
                Id: requestId,
                Ok: false,
                ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
                Method: method,
                ElapsedMs: ElapsedMilliseconds(stopwatch),
                CommandId: commandId,
                Version: EnvelopeVersion,
                Data: null,
                Errors: validationErrors,
                Summary: $"Validation failed for {commandId}.")).ConfigureAwait(false);
            return;
        }

        Stopwatch freshnessTimer = Stopwatch.StartNew();
        HotWorkspaceFreshnessResult? freshness = await EnsureHotWorkspaceFreshAsync(
            command,
            root,
            input,
            cancellationToken).ConfigureAwait(false);
        freshnessTimer.Stop();

        Stopwatch executeTimer = Stopwatch.StartNew();
        CommandExecutionResult result = await command.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        executeTimer.Stop();

        UpdateAliasBindings(root, method, result.Data, result.Ok);
        object? responseData = BuildResponseData(commandId, result.Data, result.Ok);
        WorkspaceHostWorkspaceMetadata? workspaceMetadata = BuildWorkspaceMetadata(
            root,
            method,
            responseData,
            result.Ok) ?? BuildFreshnessMetadata(root, freshness);

        await WriteResponseAsync(writer, new WorkspaceHostResponse(
            Id: requestId,
            Ok: result.Ok,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: method,
            ElapsedMs: ElapsedMilliseconds(stopwatch),
            CommandId: commandId,
            Version: EnvelopeVersion,
            Workspace: workspaceMetadata,
            Data: responseData,
            Telemetry: result.Telemetry,
            Errors: result.Errors,
            FreshnessMs: freshness is null ? null : Math.Round(freshnessTimer.Elapsed.TotalMilliseconds, 3),
            ExecuteMs: Math.Round(executeTimer.Elapsed.TotalMilliseconds, 3),
            FreshnessMode: freshness?.Mode)).ConfigureAwait(false);
    }

    /// <summary>
    /// A command that runs on a hot workspace must see the files as they are on disk now, not as they were at the last
    /// call. The lifecycle commands (<c>workspace.*</c>) are excluded: they report or change that state themselves.
    /// </summary>
    private static async Task<HotWorkspaceFreshnessResult?> EnsureHotWorkspaceFreshAsync(
        IAgentCommand command,
        JsonElement root,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        if (!command.Descriptor.RunsOnHotWorkspace ||
            command.Descriptor.Id.StartsWith("workspace.", StringComparison.Ordinal) ||
            !TryGetProperty(input, "workspace_handle", out JsonElement handleProperty) ||
            handleProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(handleProperty.GetString()))
        {
            return null;
        }

        return await HotWorkspaceFreshness.EnsureFreshAsync(
                handleProperty.GetString()!,
                GetStringProperty(root, "refresh_policy"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static WorkspaceHostWorkspaceMetadata? BuildFreshnessMetadata(
        JsonElement requestRoot,
        HotWorkspaceFreshnessResult? freshness)
    {
        if (freshness is null ||
            !WorkspaceHostStoreProvider.Current.TryGet(freshness.WorkspaceHandle, out HostedWorkspace? hosted) ||
            hosted is null)
        {
            return null;
        }

        List<string> diagnostics = hosted.Workspace.WorkspaceDiagnostics.ToList();
        if (freshness.Error is not null)
        {
            diagnostics.Add($"refresh_error: {freshness.Error.Code}: {freshness.Error.Message}");
        }

        return new WorkspaceHostWorkspaceMetadata(
            Alias: GetStringProperty(requestRoot, "workspace_alias"),
            WorkspaceHandle: freshness.WorkspaceHandle,
            WorkspaceKind: hosted.Workspace.WorkspaceKind,
            SolutionScoped: string.Equals(hosted.Workspace.WorkspaceKind, "solution", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(hosted.Workspace.WorkspaceKind, "slnx", StringComparison.OrdinalIgnoreCase),
            ResolutionSource: "workspace_handle",
            RefreshPolicy: freshness.RefreshPolicy,
            RefreshAction: freshness.RefreshAction,
            DirtyBefore: freshness.DirtyBefore,
            DirtyAfter: freshness.DirtyAfter,
            RequiresReload: freshness.RequiresReload,
            RequiresDesignTimeBuild: false,
            WorkspaceFingerprint: hosted.WorkspaceFingerprint,
            ProjectCount: hosted.Workspace.ProjectCount,
            DocumentCount: hosted.Workspace.DocumentCount,
            InvalidatedPaths: freshness.InvalidatedPaths,
            WorkspaceDiagnostics: diagnostics);
    }

    private static object? BuildResponseData(string commandId, object? data, bool resultOk)
    {
        if (!resultOk ||
            data is null ||
            !string.Equals(commandId, "workspace.list", StringComparison.Ordinal))
        {
            return data;
        }

        JsonElement dataElement = JsonSerializer.SerializeToElement(data, JsonOptions);
        Dictionary<string, object?> flattened = new(StringComparer.Ordinal);
        if (dataElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in dataElement.EnumerateObject())
            {
                flattened[property.Name] = property.Value.Clone();
            }
        }

        flattened["aliases"] = SnapshotAliases()
            .Select(pair => new
            {
                alias = pair.Key,
                workspace_handle = pair.Value,
            })
            .ToArray();
        return flattened;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> SnapshotAliases()
    {
        lock (AliasGate)
        {
            return WorkspaceAliases
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static WorkspaceHostWorkspaceMetadata? BuildWorkspaceMetadata(
        JsonElement requestRoot,
        string method,
        object? data,
        bool resultOk)
    {
        if (!resultOk || data is null)
        {
            return null;
        }

        if (!string.Equals(method, WorkspaceHostProtocol.Method.WorkspacePreload, StringComparison.Ordinal) &&
            !string.Equals(method, WorkspaceHostProtocol.Method.WorkspaceStatus, StringComparison.Ordinal) &&
            !string.Equals(method, WorkspaceHostProtocol.Method.WorkspaceRefresh, StringComparison.Ordinal) &&
            !string.Equals(method, WorkspaceHostProtocol.Method.WorkspaceClose, StringComparison.Ordinal))
        {
            return null;
        }

        JsonElement dataElement = JsonSerializer.SerializeToElement(data, JsonOptions);
        string? workspaceHandle = GetStringProperty(dataElement, "workspace_handle");
        if (string.IsNullOrWhiteSpace(workspaceHandle))
        {
            return null;
        }

        bool dirty = GetBoolProperty(dataElement, "dirty", defaultValue: false);
        bool dirtyBefore = GetBoolProperty(dataElement, "dirty_before", defaultValue: dirty);
        bool dirtyAfter = GetBoolProperty(dataElement, "dirty_after", defaultValue: dirty);
        bool requiresReload = GetBoolProperty(dataElement, "requires_reload", defaultValue: false);
        int projectCount = GetIntProperty(dataElement, "projects_loaded", defaultValue: 0);
        int documentCount = GetIntProperty(dataElement, "documents_loaded", defaultValue: 0);

        return new WorkspaceHostWorkspaceMetadata(
            Alias: GetStringProperty(requestRoot, "workspace_alias"),
            WorkspaceHandle: workspaceHandle,
            WorkspaceKind: GetStringProperty(dataElement, "workspace_kind"),
            SolutionScoped: GetBoolProperty(dataElement, "solution_scoped", defaultValue: false),
            ResolutionSource: string.Equals(method, WorkspaceHostProtocol.Method.WorkspacePreload, StringComparison.Ordinal)
                ? "workspace_path"
                : "workspace_handle",
            RefreshPolicy: GetStringProperty(requestRoot, "refresh_policy") ?? WorkspaceHostProtocol.RefreshPolicy.Auto,
            RefreshAction: GetStringProperty(dataElement, "refresh_action") ?? WorkspaceHostProtocol.RefreshAction.None,
            DirtyBefore: dirtyBefore,
            DirtyAfter: dirtyAfter,
            RequiresReload: requiresReload,
            RequiresDesignTimeBuild: false,
            WorkspaceFingerprint: GetStringProperty(dataElement, "workspace_fingerprint"),
            ProjectCount: projectCount,
            DocumentCount: documentCount,
            InvalidatedPaths: GetStringArrayProperty(dataElement, "invalidated_paths"),
            WorkspaceDiagnostics: GetStringArrayProperty(dataElement, "workspace_diagnostics"));
    }

    private static void UpdateAliasBindings(JsonElement requestRoot, string method, object? data, bool resultOk)
    {
        if (!resultOk)
        {
            return;
        }

        string? alias = GetStringProperty(requestRoot, "workspace_alias");
        if (string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        if (string.Equals(method, WorkspaceHostProtocol.Method.WorkspaceClose, StringComparison.Ordinal))
        {
            lock (AliasGate)
            {
                WorkspaceAliases.Remove(alias);
            }

            return;
        }

        if (!string.Equals(method, WorkspaceHostProtocol.Method.WorkspacePreload, StringComparison.Ordinal) || data is null)
        {
            return;
        }

        JsonElement dataElement = JsonSerializer.SerializeToElement(data, JsonOptions);
        string? handle = GetStringProperty(dataElement, "workspace_handle");
        if (string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        lock (AliasGate)
        {
            WorkspaceAliases[alias] = handle;
        }
    }

    private static JsonElement GetInputElement(JsonElement root, string? commandId, bool runsOnHotWorkspace = false)
    {
        bool shouldResolveAlias = runsOnHotWorkspace ||
            string.Equals(commandId, "workspace.status", StringComparison.Ordinal) ||
            string.Equals(commandId, "workspace.refresh", StringComparison.Ordinal) ||
            string.Equals(commandId, "workspace.close", StringComparison.Ordinal);

        if (TryGetProperty(root, "input", out JsonElement input))
        {
            if (shouldResolveAlias && !TryGetProperty(input, "workspace_handle", out _))
            {
                string? alias = GetStringProperty(root, "workspace_alias");
                if (!string.IsNullOrWhiteSpace(alias) && TryResolveAlias(alias, out string? handle))
                {
                    Dictionary<string, object?> augmentedInput = new(StringComparer.Ordinal);
                    if (input.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in input.EnumerateObject())
                        {
                            augmentedInput[property.Name] = property.Value.Clone();
                        }
                    }

                    augmentedInput["workspace_handle"] = handle;
                    AddStringIfPresent(root, augmentedInput, "refresh_policy");
                    return JsonSerializer.SerializeToElement(augmentedInput);
                }
            }

            return input.Clone();
        }

        Dictionary<string, object?> synthesizedInput = new(StringComparer.Ordinal);
        AddStringIfPresent(root, synthesizedInput, "workspace_handle");
        AddStringIfPresent(root, synthesizedInput, "workspace_alias");
        AddStringIfPresent(root, synthesizedInput, "refresh_policy");
        if (shouldResolveAlias && !synthesizedInput.ContainsKey("workspace_handle"))
        {
            string? alias = GetStringProperty(root, "workspace_alias");
            if (!string.IsNullOrWhiteSpace(alias) && TryResolveAlias(alias, out string? handle))
            {
                synthesizedInput["workspace_handle"] = handle;
            }
        }

        return JsonSerializer.SerializeToElement(synthesizedInput);
    }

    private static bool TryResolveAlias(string alias, out string? handle)
    {
        lock (AliasGate)
        {
            return WorkspaceAliases.TryGetValue(alias, out handle);
        }
    }

    private static string ResolveMethod(JsonElement root)
    {
        string? explicitMethod = GetStringProperty(root, "method");
        if (!string.IsNullOrWhiteSpace(explicitMethod))
        {
            return explicitMethod;
        }

        return TryGetProperty(root, "command_id", out _) ? WorkspaceHostProtocol.Method.ToolCall : string.Empty;
    }

    private static string? ResolveCommandId(JsonElement root, string method)
    {
        if (WorkspaceMethodCommandMap.TryGetValue(method, out string? workspaceCommandId))
        {
            return workspaceCommandId;
        }

        return GetStringProperty(root, "command_id");
    }

    private static bool IsAuthorized(JsonElement root, string method)
    {
        if (string.IsNullOrWhiteSpace(ExpectedAuthToken) ||
            string.Equals(method, WorkspaceHostProtocol.Method.Handshake, StringComparison.Ordinal))
        {
            return true;
        }

        string? provided = GetStringProperty(root, "auth_token");
        return !string.IsNullOrWhiteSpace(provided) &&
               FixedTimeEquals(provided, ExpectedAuthToken);
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static string? GetStringProperty(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool GetBoolProperty(JsonElement root, string propertyName, bool defaultValue)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static int GetIntProperty(JsonElement root, string propertyName, int defaultValue)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static IReadOnlyList<string> GetStringArrayProperty(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        List<string> values = new();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()))
            {
                values.Add(item.GetString()!);
            }
        }

        return values;
    }

    private static void AddStringIfPresent(
        JsonElement root,
        Dictionary<string, object?> input,
        string propertyName)
    {
        string? value = GetStringProperty(root, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            input[propertyName] = value;
        }
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out JsonElement property))
        {
            value = property;
            return true;
        }

        value = default;
        return false;
    }

    private static Task WriteSuccessResponseAsync(
        TextWriter writer,
        string requestId,
        string method,
        Stopwatch stopwatch,
        object? data)
    {
        return WriteResponseAsync(writer, new WorkspaceHostResponse(
            Id: requestId,
            Ok: true,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: method,
            ElapsedMs: ElapsedMilliseconds(stopwatch),
            Data: data));
    }

    private static Task WriteErrorResponseAsync(
        TextWriter writer,
        string requestId,
        string method,
        string code,
        string message,
        string nextCommand,
        Stopwatch stopwatch,
        string? commandId = null)
    {
        CommandError error = new(
            Code: code,
            Message: message,
            Details: new
            {
                next_command = nextCommand,
            });

        return WriteResponseAsync(writer, new WorkspaceHostResponse(
            Id: requestId,
            Ok: false,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: method,
            ElapsedMs: ElapsedMilliseconds(stopwatch),
            CommandId: commandId,
            Version: commandId is null ? null : EnvelopeVersion,
            Data: null,
            Errors: new[] { error },
            Summary: commandId is null ? null : message));
    }

    private static async Task WriteResponseAsync(TextWriter writer, WorkspaceHostResponse response)
    {
        string json = JsonSerializer.Serialize(response, JsonOptions);
        try
        {
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Client closed its pipe or socket before reading the response; keep the host alive.
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static double ElapsedMilliseconds(Stopwatch stopwatch)
        => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);

    private static string GetAssemblyVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static IReadOnlyList<string> SupportedMethods()
        => new[]
        {
            WorkspaceHostProtocol.Method.Handshake,
            WorkspaceHostProtocol.Method.DaemonStatus,
            WorkspaceHostProtocol.Method.ToolList,
            WorkspaceHostProtocol.Method.ToolCall,
            WorkspaceHostProtocol.Method.WorkspacePreload,
            WorkspaceHostProtocol.Method.WorkspaceStatus,
            WorkspaceHostProtocol.Method.WorkspaceRefresh,
            WorkspaceHostProtocol.Method.WorkspaceClose,
            WorkspaceHostProtocol.Method.WorkspaceList,
            WorkspaceHostProtocol.Method.Shutdown,
        };

    private static readonly List<IDisposable> SignalRegistrations = new();

    private static void InitializeLog(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            LogFilePath = null;
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(logPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            LogFilePath = fullPath;
        }
        catch
        {
            LogFilePath = null;
        }
    }

    private static void RegisterLifecycleLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Exception? ex = args.ExceptionObject as Exception;
            string detail = ex is not null
                ? $"{ex.GetType().FullName}: {SanitizeLogText(ex.Message)}"
                : SanitizeLogText(args.ExceptionObject?.ToString() ?? "unknown");
            Log($"unhandled_exception terminating={args.IsTerminating} detail={detail}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"unobserved_task_exception detail={SanitizeLogText(args.Exception.ToString())}");
            args.SetObserved();
        };

        Console.CancelKeyPress += (_, args) =>
        {
            Log($"cancel_key_press special_key={args.SpecialKey}");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log($"process_exit pid={Environment.ProcessId}");
        };

        TryRegisterPosixSignal(PosixSignal.SIGTERM, "SIGTERM");
        TryRegisterPosixSignal(PosixSignal.SIGINT, "SIGINT");
    }

    private static void TryRegisterPosixSignal(PosixSignal signal, string label)
    {
        try
        {
            PosixSignalRegistration registration = PosixSignalRegistration.Create(signal, _ =>
            {
                Log($"signal_received signal={label} pid={Environment.ProcessId}");
            });
            SignalRegistrations.Add(registration);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void Log(string message)
    {
        string? path = LogFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string line = $"{DateTimeOffset.UtcNow:O} [pid:{Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (LogGate)
        {
            try
            {
                if (File.Exists(path))
                {
                    FileInfo info = new(path);
                    if (info.Length >= MaxLogFileBytes)
                    {
                        string[] existingLines = File.ReadAllLines(path);
                        int keep = Math.Min(existingLines.Length, 100);
                        string trimmed = string.Join(Environment.NewLine, existingLines.Skip(existingLines.Length - keep)) + Environment.NewLine;
                        File.WriteAllText(path, trimmed, Utf8NoBom);
                    }
                }

                File.AppendAllText(path, line, Utf8NoBom);
            }
            catch
            {
            }
        }
    }

    private static string SanitizeLogText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string collapsed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return collapsed.Length <= 400 ? collapsed : collapsed[..400] + "...";
    }

    private sealed record WorkspaceHostOptions(
        string Transport,
        string? PipeName,
        string? SocketPath,
        string? AuthToken,
        string? LogPath = null,
        bool ExitOnStdinEof = false,
        TimeSpan? IdleTimeout = null,
        string? ManifestPath = null,
        string? LaunchWarning = null);
}
