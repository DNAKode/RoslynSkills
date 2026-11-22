#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.TransportServer;

internal static class Program
{
    private const string EnvelopeVersion = "1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public static async Task<int> Main(string[] args)
    {
        _ = args;
        ICommandRegistry registry = DefaultRegistryFactory.Create();

        while (true)
        {
            string? line = await Console.In.ReadLineAsync().ConfigureAwait(false);
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
                break;
            }
        }

        return 0;
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
                await WriteErrorAsync(
                    requestId,
                    "invalid_request",
                    "Request must include 'method' or a top-level 'command_id'.",
                    stopwatch.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                return false;
            }

            switch (method)
            {
                case "shutdown":
                    await WriteObjectAsync(new
                    {
                        id = requestId,
                        ok = true,
                        method,
                        elapsed_ms = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                    }).ConfigureAwait(false);
                    return true;

                case "tool/list":
                    await HandleToolListAsync(registry, requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                case "tool/call":
                    await HandleToolCallAsync(registry, root, requestId, method, stopwatch).ConfigureAwait(false);
                    return false;

                default:
                    await WriteErrorAsync(
                        requestId,
                        "unknown_method",
                        $"Unsupported method '{method}'. Supported methods: tool/list, tool/call, shutdown.",
                        stopwatch.Elapsed.TotalMilliseconds,
                        method).ConfigureAwait(false);
                    return false;
            }
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(
                requestId,
                "invalid_json",
                $"Request JSON parse failed: {ex.Message}",
                stopwatch.Elapsed.TotalMilliseconds,
                method).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(
                requestId,
                "server_error",
                ex.Message,
                stopwatch.Elapsed.TotalMilliseconds,
                method).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task HandleToolListAsync(
        ICommandRegistry registry,
        string requestId,
        string method,
        Stopwatch stopwatch)
    {
        IReadOnlyList<CommandDescriptor> commands = registry.ListCommands();
        await WriteObjectAsync(new
        {
            id = requestId,
            ok = true,
            method,
            elapsed_ms = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            data = new
            {
                total = commands.Count,
                commands,
            },
        }).ConfigureAwait(false);
    }

    private static async Task HandleToolCallAsync(
        ICommandRegistry registry,
        JsonElement root,
        string requestId,
        string method,
        Stopwatch stopwatch)
    {
        string? commandId = GetStringProperty(root, "command_id");
        JsonElement input = GetInputElement(root);

        if (string.IsNullOrWhiteSpace(commandId))
        {
            await WriteErrorAsync(
                requestId,
                "invalid_request",
                "tool/call requires 'command_id'.",
                stopwatch.Elapsed.TotalMilliseconds,
                method).ConfigureAwait(false);
            return;
        }

        if (!registry.TryGet(commandId, out IAgentCommand? command) || command is null)
        {
            double elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
            await WriteObjectAsync(new CommandEnvelope(
                Ok: false,
                CommandId: commandId,
                Version: EnvelopeVersion,
                Data: null,
                Errors: new[] { new CommandError("command_not_found", $"Command '{commandId}' was not found.") },
                TraceId: string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                Transport: new CommandTransportMetadata(
                    Mode: "transport_server",
                    Timings: new CommandTransportTimings(TotalMs: elapsedMs)))).ConfigureAwait(false);
            return;
        }

        Stopwatch validateTimer = Stopwatch.StartNew();
        IReadOnlyList<CommandError> validationErrors = command.Validate(input);
        validateTimer.Stop();
        if (validationErrors.Count > 0)
        {
            double validateMs = Math.Round(validateTimer.Elapsed.TotalMilliseconds, 3);
            double totalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
            CommandEnvelope invalidEnvelope = new(
                Ok: false,
                CommandId: commandId,
                Version: EnvelopeVersion,
                Data: null,
                Errors: validationErrors,
                TraceId: string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                Telemetry: new
                {
                    timing = new
                    {
                        validate_ms = (int)validateTimer.ElapsedMilliseconds,
                        execute_ms = (int?)null,
                        total_ms = (int)stopwatch.ElapsedMilliseconds,
                    },
                    cache_context = new
                    {
                        binary_launch_mode = "transport_server",
                    },
                    command_telemetry = (object?)null,
                },
                Transport: new CommandTransportMetadata(
                    Mode: "transport_server",
                    Timings: new CommandTransportTimings(
                        ValidateMs: validateMs,
                        TotalMs: totalMs)));

            await WriteObjectAsync(invalidEnvelope).ConfigureAwait(false);
            return;
        }

        Stopwatch executeTimer = Stopwatch.StartNew();
        CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None).ConfigureAwait(false);
        executeTimer.Stop();
        stopwatch.Stop();
        double execMs = Math.Round(executeTimer.Elapsed.TotalMilliseconds, 3);
        double totalElapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
        CommandEnvelope envelope = new(
            Ok: result.Ok,
            CommandId: commandId,
            Version: EnvelopeVersion,
            Data: result.Data,
            Errors: result.Errors,
            TraceId: string.IsNullOrWhiteSpace(requestId) ? null : requestId,
            Telemetry: new
            {
                timing = new
                {
                    validate_ms = (int)validateTimer.ElapsedMilliseconds,
                    execute_ms = (int)executeTimer.ElapsedMilliseconds,
                    total_ms = (int)stopwatch.ElapsedMilliseconds,
                },
                cache_context = new
                {
                    binary_launch_mode = "transport_server",
                },
                command_telemetry = result.Telemetry,
            },
            Transport: new CommandTransportMetadata(
                Mode: "transport_server",
                Timings: new CommandTransportTimings(
                    ValidateMs: Math.Round(validateTimer.Elapsed.TotalMilliseconds, 3),
                    ExecuteMs: execMs,
                    HostElapsedMs: execMs,
                    TotalMs: totalElapsedMs)));

        await WriteObjectAsync(envelope).ConfigureAwait(false);
    }

    private static JsonElement GetInputElement(JsonElement root)
    {
        if (TryGetProperty(root, "input", out JsonElement input))
        {
            return input.Clone();
        }

        return JsonSerializer.SerializeToElement(new { });
    }

    private static string ResolveMethod(JsonElement root)
    {
        string? explicitMethod = GetStringProperty(root, "method");
        if (!string.IsNullOrWhiteSpace(explicitMethod))
        {
            return explicitMethod;
        }

        return TryGetProperty(root, "command_id", out _) ? "tool/call" : string.Empty;
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

    private static Task WriteErrorAsync(
        string requestId,
        string code,
        string message,
        double elapsedMs,
        string? method = null,
        string? commandId = null)
    {
        return WriteObjectAsync(new
        {
            id = requestId,
            ok = false,
            method,
            command_id = commandId,
            elapsed_ms = Math.Round(elapsedMs, 3),
            error = new
            {
                code,
                message,
            },
        });
    }

    private static async Task WriteObjectAsync(object value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
    }
}

