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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static HotCommandRouter? Router;
    private static McpToolMode ActiveMode = McpToolMode.Full;

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

        try
        {
            while (true)
            {
                JsonDocument? request;
                try
                {
                    request = await ReadMessageAsync(input, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await WriteErrorResponseAsync(
                        output,
                        idNode: null,
                        code: -32700,
                        message: "Parse error.",
                        dataNode: JsonValue.Create(ex.Message)).ConfigureAwait(false);
                    continue;
                }

                if (request is null)
                {
                    break;
                }

                using (request)
                {
                    JsonElement root = request.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        await WriteErrorResponseAsync(
                            output,
                            idNode: null,
                            code: -32600,
                            message: "Invalid request: top-level JSON must be an object.",
                            dataNode: null).ConfigureAwait(false);
                        continue;
                    }

                    JsonNode? idNode = TryGetIdNode(root);
                    bool hasResponseId = idNode is not null;

                    if (!TryGetStringProperty(root, "method", out string? method) || string.IsNullOrWhiteSpace(method))
                    {
                        if (hasResponseId)
                        {
                            await WriteErrorResponseAsync(
                                output,
                                idNode,
                                code: -32600,
                                message: "Invalid request: missing method.",
                                dataNode: null).ConfigureAwait(false);
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
                                ? "Roslyn-backed C# navigation, mindspace context, search, diagnostics, and edits (slim mode: 12 tools). Use roslyn_search for syntax-aware overlay search (code/comments/strings/declarations/invocations), roslyn_context for one-call symbol mindspace (definition+refs+callers+impls+diag), roslyn_nav_* with symbol/symbols or file_path/line/column, roslyn_diag_snapshot for multi-project diagnostics, roslyn_edit_* for semantic/atomic edits with SHA-256 preconditions, and roslyn_run for any other RoslynSkills command (ctx.read_window, ctx.file_outline, edit.change_signature, nav.call_path, analyze.impact_slice, query.batch, workspace.status)."
                                : "Roslyn-backed C# navigation, diagnostics, and edits. Tool names map from command ids via roslyn_<command_id_with_underscores>. Resource template invocation is available via roslyn://command/{commandId}.";
                            JsonObject result = new()
                            {
                                ["protocolVersion"] = protocolVersion,
                                ["capabilities"] = new JsonObject
                                {
                                    ["tools"] = new JsonObject
                                    {
                                        ["listChanged"] = false,
                                    },
                                    ["resources"] = new JsonObject
                                    {
                                        ["subscribe"] = false,
                                        ["listChanged"] = false,
                                    },
                                },
                                ["serverInfo"] = new JsonObject
                                {
                                    ["name"] = "roslynskills-mcp",
                                    ["version"] = GetServerVersion(),
                                },
                                ["instructions"] = instructions,
                            };

                            await WriteResultResponseAsync(output, idNode!, result).ConfigureAwait(false);
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

                            JsonObject result = new()
                            {
                                ["tools"] = ActiveMode == McpToolMode.Slim
                                    ? BuildSlimToolList()
                                    : BuildToolList(toolMap),
                            };

                            await WriteResultResponseAsync(output, idNode!, result).ConfigureAwait(false);
                            break;
                        }

                        case "tools/call":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            await HandleToolCallAsync(output, idNode!, @params, registry, toolMap).ConfigureAwait(false);
                            break;
                        }

                        case "resources/list":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            JsonObject result = new()
                            {
                                ["resources"] = BuildResourceList(toolMap),
                            };

                            await WriteResultResponseAsync(output, idNode!, result).ConfigureAwait(false);
                            break;
                        }

                        case "resources/templates/list":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            JsonObject result = new()
                            {
                                ["resourceTemplates"] = BuildResourceTemplateList(),
                            };

                            await WriteResultResponseAsync(output, idNode!, result).ConfigureAwait(false);
                            break;
                        }

                        case "resources/read":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            await HandleResourceReadAsync(output, idNode!, @params, registry, toolMap).ConfigureAwait(false);
                            break;
                        }

                        case "ping":
                        {
                            if (!hasResponseId)
                            {
                                break;
                            }

                            await WriteResultResponseAsync(output, idNode!, new JsonObject()).ConfigureAwait(false);
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
                                idNode,
                                code: -32601,
                                message: $"Method not found: {method}",
                                dataNode: null).ConfigureAwait(false);
                            break;
                        }
                    }
                }
            }
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
        JsonNode idNode,
        JsonElement @params,
        ICommandRegistry registry,
        ToolMap toolMap)
    {
        if (@params.ValueKind != JsonValueKind.Object)
        {
            await WriteErrorResponseAsync(
                output,
                idNode,
                code: -32602,
                message: "Invalid params: tools/call expects an object.",
                dataNode: null).ConfigureAwait(false);
            return;
        }

        if (!TryGetStringProperty(@params, "name", out string? toolName) || string.IsNullOrWhiteSpace(toolName))
        {
            await WriteErrorResponseAsync(
                output,
                idNode,
                code: -32602,
                message: "Invalid params: tools/call requires a non-empty tool name.",
                dataNode: null).ConfigureAwait(false);
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
                    idNode,
                    code: -32602,
                    message: "Invalid params: roslyn_run requires a non-empty string 'command_id'.",
                    dataNode: null).ConfigureAwait(false);
                return;
            }

            resolvedCommandId = requestedCommandId.Trim();
            if (arguments.TryGetProperty("input", out JsonElement nestedInput))
            {
                if (nestedInput.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    await WriteErrorResponseAsync(
                        output,
                        idNode,
                        code: -32602,
                        message: "Invalid params: roslyn_run 'input' must be an object.",
                        dataNode: null).ConfigureAwait(false);
                    return;
                }

                commandInput = nestedInput.ValueKind == JsonValueKind.Object
                    ? nestedInput.Clone()
                    : JsonSerializer.SerializeToElement(new { });
            }
            else
            {
                commandInput = JsonSerializer.SerializeToElement(new { });
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
                idNode,
                code: -32602,
                message: hint,
                dataNode: null).ConfigureAwait(false);
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
            await WriteToolEnvelopeResultAsync(output, idNode, notFoundEnvelope).ConfigureAwait(false);
            return;
        }

        CommandEnvelope envelope = await ExecuteCommandAsync(command, resolvedCommandId, commandInput).ConfigureAwait(false);
        await WriteToolEnvelopeResultAsync(output, idNode, envelope).ConfigureAwait(false);
    }

    private static async Task WriteToolEnvelopeResultAsync(Stream output, JsonNode idNode, CommandEnvelope envelope)
    {
        JsonObject toolResult = new()
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = BuildToolResultText(envelope),
                },
            },
            ["structuredContent"] = ToJsonNode(envelope),
            ["isError"] = !envelope.Ok,
        };

        await WriteResultResponseAsync(output, idNode, toolResult).ConfigureAwait(false);
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
        JsonNode idNode,
        JsonElement @params,
        ICommandRegistry registry,
        ToolMap toolMap)
    {
        if (@params.ValueKind != JsonValueKind.Object)
        {
            await WriteErrorResponseAsync(
                output,
                idNode,
                code: -32602,
                message: "Invalid params: resources/read expects an object.",
                dataNode: null).ConfigureAwait(false);
            return;
        }

        if (!TryGetStringProperty(@params, "uri", out string? uriString) || string.IsNullOrWhiteSpace(uriString))
        {
            await WriteErrorResponseAsync(
                output,
                idNode,
                code: -32602,
                message: "Invalid params: resources/read requires a non-empty uri.",
                dataNode: null).ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out Uri? uri))
        {
            await WriteErrorResponseAsync(
                output,
                idNode,
                code: -32602,
                message: $"Invalid resource URI '{uriString}'.",
                dataNode: null).ConfigureAwait(false);
            return;
        }

        if (!uri.Scheme.Equals(ResourceScheme, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorResponseAsync(
                output,
                idNode,
                code: -32602,
                message: $"Unsupported resource scheme '{uri.Scheme}'.",
                dataNode: null).ConfigureAwait(false);
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
            await WriteResourceTextResultAsync(output, idNode, CommandsCatalogResourceUri, "application/json", text).ConfigureAwait(false);
            return;
        }

        if (uri.Host.Equals("command-meta", StringComparison.OrdinalIgnoreCase))
        {
            string commandId = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (string.IsNullOrWhiteSpace(commandId) || !toolMap.ByCommandId.TryGetValue(commandId, out ToolBinding? metaBinding))
            {
                await WriteErrorResponseAsync(
                    output,
                    idNode,
                    code: -32602,
                    message: $"Unknown command metadata resource '{uri.AbsoluteUri}'.",
                    dataNode: null).ConfigureAwait(false);
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
            await WriteResourceTextResultAsync(output, idNode, uri.AbsoluteUri, "application/json", text).ConfigureAwait(false);
            return;
        }

        if (uri.Host.Equals("command", StringComparison.OrdinalIgnoreCase))
        {
            string commandId = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (string.IsNullOrWhiteSpace(commandId) || !toolMap.ByCommandId.TryGetValue(commandId, out ToolBinding? binding))
            {
                await WriteErrorResponseAsync(
                    output,
                    idNode,
                    code: -32602,
                    message: $"Unknown command resource '{uri.AbsoluteUri}'.",
                    dataNode: null).ConfigureAwait(false);
                return;
            }

            if (!registry.TryGet(binding.CommandId, out IAgentCommand? command) || command is null)
            {
                await WriteErrorResponseAsync(
                    output,
                    idNode,
                    code: -32603,
                    message: $"Command '{binding.CommandId}' is unavailable.",
                    dataNode: null).ConfigureAwait(false);
                return;
            }

            if (!TryBuildResourceCommandInput(uri, out JsonElement input, out string? inputError))
            {
                await WriteErrorResponseAsync(
                    output,
                    idNode,
                    code: -32602,
                    message: inputError ?? "Failed to parse command input from resource URI.",
                    dataNode: null).ConfigureAwait(false);
                return;
            }

            CommandEnvelope envelope = await ExecuteCommandAsync(command, binding.CommandId, input).ConfigureAwait(false);

            string text = JsonSerializer.Serialize(envelope, JsonOptions);
            await WriteResourceTextResultAsync(output, idNode, uri.AbsoluteUri, "application/json", text).ConfigureAwait(false);
            return;
        }

        await WriteErrorResponseAsync(
            output,
            idNode,
            code: -32601,
            message: $"Unknown resource URI '{uri.AbsoluteUri}'.",
            dataNode: null).ConfigureAwait(false);
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
            Stopwatch validateTimer = Stopwatch.StartNew();
            IReadOnlyList<CommandError> validationErrors = command.Validate(input);
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
            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None).ConfigureAwait(false);
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
            ["command_id"] = new JsonObject { ["type"] = "string", ["description"] = "ctx.read_window, ctx.file_outline, edit.change_signature, nav.call_path, analyze.impact_slice, query.batch, workspace.*" },
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
                ["pattern", "patterns", "syntax_filter", "whole_word", "mode", "file_path", "roots", "max_results", "brief"],
                requiredOverride: []),
            SlimTool(
                "roslyn_context",
                "One-call symbol mindspace: def + refs + interface callers + impls + diag.",
                readOnly: true,
                "ctx.symbol_context",
                ["symbol", "file_path", "line", "column", "include_definition", "include_references", "include_callers", "include_callees", "include_implementations", "include_diagnostics", "brief"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_symbol",
                "Go to definition or find symbol_name in file.",
                readOnly: true,
                "nav.go_to_definition",
                ["symbol", "file_path", "line", "column", "brief"],
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
                ["symbol", "symbols", "file_path", "line", "column", "include_declaration", "max_results"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_invocations",
                "Call sites and creations (single symbol or symbols batch).",
                readOnly: true,
                "nav.find_invocations",
                ["symbol", "symbols", "file_path", "line", "column", "brief", "max_results", "include_object_creations"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_call_hierarchy",
                "Call graph (default max_depth=1; interface callers included).",
                readOnly: true,
                "nav.call_hierarchy",
                ["symbol", "file_path", "line", "column", "direction", "max_depth", "max_nodes", "max_edges", "brief"],
                requiredOverride: []),
            SlimTool(
                "roslyn_nav_implementations",
                "Implementing types/members and derived interfaces.",
                readOnly: true,
                "nav.find_implementations",
                ["symbol", "file_path", "line", "column", "max_results"],
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
                ["symbol", "file_path", "line", "column", "new_name", "apply", "max_diagnostics"],
                requiredOverride: ["new_name"]),
            SlimTool(
                "roslyn_edit_transaction",
                "Atomic multi-file edits with SHA-256 preconditions and diagnostics.",
                readOnly: false,
                "edit.transaction",
                ["operations", "file_path", "expected_sha256", "expected_file_hashes", "apply", "atomic", "include_diagnostics"],
                requiredOverride: ["operations"],
                allowedNested: txSlimNested),
            SlimTool(
                "roslyn_edit_code_fix",
                "Discover or apply Roslyn CodeFixProvider fixes.",
                readOnly: false,
                "edit.apply_code_fix",
                ["file_path", "diagnostic_id", "line", "column", "fix_title", "apply", "max_diagnostics"],
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

        return JsonSerializer.SerializeToElement(new { });
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
            return arguments.Clone();
        }

        return JsonSerializer.SerializeToElement(new { });
    }

    private static bool TryBuildResourceCommandInput(Uri resourceUri, out JsonElement input, out string? error)
    {
        input = JsonSerializer.SerializeToElement(new { });
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
        input = JsonSerializer.SerializeToElement(new { });
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

    private static JsonNode? TryGetIdNode(JsonElement root)
    {
        if (!root.TryGetProperty("id", out JsonElement idElement))
        {
            return null;
        }

        return JsonNode.Parse(idElement.GetRawText());
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return JsonNode.Parse(element.GetRawText());
        }

        return JsonSerializer.SerializeToNode(value, JsonOptions);
    }

    private static async Task WriteResultResponseAsync(Stream output, JsonNode idNode, JsonNode resultNode)
    {
        JsonObject response = new()
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = idNode,
            ["result"] = resultNode,
        };

        await WriteMessageAsync(output, response).ConfigureAwait(false);
    }

    private static async Task WriteErrorResponseAsync(Stream output, JsonNode? idNode, int code, string message, JsonNode? dataNode)
    {
        JsonObject error = new()
        {
            ["code"] = code,
            ["message"] = message,
        };

        if (dataNode is not null)
        {
            error["data"] = dataNode;
        }

        JsonObject response = new()
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = idNode,
            ["error"] = error,
        };

        await WriteMessageAsync(output, response).ConfigureAwait(false);
    }

    private static async Task WriteResourceTextResultAsync(
        Stream output,
        JsonNode idNode,
        string uri,
        string mimeType,
        string text)
    {
        JsonObject content = new()
        {
            ["uri"] = uri,
            ["mimeType"] = mimeType,
            ["text"] = text,
        };

        JsonObject result = new()
        {
            ["contents"] = new JsonArray
            {
                content,
            },
        };

        await WriteResultResponseAsync(output, idNode, result).ConfigureAwait(false);
    }

    private static async Task WriteMessageAsync(Stream output, JsonNode payload)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await output.WriteAsync(jsonBytes, 0, jsonBytes.Length).ConfigureAwait(false);
        await output.WriteAsync(new byte[] { (byte)'\n' }, 0, 1).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<JsonDocument?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
    {
        string? firstLine = await ReadAsciiLineAsync(input, cancellationToken).ConfigureAwait(false);
        while (firstLine is not null && firstLine.Length == 0)
        {
            firstLine = await ReadAsciiLineAsync(input, cancellationToken).ConfigureAwait(false);
        }

        if (firstLine is null)
        {
            return null;
        }

        string trimmedFirstLine = firstLine.TrimStart();
        if (trimmedFirstLine.StartsWith("{", StringComparison.Ordinal) ||
            trimmedFirstLine.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return JsonDocument.Parse(trimmedFirstLine);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Invalid newline-delimited JSON message.", ex);
            }
        }

        List<string> headers = new()
        {
            firstLine,
        };

        while (true)
        {
            string? line = await ReadAsciiLineAsync(input, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidDataException("Unexpected EOF while reading headers.");
            }

            if (line.Length == 0)
            {
                break;
            }

            headers.Add(line);
        }

        int contentLength = -1;
        foreach (string header in headers)
        {
            int separator = header.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string name = header.Substring(0, separator).Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rawValue = header.Substring(separator + 1).Trim();
            if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
            {
                throw new InvalidDataException($"Invalid Content-Length header value '{rawValue}'.");
            }
        }

        if (contentLength < 0)
        {
            throw new InvalidDataException("Missing Content-Length header.");
        }

        byte[] body = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            await ReadExactlyAsync(input, body, contentLength, cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(body.AsMemory(0, contentLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading message body.");
            }

            offset += read;
        }
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        bool sawAnyBytes = false;

        while (true)
        {
            int next = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                if (!sawAnyBytes)
                {
                    return null;
                }

                throw new EndOfStreamException("Unexpected EOF while reading header line.");
            }

            sawAnyBytes = true;
            byte value = (byte)next;
            if (value == (byte)'\n')
            {
                break;
            }

            if (value == (byte)'\r')
            {
                int lf = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                if (lf != (byte)'\n')
                {
                    throw new InvalidDataException("Header lines must end with CRLF.");
                }

                break;
            }

            Span<byte> span = buffer.GetSpan(1);
            span[0] = value;
            buffer.Advance(1);
            if (buffer.WrittenCount > MaxHeaderLineLength)
            {
                throw new InvalidDataException("Header line exceeds maximum supported length.");
            }
        }

        return Encoding.ASCII.GetString(buffer.WrittenSpan);
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        int read = await stream.ReadAsync(one, 0, 1, cancellationToken).ConfigureAwait(false);
        return read == 1 ? one[0] : -1;
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



