#nullable enable
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynSkills.Contracts;
using RoslynSkills.Core;

namespace RoslynSkills.McpServer;

internal static class Program
{
    private enum McpToolMode
    {
        Full = 0,
        Slim = 1,
    }

    private const string JsonRpcVersion = "2.0";
    private const string McpProtocolVersion = "2025-06-18";
    private const string CommandEnvelopeVersion = "1.0";
    private const string ToolPrefix = "roslyn_";
    private const string ResourceScheme = "roslyn";
    private const string CommandsCatalogResourceUri = "roslyn://commands";
    private const string CommandResourceTemplate = "roslyn://command/{commandId}?key=value";
    private const string CommandResourceTemplateInputJson = "roslyn://command/{commandId}?input_json=%7B...%7D";
    private const string CommandResourceTemplateInputBase64 = "roslyn://command/{commandId}?input_b64=ey4uLn0";
    private const int MaxHeaderLineLength = 8192;

    private static readonly JsonSerializerOptions JsonOptions = McpContractsJsonContext.CreateMcpOptions();
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly SemaphoreSlim OutputGate = new(1, 1);
    private static readonly ArrayBufferWriter<byte> OutputBuffer = new(8192);

    private static HotCommandRouter? Router;
    private static McpToolMode ActiveMode = McpToolMode.Full;
    private static ReadOnlyMemory<byte> CachedToolListResult;
    private static ReadOnlyMemory<byte> CachedResourceListResult;
    private static ReadOnlyMemory<byte> CachedResourceTemplateListResult;

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseOptions(args, out McpToolMode mode, out bool showHelp, out string? parseError))
        {
            await Console.Error.WriteLineAsync(parseError).ConfigureAwait(false);
            return 2;
        }

        if (showHelp)
        {
            await Console.Error.WriteLineAsync("Usage: RoslynSkills.McpServer [--mode full|slim]").ConfigureAwait(false);
            return 0;
        }

        ActiveMode = mode;
        ICommandRegistry registry = DefaultRegistryFactory.Create();
        Router = HotCommandRouter.IsEnabled() ? new HotCommandRouter(registry) : null;
        ToolMap toolMap = BuildToolMap(registry.ListCommands());

        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        BufferedMessageReader messageReader = new(input);
        List<Task> inFlight = new();

        try
        {
            while (true)
            {
                PruneCompletedTasks(inFlight);

                JsonDocument? request;
                try
                {
                    request = await messageReader.ReadMessageAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await WriteErrorResponseAsync(
                        output,
                        idElement: null,
                        code: -32700,
                        message: "Parse error.",
                        dataText: ex.Message).ConfigureAwait(false);
                    continue;
                }

                if (request is null)
                {
                    break;
                }

                bool transferredOwnership = false;
                try
                {
                    JsonElement root = request.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        await WriteErrorResponseAsync(
                            output,
                            idElement: null,
                            code: -32600,
                            message: "Invalid request: top-level JSON must be an object.",
                            dataText: null).ConfigureAwait(false);
                        continue;
                    }

                    bool hasResponseId = root.TryGetProperty("id", out JsonElement idElement);

                    if (!TryGetStringProperty(root, "method", out string? method) || string.IsNullOrWhiteSpace(method))
                    {
                        if (hasResponseId)
                        {
                            await WriteErrorResponseAsync(
                                output,
                                idElement,
                                code: -32600,
                                message: "Invalid request: missing method.",
                                dataText: null).ConfigureAwait(false);
                        }

                        continue;
                    }

                    JsonElement @params = GetParams(root);
                    switch (method)
                    {
                        case "initialize":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            string protocolVersion = ResolveProtocolVersion(@params);
                            string instructions = ActiveMode == McpToolMode.Slim
                                ? "Roslyn-backed C# navigation, mindspace context, search, diagnostics, and edits (slim mode: 12 tools). Pass workspace_path (worktree root or .slnx/.sln) when working in a git worktree outside the default CWD. Use roslyn_search for syntax-aware overlay search (code/comments/strings/declarations/invocations), roslyn_context for one-call symbol mindspace (definition+refs+callers+impls+diag), roslyn_nav_* with symbol/symbols or file_path/line/column, roslyn_diag_snapshot for multi-project diagnostics, roslyn_edit_* for semantic/atomic edits with SHA-256 preconditions, and roslyn_run for any other RoslynSkills command (ctx.read_window, ctx.file_outline, edit.change_signature, nav.call_path, analyze.impact_slice, query.batch, workspace.*, daemon.list, daemon.stop)."
                                : "Roslyn-backed C# navigation, diagnostics, and edits. Tool names map from command ids via roslyn_<command_id_with_underscores>. Resource template invocation is available via roslyn://command/{commandId}.";
                            await WriteInitializeResponseAsync(output, idElement, protocolVersion, instructions).ConfigureAwait(false);
                            break;
                        }

                        case "notifications/initialized":
                            break;

                        case "tools/list":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            ReadOnlyMemory<byte> rawResult = GetOrBuildToolListResult(toolMap);
                            await WriteRawResultResponseAsync(output, idElement, rawResult).ConfigureAwait(false);
                            break;
                        }

                        case "tools/call":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            if (IsConcurrentReadOnlyToolCall(@params, registry, toolMap))
                            {
                                JsonDocument ownedRequest = request;
                                transferredOwnership = true;
                                Task task = Task.Run(async () =>
                                {
                                    using (ownedRequest)
                                    {
                                        JsonElement taskRoot = ownedRequest.RootElement;
                                        taskRoot.TryGetProperty("id", out JsonElement taskId);
                                        JsonElement taskParams = GetParams(taskRoot);
                                        await HandleToolCallAsync(output, taskId, taskParams, registry, toolMap).ConfigureAwait(false);
                                    }
                                });
                                inFlight.Add(task);
                            }
                            else
                            {
                                await DrainInFlightAsync(inFlight).ConfigureAwait(false);
                                await HandleToolCallAsync(output, idElement, @params, registry, toolMap).ConfigureAwait(false);
                            }

                            break;
                        }

                        case "resources/list":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            ReadOnlyMemory<byte> rawResult = GetOrBuildResourceListResult(toolMap);
                            await WriteRawResultResponseAsync(output, idElement, rawResult).ConfigureAwait(false);
                            break;
                        }

                        case "resources/templates/list":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            ReadOnlyMemory<byte> rawResult = GetOrBuildResourceTemplateListResult();
                            await WriteRawResultResponseAsync(output, idElement, rawResult).ConfigureAwait(false);
                            break;
                        }

                        case "resources/read":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            await DrainInFlightAsync(inFlight).ConfigureAwait(false);
                            await HandleResourceReadAsync(output, idElement, @params, registry, toolMap).ConfigureAwait(false);
                            break;
                        }

                        case "ping":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            await WriteEmptyResultResponseAsync(output, idElement).ConfigureAwait(false);
                            break;
                        }

                        default:
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            await WriteErrorResponseAsync(
                                output,
                                idElement,
                                code: -32601,
                                message: $"Method not found: {method}",
                                dataText: null).ConfigureAwait(false);
                            break;
                        }
                    }
                }
                finally
                {
                    if (!transferredOwnership)
                    {
                        request.Dispose();
                    }
                }
            }

            await DrainInFlightAsync(inFlight).ConfigureAwait(false);
        }
        finally
        {
            if (Router is not null)
            {
                await Router.DisposeAsync().ConfigureAwait(false);
                Router = null;
            }
        }

        return 0;
    }

    private static void PruneCompletedTasks(List<Task> inFlight)
    {
        for (int i = inFlight.Count - 1; i >= 0; i--)
        {
            if (inFlight[i].IsCompleted)
            {
                inFlight.RemoveAt(i);
            }
        }
    }

    private static async Task DrainInFlightAsync(List<Task> inFlight)
    {
        if (inFlight.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"roslynskills-mcp: background request error: {ex.Message}");
        }
        finally
        {
            inFlight.Clear();
        }
    }

    private static bool IsConcurrentReadOnlyToolCall(JsonElement @params, ICommandRegistry registry, ToolMap toolMap)
    {
        if (@params.ValueKind != JsonValueKind.Object ||
            !TryGetStringProperty(@params, "name", out string? toolName) ||
            string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        JsonElement arguments = @params.TryGetProperty("arguments", out JsonElement argsElem)
            ? argsElem
            : EmptyObject;

        string? resolvedCommandId;
        if (string.Equals(toolName, "roslyn_run", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !TryGetStringProperty(arguments, "command_id", out string? requestedId) ||
                string.IsNullOrWhiteSpace(requestedId))
            {
                return false;
            }

            resolvedCommandId = requestedId.Trim();
        }
        else if (TryResolveSlimToolCommandId(toolName, arguments, out string? slimId))
        {
            resolvedCommandId = slimId;
        }
        else if (ActiveMode == McpToolMode.Full && toolMap.ByToolName.TryGetValue(toolName, out ToolBinding? binding))
        {
            resolvedCommandId = binding.CommandId;
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(resolvedCommandId) ||
            HotCommandRouter.IsWorkspaceLifecycleCommand(resolvedCommandId))
        {
            return false;
        }

        return registry.TryGet(resolvedCommandId, out IAgentCommand? command) &&
               command is not null &&
               !command.Descriptor.MutatesState;
    }

    private static ReadOnlyMemory<byte> GetOrBuildToolListResult(ToolMap toolMap)
    {
        if (!CachedToolListResult.IsEmpty)
        {
            return CachedToolListResult;
        }

        JsonObject result = new()
        {
            ["tools"] = ActiveMode == McpToolMode.Slim
                ? BuildSlimToolList()
                : BuildToolList(toolMap),
        };
        CachedToolListResult = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        return CachedToolListResult;
    }

    private static ReadOnlyMemory<byte> GetOrBuildResourceListResult(ToolMap toolMap)
    {
        if (!CachedResourceListResult.IsEmpty)
        {
            return CachedResourceListResult;
        }

        JsonObject result = new()
        {
            ["resources"] = BuildResourceList(toolMap),
        };
        CachedResourceListResult = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        return CachedResourceListResult;
    }

    private static ReadOnlyMemory<byte> GetOrBuildResourceTemplateListResult()
    {
        if (!CachedResourceTemplateListResult.IsEmpty)
        {
            return CachedResourceTemplateListResult;
        }

        JsonObject result = new()
        {
            ["resourceTemplates"] = BuildResourceTemplateList(),
        };
        CachedResourceTemplateListResult = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        return CachedResourceTemplateListResult;
    }

    private static bool TryParseOptions(string[] args, out McpToolMode mode, out bool showHelp, out string? error)
    {
        mode = ParseModeFromEnvironment();
        showHelp = false;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--help" or "-h")
            {
                showHelp = true;
                return true;
            }

            string? value = null;
            int eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (value is null)
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "Option '--mode' requires a value ('full' or 'slim').";
                        return false;
                    }

                    value = args[++i];
                }

                if (!TryParseModeValue(value, out mode))
                {
                    error = $"Unsupported --mode value '{value}'. Supported values: full, slim.";
                    return false;
                }

                continue;
            }

            error = $"Unknown option '{args[i]}'. Usage: RoslynSkills.McpServer [--mode full|slim]";
            return false;
        }

        return true;
    }

    private static McpToolMode ParseModeFromEnvironment()
    {
        string? raw = Environment.GetEnvironmentVariable("ROSLYNSKILLS_MCP_MODE");
        return !string.IsNullOrWhiteSpace(raw) && TryParseModeValue(raw, out McpToolMode parsed)
            ? parsed
            : McpToolMode.Full;
    }

    private static bool TryParseModeValue(string raw, out McpToolMode mode)
    {
        string trimmed = raw.Trim();
        if (string.Equals(trimmed, "slim", StringComparison.OrdinalIgnoreCase))
        {
            mode = McpToolMode.Slim;
            return true;
        }

        if (string.Equals(trimmed, "full", StringComparison.OrdinalIgnoreCase))
        {
            mode = McpToolMode.Full;
            return true;
        }

        mode = McpToolMode.Full;
        return false;
    }

    private static string GetServerVersion()
    {
        Assembly assembly = typeof(Program).Assembly;
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? info[..plus] : info;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.1.6";
    }

    private static async Task HandleToolCallAsync(
        Stream output,
        JsonElement idElement,
        JsonElement @params,
        ICommandRegistry registry,
        ToolMap toolMap)
    {
        if (@params.ValueKind != JsonValueKind.Object)
        {
            await WriteErrorResponseAsync(
                output,
                idElement,
                code: -32602,
                message: "Invalid params: tools/call expects an object.",
                dataText: null).ConfigureAwait(false);
            return;
        }

        if (!TryGetStringProperty(@params, "name", out string? toolName) || string.IsNullOrWhiteSpace(toolName))
        {
            await WriteErrorResponseAsync(
                output,
                idElement,
                code: -32602,
                message: "Invalid params: tools/call requires a non-empty tool name.",
                dataText: null).ConfigureAwait(false);
            return;
        }

        JsonElement arguments = GetArguments(@params);
        string resolvedCommandId;
        JsonElement commandInput;

        if (string.Equals(toolName, "roslyn_run", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !TryGetStringProperty(arguments, "command_id", out string? requestedCommandId) ||
                string.IsNullOrWhiteSpace(requestedCommandId))
            {
                await WriteErrorResponseAsync(
                    output,
                    idElement,
                    code: -32602,
                    message: "Invalid params: roslyn_run requires a non-empty string 'command_id'.",
                    dataText: null).ConfigureAwait(false);
                return;
            }

            resolvedCommandId = requestedCommandId.Trim();
            if (arguments.TryGetProperty("input", out JsonElement nestedInput))
            {
                if (nestedInput.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    await WriteErrorResponseAsync(
                        output,
                        idElement,
                        code: -32602,
                        message: "Invalid params: roslyn_run 'input' must be an object.",
                        dataText: null).ConfigureAwait(false);
                    return;
                }

                commandInput = nestedInput.ValueKind == JsonValueKind.Object
                    ? nestedInput
                    : EmptyObject;
            }
            else
            {
                commandInput = EmptyObject;
            }
        }
        else if (TryResolveSlimToolCommandId(toolName, arguments, out string? slimCommandId))
        {
            resolvedCommandId = slimCommandId!;
            commandInput = arguments;
        }
        else if (ActiveMode == McpToolMode.Full && toolMap.ByToolName.TryGetValue(toolName, out ToolBinding? binding))
        {
            resolvedCommandId = binding.CommandId;
            commandInput = arguments;
        }
        else
        {
            string hint = ActiveMode == McpToolMode.Slim
                ? $"Unknown tool '{toolName}'. In slim mode call one of the 12 exposed tools or use 'roslyn_run' with command_id."
                : $"Unknown tool '{toolName}'.";
            await WriteErrorResponseAsync(
                output,
                idElement,
                code: -32602,
                message: hint,
                dataText: null).ConfigureAwait(false);
            return;
        }

        if (HotCommandRouter.IsDaemonManagementCommand(resolvedCommandId))
        {
            CommandEnvelope daemonEnvelope = Router is not null
                ? await Router.RunDaemonManagementCommandAsync(resolvedCommandId, commandInput, CancellationToken.None).ConfigureAwait(false)
                : await HotCommandRouter.RunDaemonManagementStandaloneAsync(resolvedCommandId, commandInput, CancellationToken.None).ConfigureAwait(false);
            await WriteToolEnvelopeResultAsync(output, idElement, daemonEnvelope).ConfigureAwait(false);
            return;
        }

        string lookupCommandId = string.Equals(resolvedCommandId, "workspace.use", StringComparison.OrdinalIgnoreCase)
            ? "workspace.preload"
            : resolvedCommandId;
        if (!registry.TryGet(lookupCommandId, out IAgentCommand? command) || command is null)
        {
            CommandEnvelope notFoundEnvelope = new(
                Ok: false,
                CommandId: resolvedCommandId,
                Version: CommandEnvelopeVersion,
                Data: null,
                Errors: new[] { new CommandError("command_not_found", $"Command '{resolvedCommandId}' was not found.") },
                TraceId: null,
                Preview: $"{resolvedCommandId} failed",
                Summary: $"Command '{resolvedCommandId}' was not found.",
                Transport: new CommandTransportMetadata(Mode: "mcp"));
            await WriteToolEnvelopeResultAsync(output, idElement, notFoundEnvelope).ConfigureAwait(false);
            return;
        }

        CommandEnvelope envelope = await ExecuteCommandAsync(command, resolvedCommandId, commandInput).ConfigureAwait(false);
        await WriteToolEnvelopeResultAsync(output, idElement, envelope).ConfigureAwait(false);
    }

    private static async Task WriteToolEnvelopeResultAsync(Stream output, JsonElement idElement, CommandEnvelope envelope)
    {
        string resultText = BuildToolResultText(envelope);
        await WriteUtf8MessageAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, JsonRpcVersion);
            writer.WritePropertyName("id"u8);
            idElement.WriteTo(writer);
            writer.WriteStartObject("result"u8);
            writer.WriteStartArray("content"u8);
            writer.WriteStartObject();
            writer.WriteString("type"u8, "text"u8);
            writer.WriteString("text"u8, resultText);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WritePropertyName("structuredContent"u8);
            JsonSerializer.Serialize(writer, envelope, JsonOptions);
            writer.WriteBoolean("isError"u8, !envelope.Ok);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private static bool TryResolveSlimToolCommandId(string toolName, JsonElement arguments, out string? commandId)
    {
        switch (toolName.ToLowerInvariant())
        {
            case "roslyn_search":
                commandId = "ctx.search_text";
                return true;

            case "roslyn_context":
                commandId = "ctx.symbol_context";
                return true;

            case "roslyn_nav_symbol":
                // Support both solution-wide declaration jump (nav.go_to_definition via symbol or file/line/col)
                // and file-scoped identifier lookup (nav.find_symbol via file_path + symbol_name without line).
                bool hasFilePath = TryGetStringProperty(arguments, "file_path", out string? fp) && !string.IsNullOrWhiteSpace(fp);
                bool hasSymbolName = TryGetStringProperty(arguments, "symbol_name", out string? sn) && !string.IsNullOrWhiteSpace(sn);
                bool hasLine = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("line", out _);
                commandId = hasFilePath && hasSymbolName && !hasLine
                    ? "nav.find_symbol"
                    : "nav.go_to_definition";
                return true;

            case "roslyn_nav_references":
                commandId = "nav.find_references";
                return true;

            case "roslyn_nav_invocations":
                commandId = "nav.find_invocations";
                return true;

            case "roslyn_nav_call_hierarchy":
                commandId = "nav.call_hierarchy";
                return true;

            case "roslyn_nav_implementations":
                commandId = "nav.find_implementations";
                return true;

            case "roslyn_diag_snapshot":
                commandId = "diag.get_workspace_snapshot";
                return true;

            case "roslyn_edit_rename":
                commandId = "edit.rename_symbol";
                return true;

            case "roslyn_edit_transaction":
                commandId = "edit.transaction";
                return true;

            case "roslyn_edit_code_fix":
                commandId = "edit.apply_code_fix";
                return true;

            default:
                commandId = null;
                return false;
        }
    }

    private static async Task HandleResourceReadAsync(
        Stream output,
        JsonElement idElement,
        JsonElement @params,
        ICommandRegistry registry,
        ToolMap toolMap)
    {
        if (@params.ValueKind != JsonValueKind.Object)
        {
            await WriteErrorResponseAsync(
                output,
                idElement,
                code: -32602,
                message: "Invalid params: resources/read expects an object.",
                dataText: null).ConfigureAwait(false);
            return;
        }

        if (!TryGetStringProperty(@params, "uri", out string? uriString) || string.IsNullOrWhiteSpace(uriString))
        {
            await WriteErrorResponseAsync(
                output,
                idElement,
                code: -32602,
                message: "Invalid params: resources/read requires a non-empty uri.",
                dataText: null).ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out Uri? uri))
        {
            await WriteErrorResponseAsync(
                output,
                idElement,
                code: -32602,
                message: $"Invalid resource URI '{uriString}'.",
                dataText: null).ConfigureAwait(false);
            return;
        }

        if (!uri.Scheme.Equals(ResourceScheme, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorResponseAsync(
                output,
                idElement,
                code: -32602,
                message: $"Unsupported resource scheme '{uri.Scheme}'.",
                dataText: null).ConfigureAwait(false);
            return;
        }

        bool isCommandsCatalog =
            uri.Host.Equals("commands", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/");
        if (isCommandsCatalog ||
            uri.AbsoluteUri.Equals(CommandsCatalogResourceUri, StringComparison.OrdinalIgnoreCase))
        {
            JsonNode payload = BuildCommandsCatalog(toolMap);
            string text = JsonSerializer.Serialize(payload, JsonOptions);
            await WriteResourceTextResultAsync(output, idElement, CommandsCatalogResourceUri, "application/json", text).ConfigureAwait(false);
            return;
        }

        if (uri.Host.Equals("command-meta", StringComparison.OrdinalIgnoreCase))
        {
            string commandId = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (string.IsNullOrWhiteSpace(commandId) || !toolMap.ByCommandId.TryGetValue(commandId, out ToolBinding? metaBinding))
            {
                await WriteErrorResponseAsync(
                    output,
                    idElement,
                    code: -32602,
                    message: $"Unknown command metadata resource '{uri.AbsoluteUri}'.",
                    dataText: null).ConfigureAwait(false);
                return;
            }

            JsonObject payload = new()
            {
                ["commandId"] = metaBinding.CommandId,
                ["toolName"] = metaBinding.ToolName,
                ["summary"] = metaBinding.Summary,
                ["inputSchemaVersion"] = metaBinding.InputSchemaVersion,
                ["mutatesState"] = metaBinding.MutatesState,
                ["maturity"] = metaBinding.Maturity,
                ["traits"] = ToJsonArray(metaBinding.Traits),
                ["invocationTemplate"] = CommandResourceTemplate,
            };

            string text = JsonSerializer.Serialize(payload, JsonOptions);
            await WriteResourceTextResultAsync(output, idElement, uri.AbsoluteUri, "application/json", text).ConfigureAwait(false);
            return;
        }

        if (uri.Host.Equals("command", StringComparison.OrdinalIgnoreCase))
        {
            string commandId = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (string.IsNullOrWhiteSpace(commandId) || !toolMap.ByCommandId.TryGetValue(commandId, out ToolBinding? binding))
            {
                await WriteErrorResponseAsync(
                    output,
                    idElement,
                    code: -32602,
                    message: $"Unknown command resource '{uri.AbsoluteUri}'.",
                    dataText: null).ConfigureAwait(false);
                return;
            }

            if (!registry.TryGet(binding.CommandId, out IAgentCommand? command) || command is null)
            {
                await WriteErrorResponseAsync(
                    output,
                    idElement,
                    code: -32603,
                    message: $"Command '{binding.CommandId}' is unavailable.",
                    dataText: null).ConfigureAwait(false);
                return;
            }

            if (!TryBuildResourceCommandInput(uri, out JsonElement input, out string? inputError))
            {
                await WriteErrorResponseAsync(
                    output,
                    idElement,
                    code: -32602,
                    message: inputError ?? "Failed to parse command input from resource URI.",
                    dataText: null).ConfigureAwait(false);
                return;
            }

            CommandEnvelope envelope = await ExecuteCommandAsync(command, binding.CommandId, input).ConfigureAwait(false);

            string text = JsonSerializer.Serialize(envelope, JsonOptions);
            await WriteResourceTextResultAsync(output, idElement, uri.AbsoluteUri, "application/json", text).ConfigureAwait(false);
            return;
        }

        await WriteErrorResponseAsync(
            output,
            idElement,
            code: -32601,
            message: $"Unknown resource URI '{uri.AbsoluteUri}'.",
            dataText: null).ConfigureAwait(false);
    }

    private static async Task<CommandEnvelope> ExecuteCommandAsync(
        IAgentCommand command,
        string commandId,
        JsonElement input)
    {
        Stopwatch totalTimer = Stopwatch.StartNew();
        if (Router is not null && HotCommandRouter.ShouldRoute(command))
        {
            // Route hot-workspace and workspace-lifecycle commands directly over the persistent WorkspaceHost pipe.
            try
            {
                CommandEnvelope? routed = await Router.TryRunAsync(command, commandId, input, CancellationToken.None).ConfigureAwait(false);
                if (routed is not null)
                {
                    return routed;
                }
            }
            catch (Exception ex) when (!command.Descriptor.MutatesState)
            {
                Console.Error.WriteLine($"roslynskills-mcp: hot routing failed for {commandId}, running in-process: {ex.Message}");
            }
        }

        try
        {
            JsonElement inProcessInput = HotCommandRouter.ResolveRelativePathsAgainstWorkspace(input);
            Stopwatch validateTimer = Stopwatch.StartNew();
            IReadOnlyList<CommandError> validationErrors = command.Validate(inProcessInput);
            validateTimer.Stop();
            if (validationErrors.Count > 0)
            {
                totalTimer.Stop();
                return new CommandEnvelope(
                    Ok: false,
                    CommandId: commandId,
                    Version: CommandEnvelopeVersion,
                    Data: null,
                    Errors: validationErrors,
                    TraceId: null,
                    Preview: null,
                    Summary: "Command validation failed.",
                    Transport: new CommandTransportMetadata(
                        Mode: "mcp",
                        Timings: new CommandTransportTimings(
                            ValidateMs: Math.Round(validateTimer.Elapsed.TotalMilliseconds, 3),
                            TotalMs: Math.Round(totalTimer.Elapsed.TotalMilliseconds, 3))));
            }

            Stopwatch executeTimer = Stopwatch.StartNew();
            CommandExecutionResult result = await command.ExecuteAsync(inProcessInput, CancellationToken.None).ConfigureAwait(false);
            executeTimer.Stop();
            totalTimer.Stop();
            return new CommandEnvelope(
                Ok: result.Ok,
                CommandId: commandId,
                Version: CommandEnvelopeVersion,
                Data: result.Data,
                Errors: result.Errors,
                TraceId: null,
                Telemetry: result.Telemetry,
                Preview: null,
                Summary: result.Ok ? "Command executed successfully." : "Command returned one or more errors.",
                Transport: new CommandTransportMetadata(
                    Mode: "mcp",
                    Timings: new CommandTransportTimings(
                        ValidateMs: Math.Round(validateTimer.Elapsed.TotalMilliseconds, 3),
                        ExecuteMs: Math.Round(executeTimer.Elapsed.TotalMilliseconds, 3),
                        TotalMs: Math.Round(totalTimer.Elapsed.TotalMilliseconds, 3))));
        }
        catch (Exception ex)
        {
            totalTimer.Stop();
            return new CommandEnvelope(
                Ok: false,
                CommandId: commandId,
                Version: CommandEnvelopeVersion,
                Data: null,
                Errors: new[]
                {
                    new CommandError("command_exception", ex.Message),
                },
                TraceId: null,
                Preview: null,
                Summary: "Command execution threw an exception.",
                Transport: new CommandTransportMetadata(
                    Mode: "mcp",
                    Timings: new CommandTransportTimings(
                        TotalMs: Math.Round(totalTimer.Elapsed.TotalMilliseconds, 3))));
        }
    }

    private static string BuildToolResultText(CommandEnvelope envelope)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Summary))
        {
            return $"{envelope.CommandId}: {envelope.Summary}";
        }

        if (envelope.Ok)
        {
            return $"{envelope.CommandId}: ok";
        }

        if (envelope.Errors.Count > 0)
        {
            return $"{envelope.CommandId}: {envelope.Errors[0].Code} - {envelope.Errors[0].Message}";
        }

        return $"{envelope.CommandId}: failed";
    }

    private static JsonArray BuildSlimToolList()
    {
        // Project every slim tool's JSON Schema from the single descriptor-driven CommandInputSchemaCatalog
        // while keeping tools/list compact (~5-6 KB for all 12 tools).
        static JsonObject SlimTool(
            string name,
            string desc,
            bool readOnly,
            string backingCommandId,
            string[] allowedParams,
            string[]? requiredOverride = null,
            Dictionary<string, string>? shortDescs = null,
            Dictionary<string, IReadOnlySet<string>>? allowedNested = null,
            Action<JsonObject>? extraProps = null)
        {
            CommandInputSchema schema = CommandInputSchemaCatalog.GetSchema(backingCommandId)
                ?? throw new InvalidOperationException($"Missing descriptor input schema for '{backingCommandId}'.");

            HashSet<string> allowed = new(allowedParams, StringComparer.OrdinalIgnoreCase);
            HashSet<string>? requiredSet = requiredOverride is null
                ? null
                : new HashSet<string>(requiredOverride, StringComparer.OrdinalIgnoreCase);

            JsonObject inputSchema = CommandInputSchemaCatalog.ToJsonSchemaObject(
                schema,
                description: null,
                includeDescriptions: false,
                allowedParameters: allowed,
                descriptionOverrides: shortDescs,
                requiredOverrides: requiredSet,
                additionalPropertiesOverride: null,
                allowedItemProperties: allowedNested);

            if (extraProps is not null && inputSchema["properties"] is JsonObject propsObj)
            {
                extraProps(propsObj);
            }

            return new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = inputSchema,
                ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly },
            };
        }

        static JsonObject SlimRawTool(string name, string desc, bool readOnly, JsonObject props, params string[] req)
        {
            JsonObject schema = new()
            {
                ["type"] = "object",
                ["properties"] = props,
            };
            if (req.Length > 0)
            {
                JsonArray required = new();
                foreach (string r in req) required.Add(r);
                schema["required"] = required;
            }

            return new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = schema,
                ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly },
            };
        }

        JsonObject runGeneric = new()
        {
            ["command_id"] = new JsonObject { ["type"] = "string", ["description"] = "ctx.read_window, ctx.file_outline, edit.change_signature, nav.call_path, analyze.impact_slice, query.batch, workspace.*, daemon.list, daemon.stop" },
            ["input"] = new JsonObject { ["type"] = "object" },
        };

        Dictionary<string, IReadOnlySet<string>> txSlimNested = new(StringComparer.OrdinalIgnoreCase)
        {
            ["operations"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "kind", "file_path", "expected_sha256", "old_text", "new_text", "anchor_text", "insert_text", "new_content",
            },
        };

        return new JsonArray
        {
            SlimTool(
                "roslyn_search",
                "Zero-disk overlay search with AST syntax_filter and batched queries.",
                readOnly: true,
                "ctx.search_text",
                ["pattern", "patterns", "syntax_filter", "whole_word", "mode", "file_path", "roots", "workspace_path", "max_results", "brief"],
                requiredOverride: []),
            SlimTool(
                "roslyn_context",
                "One-call symbol mindspace: def + refs + interface callers + impls + diag.",
                readOnly: true,
                "ctx.symbol_context",
                ["symbol", "file_path", "line", "column", "workspace_path", "include_definition", "include_references", "include_callers", "include_callees", "include_implementations", "include_diagnostics", "brief"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_symbol",
                "Go to definition or find symbol_name in file.",
                readOnly: true,
                "nav.go_to_definition",
                ["symbol", "file_path", "line", "column", "workspace_path", "brief"],
                requiredOverride: [],
                extraProps: static p =>
                {
                    p["symbol_name"] = new JsonObject { ["type"] = "string" };
                    p["max_results"] = new JsonObject { ["type"] = "integer" };
                }),
            SlimTool(
                "roslyn_nav_references",
                "Solution-wide references (single symbol or symbols batch).",
                readOnly: true,
                "nav.find_references",
                ["symbol", "symbols", "file_path", "line", "column", "workspace_path", "include_declaration", "max_results"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_invocations",
                "Call sites and creations (single symbol or symbols batch).",
                readOnly: true,
                "nav.find_invocations",
                ["symbol", "symbols", "file_path", "line", "column", "workspace_path", "brief", "max_results", "include_object_creations"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_call_hierarchy",
                "Call graph (default max_depth=1; interface callers included).",
                readOnly: true,
                "nav.call_hierarchy",
                ["symbol", "file_path", "line", "column", "workspace_path", "direction", "max_depth", "max_nodes", "max_edges", "brief"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_implementations",
                "Implementing types/members and derived interfaces.",
                readOnly: true,
                "nav.find_implementations",
                ["symbol", "file_path", "line", "column", "workspace_path", "max_results"],
                requiredOverride: []),
            SlimTool(
                "roslyn_diag_snapshot",
                "Multi-project Roslyn compiler diagnostics snapshot.",
                readOnly: true,
                "diag.get_workspace_snapshot",
                ["directory_path", "workspace_path", "file_paths", "mode", "brief", "include_diagnostics", "severity_filter", "max_diagnostics", "require_workspace"],
                requiredOverride: []),
            SlimTool(
                "roslyn_edit_rename",
                "Solution-wide rename by symbol or file/line/col (with XML crefs).",
                readOnly: false,
                "edit.rename_symbol",
                ["symbol", "file_path", "line", "column", "workspace_path", "new_name", "apply", "max_diagnostics"],
                requiredOverride: ["new_name"]),
            SlimTool(
                "roslyn_edit_transaction",
                "Atomic multi-file edits with SHA-256 preconditions and diagnostics.",
                readOnly: false,
                "edit.transaction",
                ["operations", "file_path", "workspace_path", "expected_sha256", "expected_file_hashes", "apply", "atomic", "include_diagnostics"],
                requiredOverride: ["operations"],
                allowedNested: txSlimNested),
            SlimTool(
                "roslyn_edit_code_fix",
                "Discover or apply Roslyn CodeFixProvider fixes.",
                readOnly: false,
                "edit.apply_code_fix",
                ["file_path", "workspace_path", "diagnostic_id", "line", "column", "fix_title", "apply", "max_diagnostics"],
                requiredOverride: ["file_path"]),
            SlimRawTool("roslyn_run", "Run any RoslynSkills command by command_id.", readOnly: false, runGeneric, "command_id"),
        };
    }

    private static JsonArray BuildToolList(ToolMap toolMap)
    {
        JsonArray tools = new();
        foreach (ToolBinding binding in toolMap.OrderedBindings)
        {
            JsonObject inputSchema = BuildInputSchema(binding);

            JsonObject tool = new()
            {
                ["name"] = binding.ToolName,
                ["description"] = binding.Summary,
                ["inputSchema"] = inputSchema,
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = !binding.MutatesState,
                    ["maturityHint"] = binding.Maturity,
                    ["traits"] = ToJsonArray(binding.Traits),
                },
            };

            tools.Add(tool);
        }

        return tools;
    }

    private static JsonObject BuildInputSchema(ToolBinding binding)
    {
        CommandInputSchema? schema = binding.InputSchema ?? CommandInputSchemaCatalog.GetSchema(binding.CommandId);
        string description = $"Arguments for command '{binding.CommandId}'. Schema version: {binding.InputSchemaVersion}.";
        if (schema is not null)
        {
            return CommandInputSchemaCatalog.ToJsonSchemaObject(
                schema,
                description: description,
                includeDescriptions: true);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = true,
            ["description"] = description,
        };
    }

    private static JsonArray BuildResourceList(ToolMap toolMap)
    {
        JsonArray resources = new()
        {
            new JsonObject
            {
                ["uri"] = CommandsCatalogResourceUri,
                ["name"] = "roslyn_commands",
                ["description"] = "Roslyn command catalog and URI invocation guidance.",
                ["mimeType"] = "application/json",
            },
        };

        foreach (ToolBinding binding in toolMap.OrderedBindings)
        {
            resources.Add(new JsonObject
            {
                ["uri"] = $"roslyn://command-meta/{Uri.EscapeDataString(binding.CommandId)}",
                ["name"] = $"roslyn_command_meta_{SanitizeCommandId(binding.CommandId)}",
                ["description"] = binding.Summary,
                ["mimeType"] = "application/json",
            });
        }

        return resources;
    }

    private static JsonArray BuildResourceTemplateList()
    {
        JsonArray templates = new()
        {
            new JsonObject
            {
                ["uriTemplate"] = CommandResourceTemplate,
                ["name"] = "roslyn_command_query",
                ["description"] = "Invoke a Roslyn command via query parameters. Example: roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true",
                ["mimeType"] = "application/json",
            },
            new JsonObject
            {
                ["uriTemplate"] = CommandResourceTemplateInputJson,
                ["name"] = "roslyn_command_input_json",
                ["description"] = "Invoke a Roslyn command using URL-encoded JSON payload in input_json.",
                ["mimeType"] = "application/json",
            },
            new JsonObject
            {
                ["uriTemplate"] = CommandResourceTemplateInputBase64,
                ["name"] = "roslyn_command_input_b64",
                ["description"] = "Invoke a Roslyn command using base64url-encoded UTF-8 JSON payload in input_b64.",
                ["mimeType"] = "application/json",
            },
        };

        return templates;
    }

    private static ToolMap BuildToolMap(IReadOnlyList<CommandDescriptor> descriptors)
    {
        List<ToolBinding> bindings = new(descriptors.Count);
        Dictionary<string, ToolBinding> byToolName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ToolBinding> byCommandId = new(StringComparer.OrdinalIgnoreCase);

        foreach (CommandDescriptor descriptor in descriptors.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
        {
            string baseName = ToolPrefix + SanitizeCommandId(descriptor.Id);
            string toolName = baseName;
            int suffix = 2;
            while (byToolName.ContainsKey(toolName))
            {
                toolName = $"{baseName}_{suffix}";
                suffix++;
            }

            ToolBinding binding = new(
                ToolName: toolName,
                CommandId: descriptor.Id,
                Summary: $"{descriptor.Summary} (command: {descriptor.Id})",
                InputSchemaVersion: descriptor.InputSchemaVersion,
                MutatesState: descriptor.MutatesState,
                Maturity: descriptor.Maturity,
                Traits: descriptor.Traits ?? Array.Empty<string>(),
                InputSchema: descriptor.InputSchema);
            bindings.Add(binding);
            byToolName[toolName] = binding;
            byCommandId[descriptor.Id] = binding;
        }

        return new ToolMap(bindings, byToolName, byCommandId);
    }

    private static JsonNode BuildCommandsCatalog(ToolMap toolMap)
    {
        JsonArray commands = new();
        foreach (ToolBinding binding in toolMap.OrderedBindings)
        {
            commands.Add(new JsonObject
            {
                ["commandId"] = binding.CommandId,
                ["toolName"] = binding.ToolName,
                ["summary"] = binding.Summary,
                ["mutatesState"] = binding.MutatesState,
                ["maturity"] = binding.Maturity,
                ["traits"] = ToJsonArray(binding.Traits),
                ["inputSchemaVersion"] = binding.InputSchemaVersion,
            });
        }

        return new JsonObject
        {
            ["version"] = CommandEnvelopeVersion,
            ["invocationTemplate"] = CommandResourceTemplate,
            ["invocationExamples"] = new JsonArray
            {
                "roslyn://command/nav.find_symbol?file_path=Target.cs&symbol_name=Process&brief=true",
                "roslyn://command/nav.find_symbol_batch?input_json=%7B%22queries%22%3A%5B%7B%22file_path%22%3A%22Target.cs%22%2C%22symbol_name%22%3A%22Process%22%7D%5D%2C%22first_declaration%22%3Atrue%7D",
                "roslyn://command/ctx.search_text?pattern=RemoteUserAction&roots=src&mode=literal&max_results=100",
                "roslyn://command/ctx.file_outline?file_path=Target.vb&include_members=true&max_members=80",
                "roslyn://command/ctx.member_source?file_path=Target.vb&line=22&column=17&mode=body&brief=true",
                "roslyn://command/nav.go_to_definition?file_path=Target.cs&line=12&column=15&brief=true",
                "roslyn://command/nav.find_invocations?file_path=Target.cs&line=12&column=15&brief=true",
                "roslyn://command/nav.call_hierarchy?file_path=Target.cs&line=12&column=15&direction=both&max_depth=1&brief=true",
                "roslyn://command/nav.call_path?source_file_path=Caller.cs&source_line=12&source_column=15&target_file_path=Target.cs&target_line=48&target_column=18&max_depth=8&brief=true",
                "roslyn://command/analyze.unused_private_symbols?workspace_path=src&brief=true&max_symbols=100",
                "roslyn://command/analyze.control_flow_graph?file_path=Target.cs&line=12&column=15&brief=true&max_blocks=120&max_edges=260",
                "roslyn://command/analyze.dataflow_slice?file_path=Target.cs&line=12&column=15&brief=true&max_symbols=120",
                "roslyn://command/analyze.impact_slice?file_path=Target.cs&line=12&column=15&include_references=true&include_callers=true&brief=true",
                "roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true",
                "roslyn://command/diag.get_file_diagnostics?file_path=Target.cs",
            },
            ["commands"] = commands,
        };
    }

    private static string SanitizeCommandId(string commandId)
    {
        StringBuilder sb = new(commandId.Length + 8);
        foreach (char c in commandId)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c == '.')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append('_');
            }
        }

        if (sb.Length == 0)
        {
            sb.Append("command");
        }

        return sb.ToString();
    }

    private static JsonElement GetParams(JsonElement root)
    {
        if (root.TryGetProperty("params", out JsonElement @params))
        {
            return @params;
        }

        return EmptyObject;
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        JsonArray array = new();
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonElement GetArguments(JsonElement @params)
    {
        if (@params.TryGetProperty("arguments", out JsonElement arguments))
        {
            return arguments;
        }

        return EmptyObject;
    }

    private static bool TryBuildResourceCommandInput(Uri resourceUri, out JsonElement input, out string? error)
    {
        input = EmptyObject;
        error = null;

        Dictionary<string, List<string>> query = ParseQuery(resourceUri.Query);

        if (TryGetLastQueryValue(query, "input_b64", out string? inputBase64))
        {
            if (!TryDecodeBase64UrlUtf8(inputBase64, out string? jsonText, out string? decodeError))
            {
                error = decodeError ?? "input_b64 must be valid base64url-encoded UTF-8 JSON.";
                return false;
            }

            return TryParseInputJson(jsonText, out input, out error);
        }

        if (TryGetLastQueryValue(query, "input_json", out string? inputJson))
        {
            return TryParseInputJson(inputJson, out input, out error);
        }

        JsonObject queryObject = new();
        foreach ((string key, List<string> values) in query)
        {
            if (values.Count == 0)
            {
                queryObject[key] = JsonValue.Create(string.Empty);
                continue;
            }

            if (values.Count == 1)
            {
                queryObject[key] = ConvertQueryValue(values[0]);
                continue;
            }

            JsonArray array = new();
            foreach (string value in values)
            {
                array.Add(ConvertQueryValue(value));
            }

            queryObject[key] = array;
        }

        input = JsonSerializer.SerializeToElement(queryObject, JsonOptions);
        return true;
    }

    private static bool TryParseInputJson(string jsonText, out JsonElement input, out string? error)
    {
        error = null;
        input = EmptyObject;
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonText);
            input = document.RootElement.Clone();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse JSON input payload: {ex.Message}";
            return false;
        }
    }

    private static Dictionary<string, List<string>> ParseQuery(string query)
    {
        Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return values;
        }

        string trimmed = query[0] == '?' ? query.Substring(1) : query;
        if (trimmed.Length == 0)
        {
            return values;
        }

        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = pair.IndexOf('=');
            string rawKey;
            string rawValue;
            if (equalsIndex >= 0)
            {
                rawKey = pair.Substring(0, equalsIndex);
                rawValue = pair.Substring(equalsIndex + 1);
            }
            else
            {
                rawKey = pair;
                rawValue = string.Empty;
            }

            string key = DecodeQueryComponent(rawKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string value = DecodeQueryComponent(rawValue);
            if (!values.TryGetValue(key, out List<string>? bucket))
            {
                bucket = new List<string>();
                values[key] = bucket;
            }

            bucket.Add(value);
        }

        return values;
    }

    private static bool TryGetLastQueryValue(Dictionary<string, List<string>> query, string key, out string value)
    {
        value = string.Empty;
        if (!query.TryGetValue(key, out List<string>? values) || values.Count == 0)
        {
            return false;
        }

        value = values[values.Count - 1];
        return true;
    }

    private static string DecodeQueryComponent(string value)
    {
        string normalized = value.Replace('+', ' ');
        return Uri.UnescapeDataString(normalized);
    }

    private static bool TryDecodeBase64UrlUtf8(string encoded, out string decoded, out string? error)
    {
        decoded = string.Empty;
        error = null;
        try
        {
            string normalized = encoded.Replace('-', '+').Replace('_', '/');
            int padding = normalized.Length % 4;
            if (padding > 0)
            {
                normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
            }

            byte[] bytes = Convert.FromBase64String(normalized);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to decode base64url payload: {ex.Message}";
            return false;
        }
    }

    private static JsonNode? ConvertQueryValue(string value)
    {
        if (bool.TryParse(value, out bool booleanValue))
        {
            return JsonValue.Create(booleanValue);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
        {
            return JsonValue.Create(doubleValue);
        }

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if ((value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal)) ||
            (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal)))
        {
            try
            {
                JsonNode? parsed = JsonNode.Parse(value);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch
            {
                // Keep original string when query value is not valid JSON.
            }
        }

        return JsonValue.Create(value);
    }

    private static string ResolveProtocolVersion(JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Object &&
            TryGetStringProperty(@params, "protocolVersion", out string? requested) &&
            !string.IsNullOrWhiteSpace(requested))
        {
            return requested;
        }

        return McpProtocolVersion;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return true;
        }

        return false;
    }

    private static Task WriteInitializeResponseAsync(
        Stream output,
        JsonElement idElement,
        string protocolVersion,
        string instructions)
    {
        string version = GetServerVersion();
        return WriteUtf8MessageAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, JsonRpcVersion);
            writer.WritePropertyName("id"u8);
            idElement.WriteTo(writer);
            writer.WriteStartObject("result"u8);
            writer.WriteString("protocolVersion"u8, protocolVersion);
            writer.WriteStartObject("capabilities"u8);
            writer.WriteStartObject("tools"u8);
            writer.WriteBoolean("listChanged"u8, false);
            writer.WriteEndObject();
            writer.WriteStartObject("resources"u8);
            writer.WriteBoolean("subscribe"u8, false);
            writer.WriteBoolean("listChanged"u8, false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject("serverInfo"u8);
            writer.WriteString("name"u8, "roslynskills-mcp"u8);
            writer.WriteString("version"u8, version);
            writer.WriteEndObject();
            writer.WriteString("instructions"u8, instructions);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static Task WriteRawResultResponseAsync(Stream output, JsonElement idElement, ReadOnlyMemory<byte> rawResultUtf8)
    {
        return WriteUtf8MessageAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, JsonRpcVersion);
            writer.WritePropertyName("id"u8);
            idElement.WriteTo(writer);
            writer.WritePropertyName("result"u8);
            writer.WriteRawValue(rawResultUtf8.Span, skipInputValidation: true);
            writer.WriteEndObject();
        });
    }

    private static Task WriteEmptyResultResponseAsync(Stream output, JsonElement idElement)
    {
        return WriteUtf8MessageAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, JsonRpcVersion);
            writer.WritePropertyName("id"u8);
            idElement.WriteTo(writer);
            writer.WriteStartObject("result"u8);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static Task WriteErrorResponseAsync(Stream output, JsonElement? idElement, int code, string message, string? dataText)
    {
        return WriteUtf8MessageAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, JsonRpcVersion);
            if (idElement is { } id)
            {
                writer.WritePropertyName("id"u8);
                id.WriteTo(writer);
            }
            else
            {
                writer.WriteNull("id"u8);
            }

            writer.WriteStartObject("error"u8);
            writer.WriteNumber("code"u8, code);
            writer.WriteString("message"u8, message);
            if (dataText is not null)
            {
                writer.WriteString("data"u8, dataText);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static Task WriteResourceTextResultAsync(
        Stream output,
        JsonElement idElement,
        string uri,
        string mimeType,
        string text)
    {
        return WriteUtf8MessageAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, JsonRpcVersion);
            writer.WritePropertyName("id"u8);
            idElement.WriteTo(writer);
            writer.WriteStartObject("result"u8);
            writer.WriteStartArray("contents"u8);
            writer.WriteStartObject();
            writer.WriteString("uri"u8, uri);
            writer.WriteString("mimeType"u8, mimeType);
            writer.WriteString("text"u8, text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static async Task WriteUtf8MessageAsync(Stream output, Action<Utf8JsonWriter> writePayload)
    {
        await OutputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            OutputBuffer.ResetWrittenCount();
            using (Utf8JsonWriter writer = new(OutputBuffer))
            {
                writePayload(writer);
            }

            Span<byte> newline = OutputBuffer.GetSpan(1);
            newline[0] = (byte)'\n';
            OutputBuffer.Advance(1);

            await output.WriteAsync(OutputBuffer.WrittenMemory).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            OutputGate.Release();
        }
    }

    private sealed class BufferedMessageReader
    {
        private const int BufferSize = 65536;
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[BufferSize];
        private readonly ArrayBufferWriter<byte> _lineScratch = new(4096);
        private int _start;
        private int _end;

        public BufferedMessageReader(Stream stream)
        {
            _stream = stream;
        }

        public async Task<JsonDocument?> ReadMessageAsync(CancellationToken cancellationToken)
        {
            ReadOnlyMemory<byte>? firstLine = await ReadRawLineAsync(cancellationToken).ConfigureAwait(false);
            while (firstLine is { } candidate && candidate.Length == 0)
            {
                firstLine = await ReadRawLineAsync(cancellationToken).ConfigureAwait(false);
            }

            if (firstLine is null)
            {
                return null;
            }

            ReadOnlyMemory<byte> trimmed = TrimStartAsciiWhitespace(firstLine.Value);
            if (trimmed.Length > 0 && (trimmed.Span[0] == (byte)'{' || trimmed.Span[0] == (byte)'['))
            {
                try
                {
                    Utf8JsonReader jsonReader = new(trimmed.Span);
                    return JsonDocument.ParseValue(ref jsonReader);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Invalid newline-delimited JSON message.", ex);
                }
            }

            int contentLength = -1;
            ValidateHeaderLength(firstLine.Value.Length);
            ParseHeaderLine(firstLine.Value.Span, ref contentLength);

            while (true)
            {
                ReadOnlyMemory<byte>? line = await ReadRawLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidDataException("Unexpected EOF while reading headers.");
                }

                if (line.Value.Length == 0)
                {
                    break;
                }

                ValidateHeaderLength(line.Value.Length);
                ParseHeaderLine(line.Value.Span, ref contentLength);
            }

            if (contentLength < 0)
            {
                throw new InvalidDataException("Missing Content-Length header.");
            }

            byte[] body = ArrayPool<byte>.Shared.Rent(contentLength);
            try
            {
                await ReadExactAsync(body.AsMemory(0, contentLength), cancellationToken).ConfigureAwait(false);
                Utf8JsonReader bodyReader = new(body.AsSpan(0, contentLength));
                return JsonDocument.ParseValue(ref bodyReader);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(body);
            }
        }

        private async ValueTask<ReadOnlyMemory<byte>?> ReadRawLineAsync(CancellationToken cancellationToken)
        {
            _lineScratch.ResetWrittenCount();
            bool usingScratch = false;
            bool sawAnyBytes = false;

            while (true)
            {
                if (_start < _end)
                {
                    sawAnyBytes = true;
                    ReadOnlySpan<byte> available = _buffer.AsSpan(_start, _end - _start);
                    int nl = available.IndexOf((byte)'\n');
                    if (nl >= 0)
                    {
                        int lineLen = nl > 0 && available[nl - 1] == (byte)'\r' ? nl - 1 : nl;
                        int lineStart = _start;
                        _start += nl + 1;

                        if (!usingScratch)
                        {
                            return _buffer.AsMemory(lineStart, lineLen);
                        }

                        if (lineLen > 0)
                        {
                            available[..lineLen].CopyTo(_lineScratch.GetSpan(lineLen));
                            _lineScratch.Advance(lineLen);
                        }

                        ReadOnlyMemory<byte> scratchMem = _lineScratch.WrittenMemory;
                        if (scratchMem.Length > 0 && scratchMem.Span[^1] == (byte)'\r')
                        {
                            scratchMem = scratchMem[..^1];
                        }

                        return scratchMem;
                    }

                    usingScratch = true;
                    available.CopyTo(_lineScratch.GetSpan(available.Length));
                    _lineScratch.Advance(available.Length);
                    _start = 0;
                    _end = 0;
                }

                _start = 0;
                _end = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_end == 0)
                {
                    if (!sawAnyBytes)
                    {
                        return null;
                    }

                    if (usingScratch)
                    {
                        ReadOnlyMemory<byte> tail = _lineScratch.WrittenMemory;
                        if (tail.Length > 0 && tail.Span[^1] == (byte)'\r')
                        {
                            tail = tail[..^1];
                        }

                        return tail;
                    }

                    return null;
                }
            }
        }

        private async ValueTask ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            int written = 0;
            if (_start < _end)
            {
                int copy = Math.Min(_end - _start, destination.Length);
                _buffer.AsSpan(_start, copy).CopyTo(destination.Span);
                _start += copy;
                written += copy;
            }

            while (written < destination.Length)
            {
                int read = await _stream.ReadAsync(destination[written..], cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading message body.");
                }

                written += read;
            }
        }

        private static ReadOnlyMemory<byte> TrimStartAsciiWhitespace(ReadOnlyMemory<byte> memory)
        {
            ReadOnlySpan<byte> span = memory.Span;
            int idx = 0;
            while (idx < span.Length && span[idx] is ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                idx++;
            }

            return idx == 0 ? memory : memory[idx..];
        }

        private static void ValidateHeaderLength(int length)
        {
            if (length > MaxHeaderLineLength)
            {
                throw new InvalidDataException("Header line exceeds maximum supported length.");
            }
        }

        private static void ParseHeaderLine(ReadOnlySpan<byte> line, ref int contentLength)
        {
            int separator = line.IndexOf((byte)':');
            if (separator <= 0)
            {
                return;
            }

            string name = Encoding.ASCII.GetString(line[..separator]).Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string rawValue = Encoding.ASCII.GetString(line[(separator + 1)..]).Trim();
            if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
            {
                throw new InvalidDataException($"Invalid Content-Length header value '{rawValue}'.");
            }
        }
    }

    private sealed record ToolBinding(
        string ToolName,
        string CommandId,
        string Summary,
        string InputSchemaVersion,
        bool MutatesState,
        string Maturity,
        IReadOnlyList<string> Traits,
        CommandInputSchema? InputSchema = null);

    private sealed record ToolMap(
        IReadOnlyList<ToolBinding> OrderedBindings,
        IReadOnlyDictionary<string, ToolBinding> ByToolName,
        IReadOnlyDictionary<string, ToolBinding> ByCommandId);
}



