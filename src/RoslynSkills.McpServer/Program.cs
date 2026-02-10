#nullable enable
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynSkills.Contracts;
using RoslynSkills.Core;

namespace RoslynSkills.McpServer;

internal static class Program
{
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

    public static async Task<int> Main(string[] args)
    {
        _ = args;
        ICommandRegistry registry = DefaultRegistryFactory.Create();
        ToolMap toolMap = BuildToolMap(registry.ListCommands());

        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();

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
                                ["version"] = "0.1.0",
                            },
                            ["instructions"] = "Roslyn-backed C# navigation, diagnostics, and edits. Tool names map from command ids via roslyn_<command_id_with_underscores>. Resource template invocation is available via roslyn://command/{commandId}.",
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
                            ["tools"] = BuildToolList(toolMap),
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

        return 0;
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

        if (!toolMap.ByToolName.TryGetValue(toolName, out ToolBinding? binding))
        {
            await WriteErrorResponseAsync(
                output,
                idNode,
                code: -32602,
                message: $"Unknown tool '{toolName}'.",
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

        JsonElement input = GetArguments(@params);
        CommandEnvelope envelope = await ExecuteCommandAsync(command, binding.CommandId, input).ConfigureAwait(false);

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

            JsonObject payload = new()
            {
                ["commandId"] = binding.CommandId,
                ["ok"] = envelope.Ok,
                ["summary"] = BuildToolResultText(envelope),
                ["envelope"] = ToJsonNode(envelope),
            };

            string text = JsonSerializer.Serialize(payload, JsonOptions);
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
        try
        {
            IReadOnlyList<CommandError> validationErrors = command.Validate(input);
            if (validationErrors.Count > 0)
            {
                return new CommandEnvelope(
                    Ok: false,
                    CommandId: commandId,
                    Version: CommandEnvelopeVersion,
                    Data: null,
                    Errors: validationErrors,
                    TraceId: null,
                    Preview: null,
                    Summary: "Command validation failed.");
            }

            CommandExecutionResult result = await command.ExecuteAsync(input, CancellationToken.None).ConfigureAwait(false);
            return new CommandEnvelope(
                Ok: result.Ok,
                CommandId: commandId,
                Version: CommandEnvelopeVersion,
                Data: result.Data,
                Errors: result.Errors,
                TraceId: null,
                Preview: null,
                Summary: result.Ok ? "Command executed successfully." : "Command returned one or more errors.");
        }
        catch (Exception ex)
        {
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
                Summary: "Command execution threw an exception.");
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

    private static JsonArray BuildToolList(ToolMap toolMap)
    {
        JsonArray tools = new();
        foreach (ToolBinding binding in toolMap.OrderedBindings)
        {
            JsonObject inputSchema = BuildInputSchema(binding.CommandId, binding.InputSchemaVersion);

            JsonObject tool = new()
            {
                ["name"] = binding.ToolName,
                ["description"] = binding.Summary,
                ["inputSchema"] = inputSchema,
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = !binding.MutatesState,
                },
            };

            tools.Add(tool);
        }

        return tools;
    }

    private static JsonObject BuildInputSchema(string commandId, string schemaVersion)
    {
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["additionalProperties"] = true,
            ["description"] = $"Arguments for command '{commandId}'. Schema version: {schemaVersion}.",
        };

        JsonObject properties = new();
        JsonArray required = new();

        AddCommandInputHints(commandId, properties, required);

        if (properties.Count > 0)
        {
            schema["properties"] = properties;
        }

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static void AddCommandInputHints(string commandId, JsonObject properties, JsonArray required)
    {
        static JsonObject StringProperty(string description) => new()
        {
            ["type"] = "string",
            ["description"] = description,
        };

        static JsonObject IntProperty(string description, int min) => new()
        {
            ["type"] = "integer",
            ["minimum"] = min,
            ["description"] = description,
        };

        static JsonObject BoolProperty(string description) => new()
        {
            ["type"] = "boolean",
            ["description"] = description,
        };

        if (string.Equals(commandId, "session.open", StringComparison.OrdinalIgnoreCase))
        {
            properties["file_path"] = StringProperty("Path to a C# source file (.cs/.csx).");
            properties["session_id"] = StringProperty("Optional session identifier.");
            properties["max_diagnostics"] = IntProperty("Maximum diagnostics returned in snapshot.", 1);
            required.Add("file_path");
            return;
        }

        if (string.Equals(commandId, "edit.create_file", StringComparison.OrdinalIgnoreCase))
        {
            properties["file_path"] = StringProperty("Path of the file to create.");
            properties["content"] = StringProperty("File content; defaults to empty file.");
            properties["overwrite"] = BoolProperty("Whether to overwrite an existing file.");
            properties["create_directories"] = BoolProperty("Create missing parent directories.");
            properties["apply"] = BoolProperty("When false, run as dry-run.");
            properties["include_diagnostics"] = BoolProperty("Evaluate C# diagnostics for .cs/.csx content.");
            properties["max_diagnostics"] = IntProperty("Maximum diagnostics returned.", 1);
            required.Add("file_path");
            return;
        }

        if (string.Equals(commandId, "edit.rename_symbol", StringComparison.OrdinalIgnoreCase))
        {
            properties["file_path"] = StringProperty("Path to a C# source file.");
            properties["line"] = IntProperty("1-based line number for rename anchor.", 1);
            properties["column"] = IntProperty("1-based column number for rename anchor.", 1);
            properties["new_name"] = StringProperty("New symbol name.");
            properties["apply"] = BoolProperty("When false, run as dry-run.");
            properties["max_diagnostics"] = IntProperty("Maximum diagnostics returned.", 1);
            required.Add("file_path");
            required.Add("line");
            required.Add("column");
            required.Add("new_name");
            return;
        }

        if (string.Equals(commandId, "nav.find_symbol", StringComparison.OrdinalIgnoreCase))
        {
            properties["file_path"] = StringProperty("Path to a C# source file.");
            properties["symbol_name"] = StringProperty("Symbol name to search for.");
            properties["brief"] = BoolProperty("Return compact result payload.");
            properties["max_results"] = IntProperty("Maximum matches to return.", 1);
            required.Add("file_path");
            required.Add("symbol_name");
            return;
        }

        if (commandId.StartsWith("session.", StringComparison.OrdinalIgnoreCase))
        {
            properties["session_id"] = StringProperty("Session identifier.");
            required.Add("session_id");
        }
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
                MutatesState: descriptor.MutatesState);
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
        bool MutatesState);

    private sealed record ToolMap(
        IReadOnlyList<ToolBinding> OrderedBindings,
        IReadOnlyDictionary<string, ToolBinding> ByToolName,
        IReadOnlyDictionary<string, ToolBinding> ByCommandId);
}

