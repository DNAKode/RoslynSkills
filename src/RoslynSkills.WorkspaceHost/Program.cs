#nullable enable
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;

namespace RoslynSkills.WorkspaceHost;

internal static class Program
{
    private const string EnvelopeVersion = "1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static TextWriter ResponseWriter = Console.Out;
    private static string CurrentTransportName = "stdio-jsonl";

    private static readonly IReadOnlyDictionary<string, string> WorkspaceMethodCommandMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkspaceHostProtocol.Method.WorkspacePreload] = "workspace.preload",
            [WorkspaceHostProtocol.Method.WorkspaceStatus] = "workspace.status",
            [WorkspaceHostProtocol.Method.WorkspaceClose] = "workspace.close",
        };

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseOptions(args, out WorkspaceHostOptions? options, out string? error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            return 2;
        }

        ICommandRegistry registry = DefaultRegistryFactory.Create();

        return options.Transport switch
        {
            "stdio" => await RunStdioAsync(registry).ConfigureAwait(false),
            "named-pipe" => await RunNamedPipeAsync(registry, options.PipeName!).ConfigureAwait(false),
            "unix-socket" => await RunUnixSocketAsync(registry, options.SocketPath!).ConfigureAwait(false),
            _ => 2,
        };
    }

    private static async Task<int> RunStdioAsync(ICommandRegistry registry)
    {
        _ = await RunJsonLinesAsync(registry, Console.In, Console.Out, "stdio-jsonl").ConfigureAwait(false);
        return 0;
    }

    private static async Task<bool> RunJsonLinesAsync(
        ICommandRegistry registry,
        TextReader input,
        TextWriter output,
        string transportName)
    {
        ResponseWriter = output;
        CurrentTransportName = transportName;

        while (true)
        {
            string? line = await input.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            bool shouldExit = await HandleRequestLineAsync(registry, line).ConfigureAwait(false);
            if (shouldExit)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<int> RunNamedPipeAsync(ICommandRegistry registry, string pipeName)
    {
        while (true)
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            StreamReader reader = new(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            StreamWriter writer = new(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };

            bool shouldExit = await RunJsonLinesAsync(registry, reader, writer, "named-pipe-jsonl").ConfigureAwait(false);
            if (shouldExit)
            {
                return 0;
            }
        }
    }

    private static async Task<int> RunUnixSocketAsync(ICommandRegistry registry, string socketPath)
    {
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bool bound = false;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            bound = true;
            listener.Listen(backlog: 1);

            while (true)
            {
                using Socket connection = await listener.AcceptAsync().ConfigureAwait(false);
                await using NetworkStream stream = new(connection, ownsSocket: false);
                StreamReader reader = new(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                StreamWriter writer = new(stream, Utf8NoBom, bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                bool shouldExit = await RunJsonLinesAsync(registry, reader, writer, "unix-socket-jsonl").ConfigureAwait(false);
                if (shouldExit)
                {
                    return 0;
                }
            }
        }
        finally
        {
            if (bound)
            {
                TryDeleteSocketPath(socketPath);
            }
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
                        options = new WorkspaceHostOptions("stdio", null, null);
                        return false;
                    }

                    transport = NormalizeTransport(transport);
                    break;

                case "--pipe-name":
                    if (!TryReadOptionValue(args, ref i, value, out pipeName, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null);
                        return false;
                    }

                    break;

                case "--socket-path":
                    if (!TryReadOptionValue(args, ref i, value, out socketPath, out error))
                    {
                        options = new WorkspaceHostOptions("stdio", null, null);
                        return false;
                    }

                    break;

                default:
                    options = new WorkspaceHostOptions("stdio", null, null);
                    error = $"Unknown workspace host option '{arg}'. Supported options: --transport, --pipe-name, --socket-path.";
                    return false;
            }
        }

        if (transport == "named-pipe" && string.IsNullOrWhiteSpace(pipeName))
        {
            options = new WorkspaceHostOptions("stdio", null, null);
            error = "--transport named-pipe requires --pipe-name.";
            return false;
        }

        if (transport == "unix-socket" && string.IsNullOrWhiteSpace(socketPath))
        {
            options = new WorkspaceHostOptions("stdio", null, null);
            error = "--transport unix-socket requires --socket-path.";
            return false;
        }

        if (transport is not ("stdio" or "named-pipe" or "unix-socket"))
        {
            options = new WorkspaceHostOptions("stdio", null, null);
            error = $"Unsupported transport '{transport}'. Supported transports: stdio, named-pipe, unix-socket.";
            return false;
        }

        options = new WorkspaceHostOptions(transport, pipeName, socketPath);
        error = null;
        return true;
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
            if (File.Exists(socketPath))
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

    private static async Task<bool> HandleRequestLineAsync(ICommandRegistry registry, string line)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string requestId = string.Empty;
        string method = string.Empty;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            requestId = GetStringProperty(root, "id") ?? string.Empty;
            method = ResolveMethod(root);
            if (string.IsNullOrWhiteSpace(method))
            {
                await WriteErrorResponseAsync(
                    requestId,
                    method,
                    "invalid_request",
                    "Request must include 'method' or a top-level 'command_id'.",
                    "host/handshake",
                    stopwatch).ConfigureAwait(false);
                return false;
            }

            switch (method)
            {
                case WorkspaceHostProtocol.Method.Handshake:
                    await HandleHandshakeAsync(requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                case WorkspaceHostProtocol.Method.DaemonStatus:
                    await HandleDaemonStatusAsync(requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                case WorkspaceHostProtocol.Method.Shutdown:
                    await WriteSuccessResponseAsync(
                        requestId,
                        method,
                        stopwatch,
                        data: new
                        {
                            shutting_down = true,
                        }).ConfigureAwait(false);
                    return true;

                case WorkspaceHostProtocol.Method.ToolList:
                    await HandleToolListAsync(registry, requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                case WorkspaceHostProtocol.Method.ToolCall:
                    await HandleToolCallAsync(
                        registry,
                        root,
                        requestId,
                        method,
                        ResolveCommandId(root, method),
                        stopwatch).ConfigureAwait(false);
                    return false;

                default:
                    if (WorkspaceMethodCommandMap.TryGetValue(method, out string? workspaceCommandId))
                    {
                        await HandleToolCallAsync(
                            registry,
                            root,
                            requestId,
                            method,
                            workspaceCommandId,
                            stopwatch).ConfigureAwait(false);
                        return false;
                    }

                    await WriteErrorResponseAsync(
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
                requestId,
                method,
                "invalid_json",
                $"Request JSON parse failed: {ex.Message}",
                "host/handshake",
                stopwatch).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            await WriteErrorResponseAsync(
                requestId,
                method,
                "host_error",
                ex.Message,
                "daemon/status",
                stopwatch).ConfigureAwait(false);
            return false;
        }
    }

    private static Task HandleHandshakeAsync(string requestId, string method, Stopwatch stopwatch)
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
                "workspace.close",
                "shutdown",
            });

        return WriteSuccessResponseAsync(requestId, method, stopwatch, handshake);
    }

    private static Task HandleDaemonStatusAsync(string requestId, string method, Stopwatch stopwatch)
    {
        object data = new
        {
            protocol_version = WorkspaceHostProtocol.ProtocolVersion,
            host_version = GetAssemblyVersion(),
            process_id = Environment.ProcessId,
            transport = CurrentTransportName,
            workspace_store = "process_hot",
            supported_methods = SupportedMethods(),
        };

        return WriteSuccessResponseAsync(requestId, method, stopwatch, data);
    }

    private static Task HandleToolListAsync(
        ICommandRegistry registry,
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

        return WriteSuccessResponseAsync(requestId, method, stopwatch, data);
    }

    private static async Task HandleToolCallAsync(
        ICommandRegistry registry,
        JsonElement root,
        string requestId,
        string method,
        string? commandId,
        Stopwatch stopwatch)
    {
        JsonElement input = GetInputElement(root);

        if (string.IsNullOrWhiteSpace(commandId))
        {
            await WriteErrorResponseAsync(
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
                requestId,
                method,
                "command_not_found",
                $"Command '{commandId}' was not found.",
                "tool/list",
                stopwatch,
                commandId).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<CommandError> validationErrors = command.Validate(input);
        if (validationErrors.Count > 0)
        {
            CommandEnvelope invalidEnvelope = new(
                Ok: false,
                CommandId: commandId,
                Version: EnvelopeVersion,
                Data: null,
                Errors: validationErrors,
                TraceId: null,
                Summary: $"Validation failed for {commandId}.");

            await WriteResponseAsync(new WorkspaceHostResponse(
                Id: requestId,
                Ok: false,
                ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
                Method: method,
                ElapsedMs: ElapsedMilliseconds(stopwatch),
                Envelope: invalidEnvelope,
                Errors: validationErrors)).ConfigureAwait(false);
            return;
        }

        CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None).ConfigureAwait(false);
        WorkspaceHostWorkspaceMetadata? workspaceMetadata = BuildWorkspaceMetadata(
            root,
            method,
            result.Data,
            result.Ok);
        CommandEnvelope envelope = new(
            Ok: result.Ok,
            CommandId: commandId,
            Version: EnvelopeVersion,
            Data: result.Data,
            Errors: result.Errors,
            TraceId: null,
            Telemetry: result.Telemetry);

        await WriteResponseAsync(new WorkspaceHostResponse(
            Id: requestId,
            Ok: result.Ok,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: method,
            ElapsedMs: ElapsedMilliseconds(stopwatch),
            Workspace: workspaceMetadata,
            Envelope: envelope,
            Errors: result.Ok ? null : result.Errors)).ConfigureAwait(false);
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
            RefreshAction: WorkspaceHostProtocol.RefreshAction.None,
            DirtyBefore: dirty,
            DirtyAfter: dirty,
            RequiresReload: false,
            RequiresDesignTimeBuild: false,
            WorkspaceFingerprint: GetStringProperty(dataElement, "workspace_fingerprint"),
            ProjectCount: projectCount,
            DocumentCount: documentCount,
            InvalidatedPaths: GetStringArrayProperty(dataElement, "invalidated_paths"),
            WorkspaceDiagnostics: GetStringArrayProperty(dataElement, "workspace_diagnostics"));
    }

    private static JsonElement GetInputElement(JsonElement root)
    {
        if (TryGetProperty(root, "input", out JsonElement input))
        {
            return input.Clone();
        }

        Dictionary<string, object?> synthesizedInput = new(StringComparer.Ordinal);
        AddStringIfPresent(root, synthesizedInput, "workspace_handle");
        AddStringIfPresent(root, synthesizedInput, "workspace_alias");
        AddStringIfPresent(root, synthesizedInput, "refresh_policy");

        return JsonSerializer.SerializeToElement(synthesizedInput);
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
        string requestId,
        string method,
        Stopwatch stopwatch,
        object? data)
    {
        return WriteResponseAsync(new WorkspaceHostResponse(
            Id: requestId,
            Ok: true,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: method,
            ElapsedMs: ElapsedMilliseconds(stopwatch),
            Data: data));
    }

    private static Task WriteErrorResponseAsync(
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

        CommandEnvelope? envelope = commandId is null
            ? null
            : new CommandEnvelope(
                Ok: false,
                CommandId: commandId,
                Version: EnvelopeVersion,
                Data: null,
                Errors: new[] { error },
                TraceId: null,
                Summary: message);

        return WriteResponseAsync(new WorkspaceHostResponse(
            Id: requestId,
            Ok: false,
            ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
            Method: method,
            ElapsedMs: ElapsedMilliseconds(stopwatch),
            Envelope: envelope,
            Errors: new[] { error }));
    }

    private static async Task WriteResponseAsync(WorkspaceHostResponse response)
    {
        string json = JsonSerializer.Serialize(response, JsonOptions);
        await ResponseWriter.WriteLineAsync(json).ConfigureAwait(false);
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
            WorkspaceHostProtocol.Method.WorkspaceClose,
            WorkspaceHostProtocol.Method.Shutdown,
        };

    private sealed record WorkspaceHostOptions(
        string Transport,
        string? PipeName,
        string? SocketPath);
}
