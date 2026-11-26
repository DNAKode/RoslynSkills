#nullable enable
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using RoslynSkills.Cli;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.McpServer;

/// <summary>
/// Routes MCP tool calls for hot-workspace and workspace-lifecycle commands directly to <c>RoslynSkills.WorkspaceHost</c>
/// over a persistent named-pipe / Unix-socket connection (<c>keep_alive: true</c>), bypassing CLI argument reconstruction
/// and per-call pipe setup. Automatically binds <c>workspace.use</c> on the first semantic workspace-scoped call when a
/// solution/project is discoverable and no daemon is bound yet.
/// </summary>
/// <remarks>
/// <c>ROSCLI_DAEMON=off</c> (or <c>ROSLYNSKILLS_MCP_ROUTING=off</c>) disables daemon routing and keeps in-process execution.
/// <c>ROSLYNSKILLS_MCP_AUTOBIND=off</c> disables automatic daemon startup/preload when no alias is bound.
/// </remarks>
internal sealed class HotCommandRouter : IAsyncDisposable
{
    private const string EnvelopeVersion = "1.0";
    private const int MaxPooledConnectionsPerRepo = 4;
    private const long StatCacheTtlMs = 1000;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WorkspacePreloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static readonly bool RoutingEnabled = ReadIsEnabled();
    private static readonly bool AutoBindEnabled = ReadIsAutoBindEnabled();
    private static readonly bool RequiredDaemonMode = ReadIsRequiredDaemonMode();
    private static readonly string DefaultAlias = Environment.GetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS") ?? "default";
    private static readonly string? ConfiguredRefreshPolicy = Environment.GetEnvironmentVariable("ROSCLI_REFRESH_POLICY");

    private readonly WorkspaceHostDaemonManager _daemonManager = new();
    private readonly ConcurrentDictionary<string, PersistentRepoSession> _sessions = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _repoRootCache = new(PathComparer);

    public HotCommandRouter(ICommandRegistry registry)
    {
        _ = registry;
    }

    public static bool IsEnabled() => RoutingEnabled;

    public static bool IsAutoBindEnabled() => AutoBindEnabled;

    private static bool ReadIsEnabled()
    {
        string? mcpRouting = Environment.GetEnvironmentVariable("ROSLYNSKILLS_MCP_ROUTING");
        if (string.Equals(mcpRouting?.Trim(), "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? daemon = Environment.GetEnvironmentVariable("ROSCLI_DAEMON")?.Trim();
        return !(string.Equals(daemon, "off", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(daemon, "0", StringComparison.Ordinal) ||
                 string.Equals(daemon, "false", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ReadIsAutoBindEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("ROSLYNSKILLS_MCP_AUTOBIND")?.Trim();
        return !(string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(raw, "0", StringComparison.Ordinal) ||
                 string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldRoute(IAgentCommand command)
        => RoutingEnabled && (command.Descriptor.RunsOnHotWorkspace || IsWorkspaceLifecycleCommand(command.Descriptor.Id));

    public static bool IsWorkspaceLifecycleCommand(string commandId)
        => commandId is "workspace.use" or "workspace.preload" or "workspace.status" or "workspace.refresh" or "workspace.close" or "workspace.list";

    /// <summary>
    /// Executes <paramref name="command"/> directly against <c>WorkspaceHost</c> over a persistent pipe/socket session
    /// and returns a flat <see cref="CommandEnvelope"/> with <c>transport.mode = "mcp"</c>.
    /// Returns <see langword="null"/> when the caller should fall back to in-process execution (for example, standalone
    /// files with no discoverable workspace and <c>ROSCLI_DAEMON != required</c>).
    /// </summary>
    public async Task<CommandEnvelope?> TryRunAsync(
        IAgentCommand command,
        string commandId,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        if (!RoutingEnabled)
        {
            return null;
        }

        string normalizedCommandId = string.Equals(commandId, "workspace.use", StringComparison.OrdinalIgnoreCase)
            ? "workspace.preload"
            : commandId;
        bool isWorkspaceLifecycle = IsWorkspaceLifecycleCommand(commandId);
        if (!command.Descriptor.RunsOnHotWorkspace && !isWorkspaceLifecycle)
        {
            return null;
        }

        Stopwatch totalTimer = Stopwatch.StartNew();
        bool requireHotWorkspace = !command.Descriptor.HotWorkspaceOptional &&
                                   !isWorkspaceLifecycle &&
                                   RequiredDaemonMode;

        JsonElement normalizedInput = NormalizeInputObject(input);
        string repoRoot = ResolveCachedRepoRoot(normalizedInput);
        PersistentRepoSession session = _sessions.GetOrAdd(repoRoot, root => new PersistentRepoSession(root, _daemonManager));

        if (isWorkspaceLifecycle)
        {
            await session.LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteWorkspaceLifecycleLockedAsync(
                    session,
                    commandId,
                    normalizedCommandId,
                    normalizedInput,
                    totalTimer,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                session.LifecycleGate.Release();
            }
        }

        return await ExecuteToolCallAsync(
            session,
            command,
            normalizedCommandId,
            normalizedInput,
            requireHotWorkspace,
            totalTimer,
            cancellationToken).ConfigureAwait(false);
    }

    private string ResolveCachedRepoRoot(JsonElement input)
    {
        string? explicitPath = GetOptionalString(input, "workspace_path")
            ?? GetOptionalString(input, "file_path")
            ?? GetOptionalString(input, "directory_path")
            ?? GetOptionalString(input, "source_file_path");

        if (string.IsNullOrWhiteSpace(explicitPath))
        {
            // Check if any nested object/array has a path; if not, cache by CurrentDirectory.
            string cwd = Environment.CurrentDirectory;
            if (!HasNestedPathProperty(input))
            {
                return _repoRootCache.GetOrAdd(cwd, _ => CliApplication.InferDaemonRepoRoot(EmptyObject));
            }

            return CliApplication.InferDaemonRepoRoot(input);
        }

        string fullPath = Path.GetFullPath(explicitPath);
        string cacheKey = Directory.Exists(fullPath) ? fullPath : (Path.GetDirectoryName(fullPath) ?? fullPath);
        return _repoRootCache.GetOrAdd(cacheKey, _ => CliApplication.InferDaemonRepoRoot(input));
    }

    private static bool HasNestedPathProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<CommandEnvelope?> ExecuteWorkspaceLifecycleLockedAsync(
        PersistentRepoSession session,
        string requestedCommandId,
        string normalizedCommandId,
        JsonElement input,
        Stopwatch totalTimer,
        CancellationToken cancellationToken)
    {
        string alias = GetOptionalString(input, "alias")
            ?? GetOptionalString(input, "workspace_alias")
            ?? GetDefaultAlias();
        string? explicitHandle = GetOptionalString(input, "workspace_handle");
        WorkspaceHostDaemonEndpoint endpoint = _daemonManager.GetDefaultEndpoint(session.RepoRoot);

        string method = normalizedCommandId switch
        {
            "workspace.preload" => WorkspaceHostProtocol.Method.WorkspacePreload,
            "workspace.status" => WorkspaceHostProtocol.Method.WorkspaceStatus,
            "workspace.refresh" => WorkspaceHostProtocol.Method.WorkspaceRefresh,
            "workspace.close" => WorkspaceHostProtocol.Method.WorkspaceClose,
            "workspace.list" => WorkspaceHostProtocol.Method.WorkspaceList,
            _ => WorkspaceHostProtocol.Method.ToolCall,
        };

        if (normalizedCommandId == "workspace.preload")
        {
            CommandEnvelope start = await _daemonManager.StartAsync(
                session.RepoRoot,
                hostPath: null,
                noDetach: null,
                idleTimeout: null,
                cancellationToken).ConfigureAwait(false);
            if (!start.Ok)
            {
                totalTimer.Stop();
                return start with
                {
                    CommandId = requestedCommandId,
                    Transport = new CommandTransportMetadata(
                        Mode: "mcp",
                        DaemonEndpoint: WorkspaceHostDaemonManager.FormatEndpoint(endpoint),
                        Timings: new CommandTransportTimings(TotalMs: Math.Round(totalTimer.Elapsed.TotalMilliseconds, 3))),
                };
            }

            session.RefreshConnectionInfo();
        }

        JsonElement resolvedInput = CliApplication.ResolveRelativePathsForDaemon(input);
        string? handle = explicitHandle;
        if (string.IsNullOrWhiteSpace(handle) &&
            normalizedCommandId is "workspace.status" or "workspace.refresh" or "workspace.close")
        {
            WorkspaceAliasStore aliasStore = new(session.RepoRoot);
            if (aliasStore.TryGet(alias, out WorkspaceAliasRecord? record) &&
                record is not null &&
                !string.IsNullOrWhiteSpace(record.WorkspaceHandle))
            {
                handle = record.WorkspaceHandle;
            }
        }

        WorkspaceHostRequest request = new(
            Id: Guid.NewGuid().ToString("N"),
            Method: method,
            WorkspaceAlias: alias,
            WorkspaceHandle: handle,
            RefreshPolicy: ConfiguredRefreshPolicy,
            CommandId: normalizedCommandId,
            Input: resolvedInput,
            KeepAlive: true);

        bool waitsForWarmup = input.ValueKind == JsonValueKind.Object &&
                              input.TryGetProperty("wait_warmup", out JsonElement waitWarmupProp) &&
                              waitWarmupProp.ValueKind == JsonValueKind.True;
        TimeSpan lifecycleTimeout = normalizedCommandId is "workspace.preload" or "workspace.refresh" || waitsForWarmup
            ? WorkspacePreloadTimeout
            : TimeSpan.FromSeconds(30);

        Stopwatch roundtripTimer = Stopwatch.StartNew();
        WorkspaceHostResponse? response = await session.SendPersistentAsync(
            request,
            lifecycleTimeout,
            cancellationToken).ConfigureAwait(false);
        roundtripTimer.Stop();

        if (response is null)
        {
            return null;
        }

        UpdatePersistedAliasFromLifecycle(session, normalizedCommandId, alias, response, endpoint);
        totalTimer.Stop();
        return BuildEnvelopeFromHostResponse(
            requestedCommandId,
            request,
            response,
            endpoint,
            routedAlias: alias,
            routedHandle: handle,
            roundtripTimer,
            totalTimer);
    }

    private async Task<CommandEnvelope?> ExecuteToolCallAsync(
        PersistentRepoSession session,
        IAgentCommand command,
        string commandId,
        JsonElement input,
        bool requireHotWorkspace,
        Stopwatch totalTimer,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = _daemonManager.GetDefaultEndpoint(session.RepoRoot);
        string alias = DefaultAlias;

        (bool hasBinding, string? boundAlias, string? boundHandle, string? boundWorkspacePath) = ResolveBinding(session, input, alias);

        // If no daemon/alias is bound yet, auto-bind on the first semantic workspace-scoped call when a solution/project exists.
        bool canAutoBind = !command.Descriptor.HotWorkspaceOptional && AutoBindEnabled;
        if (!hasBinding || !session.HasLiveConnectionOrManifest())
        {
            if (canAutoBind)
            {
                await session.LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Re-check after acquiring LifecycleGate in case another concurrent call already bound it.
                    (hasBinding, boundAlias, boundHandle, boundWorkspacePath) = ResolveBinding(session, input, alias);
                    if (!hasBinding || !session.HasLiveConnectionOrManifest())
                    {
                        string? targetWorkspace = DiscoverWorkspaceTarget(session.RepoRoot, input, boundWorkspacePath);
                        if (!string.IsNullOrWhiteSpace(targetWorkspace))
                        {
                            (bool boundOk, string? autoHandle, CommandEnvelope? bindError) = await AutoBindWorkspaceLockedAsync(
                                session,
                                endpoint,
                                alias,
                                targetWorkspace,
                                commandId,
                                totalTimer,
                                cancellationToken).ConfigureAwait(false);
                            if (boundOk && !string.IsNullOrWhiteSpace(autoHandle))
                            {
                                hasBinding = true;
                                boundAlias = alias;
                                boundHandle = autoHandle;
                                boundWorkspacePath = targetWorkspace;
                            }
                            else if (requireHotWorkspace && bindError is not null)
                            {
                                return bindError;
                            }
                        }
                    }
                }
                finally
                {
                    session.LifecycleGate.Release();
                }
            }
        }

        if (!hasBinding || string.IsNullOrWhiteSpace(boundHandle))
        {
            if (requireHotWorkspace)
            {
                totalTimer.Stop();
                return ErrorEnvelope(
                    commandId,
                    "hot_workspace_alias_not_found",
                    $"Hot workspace alias '{alias}' is not bound for '{session.RepoRoot}' and no workspace could be auto-bound. Call roslyn_run with workspace.preload or run 'roscli workspace.use <solution>'.",
                    endpoint,
                    totalTimer);
            }

            return null;
        }

        JsonElement preparedInput = BuildRoutedToolInput(commandId, input, boundHandle, boundWorkspacePath, requireHotWorkspace);

        WorkspaceHostRequest request = new(
            Id: Guid.NewGuid().ToString("N"),
            Method: WorkspaceHostProtocol.Method.ToolCall,
            WorkspaceAlias: boundAlias,
            WorkspaceHandle: boundHandle,
            RefreshPolicy: ConfiguredRefreshPolicy,
            CommandId: commandId,
            Input: preparedInput,
            KeepAlive: true);

        Stopwatch roundtripTimer = Stopwatch.StartNew();
        WorkspaceHostResponse? response = await session.SendPersistentAsync(request, ToolCallTimeout, cancellationToken).ConfigureAwait(false);

        // If the host lost the handle (e.g. restarted) or was down and we can auto-rebind, recover once transparently.
        if ((response is null || IsMissingHandleError(response)) && canAutoBind)
        {
            string? targetWorkspace = DiscoverWorkspaceTarget(session.RepoRoot, input, boundWorkspacePath);
            if (!string.IsNullOrWhiteSpace(targetWorkspace))
            {
                await session.LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                bool reboundOk;
                string? reboundHandle;
                CommandEnvelope? rebindError;
                try
                {
                    await session.ResetConnectionAsync().ConfigureAwait(false);
                    (reboundOk, reboundHandle, rebindError) = await AutoBindWorkspaceLockedAsync(
                        session,
                        endpoint,
                        alias,
                        targetWorkspace,
                        commandId,
                        totalTimer,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    session.LifecycleGate.Release();
                }

                if (reboundOk && !string.IsNullOrWhiteSpace(reboundHandle))
                {
                    boundHandle = reboundHandle;
                    boundAlias = alias;
                    preparedInput = BuildRoutedToolInput(commandId, input, boundHandle, targetWorkspace, requireHotWorkspace);
                    request = request with
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        WorkspaceAlias = boundAlias,
                        WorkspaceHandle = boundHandle,
                        Input = preparedInput,
                    };
                    roundtripTimer.Restart();
                    response = await session.SendPersistentAsync(request, ToolCallTimeout, cancellationToken).ConfigureAwait(false);
                }
                else if (requireHotWorkspace && rebindError is not null)
                {
                    return rebindError;
                }
            }
        }

        roundtripTimer.Stop();

        if (response is null)
        {
            if (command.Descriptor.HotWorkspaceOptional || !requireHotWorkspace)
            {
                return null;
            }

            totalTimer.Stop();
            return ErrorEnvelope(
                commandId,
                WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                $"Workspace daemon is unavailable on endpoint '{WorkspaceHostDaemonManager.FormatEndpoint(endpoint)}'.",
                endpoint,
                totalTimer);
        }

        if (!response.Ok &&
            !requireHotWorkspace &&
            HasErrorCode(response.Errors, WorkspaceHostProtocol.ErrorCode.ProtocolMismatch))
        {
            return null;
        }

        totalTimer.Stop();
        return BuildEnvelopeFromHostResponse(
            commandId,
            request,
            response,
            endpoint,
            boundAlias,
            boundHandle,
            roundtripTimer,
            totalTimer);
    }

    private async Task<(bool Ok, string? Handle, CommandEnvelope? Error)> AutoBindWorkspaceLockedAsync(
        PersistentRepoSession session,
        WorkspaceHostDaemonEndpoint endpoint,
        string alias,
        string workspacePath,
        string callerCommandId,
        Stopwatch totalTimer,
        CancellationToken cancellationToken)
    {
        CommandEnvelope start = await _daemonManager.StartAsync(
            session.RepoRoot,
            hostPath: null,
            noDetach: null,
            idleTimeout: null,
            cancellationToken).ConfigureAwait(false);
        if (!start.Ok)
        {
            totalTimer.Stop();
            return (false, null, start with
            {
                CommandId = callerCommandId,
                Transport = new CommandTransportMetadata(
                    Mode: "mcp",
                    DaemonEndpoint: WorkspaceHostDaemonManager.FormatEndpoint(endpoint),
                    Timings: new CommandTransportTimings(TotalMs: Math.Round(totalTimer.Elapsed.TotalMilliseconds, 3))),
            });
        }

        session.RefreshConnectionInfo();

        string ext = Path.GetExtension(workspacePath);
        bool isSolution = string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase);
        JsonElement preloadInput = JsonSerializer.SerializeToElement(new
        {
            workspace_path = Path.GetFullPath(workspacePath),
            require_solution = isSolution,
        });

        WorkspaceHostRequest preloadRequest = new(
            Id: Guid.NewGuid().ToString("N"),
            Method: WorkspaceHostProtocol.Method.WorkspacePreload,
            WorkspaceAlias: alias,
            CommandId: "workspace.preload",
            Input: preloadInput,
            KeepAlive: true);

        WorkspaceHostResponse? preloadResponse = await session.SendPersistentAsync(
            preloadRequest,
            WorkspacePreloadTimeout,
            cancellationToken).ConfigureAwait(false);
        if (preloadResponse is null || !preloadResponse.Ok)
        {
            string message = preloadResponse?.Errors?.FirstOrDefault()?.Message
                ?? $"Failed to auto-bind workspace '{workspacePath}' on daemon '{WorkspaceHostDaemonManager.FormatEndpoint(endpoint)}'.";
            return (false, null, ErrorEnvelope(
                callerCommandId,
                preloadResponse?.Errors?.FirstOrDefault()?.Code ?? WorkspaceHostProtocol.ErrorCode.DaemonUnavailable,
                message,
                endpoint,
                totalTimer));
        }

        UpdatePersistedAliasFromLifecycle(session, "workspace.preload", alias, preloadResponse, endpoint);
        string? handle = preloadResponse.Workspace?.WorkspaceHandle ?? TryGetStringProperty(preloadResponse.Data, "workspace_handle");
        return (!string.IsNullOrWhiteSpace(handle), handle, null);
    }

    private (bool HasBinding, string? Alias, string? Handle, string? WorkspacePath) ResolveBinding(
        PersistentRepoSession session,
        JsonElement input,
        string defaultAlias)
    {
        string? explicitHandle = GetOptionalString(input, "workspace_handle");
        if (!string.IsNullOrWhiteSpace(explicitHandle))
        {
            string? explicitAlias = GetOptionalString(input, "workspace_alias");
            return (true, explicitAlias, explicitHandle, GetOptionalString(input, "workspace_path"));
        }

        string alias = GetOptionalString(input, "workspace_alias") ?? defaultAlias;
        WorkspaceAliasRecord? record = session.GetCachedAliasRecord(alias);
        if (record is not null && !string.IsNullOrWhiteSpace(record.WorkspaceHandle))
        {
            return (true, alias, record.WorkspaceHandle, record.WorkspacePath);
        }

        return (false, alias, null, record?.WorkspacePath);
    }

    private static JsonElement BuildRoutedToolInput(
        string commandId,
        JsonElement input,
        string workspaceHandle,
        string? boundWorkspacePath,
        bool requireHotWorkspace)
    {
        bool hasWorkspaceHandle = false;
        bool hasRequireWorkspace = false;
        bool hasScope = false;

        if (input.ValueKind == JsonValueKind.Object)
        {
            hasWorkspaceHandle = input.TryGetProperty("workspace_handle", out _);
            hasRequireWorkspace = input.TryGetProperty("require_workspace", out _);
            hasScope =
                HasNonEmptyString(input, "directory_path") ||
                HasNonEmptyString(input, "workspace_path") ||
                HasNonEmptyString(input, "file_path") ||
                HasNonEmptyArray(input, "file_paths") ||
                HasNonEmptyArray(input, "roots");
        }

        string? defaultWorkspacePath = null;
        string? defaultDirectoryPath = null;
        if (!hasScope &&
            !string.IsNullOrWhiteSpace(boundWorkspacePath) &&
            (File.Exists(boundWorkspacePath) || Directory.Exists(boundWorkspacePath)))
        {
            if (string.Equals(commandId, "ctx.search_text", StringComparison.Ordinal))
            {
                defaultWorkspacePath = boundWorkspacePath;
            }
            else if (string.Equals(commandId, "diag.get_solution_snapshot", StringComparison.Ordinal))
            {
                string? directory = Directory.Exists(boundWorkspacePath)
                    ? boundWorkspacePath
                    : Path.GetDirectoryName(boundWorkspacePath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    defaultDirectoryPath = directory;
                }
            }
        }

        bool needsAugment =
            !hasWorkspaceHandle ||
            (requireHotWorkspace && !hasRequireWorkspace) ||
            defaultWorkspacePath is not null ||
            defaultDirectoryPath is not null;

        if (!needsAugment)
        {
            return CliApplication.ResolveRelativePathsForDaemon(input);
        }

        // Resolve relative paths first (fast no-op when paths are already absolute), then merge extra fields in one UTF-8 write.
        JsonElement resolved = CliApplication.ResolveRelativePathsForDaemon(input);
        ArrayBufferWriter<byte> buffer = new(512);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            if (resolved.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in resolved.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
            }

            if (!hasWorkspaceHandle)
            {
                writer.WriteString("workspace_handle"u8, workspaceHandle);
            }

            if (requireHotWorkspace && !hasRequireWorkspace)
            {
                writer.WriteBoolean("require_workspace"u8, true);
            }

            if (defaultWorkspacePath is not null)
            {
                writer.WriteString("workspace_path"u8, defaultWorkspacePath);
            }

            if (defaultDirectoryPath is not null)
            {
                writer.WriteString("directory_path"u8, defaultDirectoryPath);
            }

            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static string? DiscoverWorkspaceTarget(string repoRoot, JsonElement input, string? persistedWorkspacePath)
    {
        // 1. Explicit workspace_path in input
        string? explicitWorkspace = GetOptionalString(input, "workspace_path");
        if (!string.IsNullOrWhiteSpace(explicitWorkspace))
        {
            string fullExplicit = Path.GetFullPath(explicitWorkspace);
            if (File.Exists(fullExplicit) && IsMsBuildWorkspaceFile(fullExplicit))
            {
                return fullExplicit;
            }

            if (Directory.Exists(fullExplicit))
            {
                string? inExplicitDir = FindPreferredWorkspaceFileInDirectory(fullExplicit);
                if (!string.IsNullOrWhiteSpace(inExplicitDir))
                {
                    return inExplicitDir;
                }
            }
        }

        // 2. Persisted workspace path from previous alias binding (e.g., daemon restarted after idle timeout)
        if (!string.IsNullOrWhiteSpace(persistedWorkspacePath) &&
            File.Exists(persistedWorkspacePath) &&
            IsMsBuildWorkspaceFile(persistedWorkspacePath))
        {
            return Path.GetFullPath(persistedWorkspacePath);
        }

        // 3. Top-level solution in repo root (.slnx preferred, then .sln)
        if (Directory.Exists(repoRoot))
        {
            string? repoSolution = FindPreferredSolutionInDirectory(repoRoot);
            if (!string.IsNullOrWhiteSpace(repoSolution))
            {
                return repoSolution;
            }
        }

        // 4. Nearest .csproj/.vbproj walking up from target file_path or directory_path to repoRoot
        string? anchorPath = GetOptionalString(input, "file_path")
            ?? GetOptionalString(input, "directory_path")
            ?? GetOptionalString(input, "source_file_path");
        if (!string.IsNullOrWhiteSpace(anchorPath))
        {
            string fullAnchor = Path.GetFullPath(anchorPath);
            string? dir = Directory.Exists(fullAnchor) ? fullAnchor : Path.GetDirectoryName(fullAnchor);
            string normalizedRepoRoot = Path.GetFullPath(repoRoot);
            while (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                string? projectOrSolution = FindPreferredWorkspaceFileInDirectory(dir);
                if (!string.IsNullOrWhiteSpace(projectOrSolution))
                {
                    return projectOrSolution;
                }

                if (PathComparer.Equals(dir, normalizedRepoRoot))
                {
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        return null;
    }

    private static string? FindPreferredSolutionInDirectory(string directory)
    {
        try
        {
            string dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            foreach (string pattern in new[] { "*.slnx", "*.sln" })
            {
                string[] files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (files.Length == 1)
                {
                    return files[0];
                }

                if (files.Length > 1)
                {
                    string? matchingName = files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileNameWithoutExtension(f), dirName, StringComparison.OrdinalIgnoreCase));
                    return matchingName ?? files[0];
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? FindPreferredWorkspaceFileInDirectory(string directory)
    {
        string? solution = FindPreferredSolutionInDirectory(directory);
        if (!string.IsNullOrWhiteSpace(solution))
        {
            return solution;
        }

        try
        {
            foreach (string pattern in new[] { "*.csproj", "*.vbproj" })
            {
                string[] projects = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (projects.Length > 0)
                {
                    return projects[0];
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool IsMsBuildWorkspaceFile(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdatePersistedAliasFromLifecycle(
        PersistentRepoSession session,
        string normalizedCommandId,
        string alias,
        WorkspaceHostResponse response,
        WorkspaceHostDaemonEndpoint endpoint)
    {
        if (!response.Ok || string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        WorkspaceAliasStore aliasStore = new(endpoint.RepoRoot);
        if (normalizedCommandId == "workspace.close")
        {
            aliasStore.Remove(alias);
            session.InvalidateAliasCache();
            return;
        }

        if (normalizedCommandId is not ("workspace.preload" or "workspace.status" or "workspace.refresh"))
        {
            return;
        }

        string? handle = response.Workspace?.WorkspaceHandle ?? TryGetStringProperty(response.Data, "workspace_handle");
        if (string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        string? workspacePath = TryGetStringProperty(response.Data, "resolved_workspace_path")
            ?? TryGetStringProperty(response.Data, "requested_workspace_path");
        string? fingerprint = response.Workspace?.WorkspaceFingerprint
            ?? TryGetStringProperty(response.Data, "workspace_fingerprint");

        WorkspaceAliasRecord record = new(
            WorkspacePath: workspacePath,
            WorkspaceHandle: handle,
            DaemonEndpoint: WorkspaceHostDaemonManager.FormatEndpoint(endpoint),
            DaemonPid: _daemonManager.TryGetDaemonProcessId(endpoint.RepoRoot),
            WorkspaceFingerprint: fingerprint,
            LastSeenUtc: DateTimeOffset.UtcNow);
        aliasStore.Upsert(alias, record);
        session.InvalidateAliasCache();
    }

    private static CommandEnvelope BuildEnvelopeFromHostResponse(
        string commandId,
        WorkspaceHostRequest request,
        WorkspaceHostResponse response,
        WorkspaceHostDaemonEndpoint endpoint,
        string? routedAlias,
        string? routedHandle,
        Stopwatch roundtripTimer,
        Stopwatch totalTimer)
    {
        IReadOnlyList<CommandError> errors = response.Errors ?? Array.Empty<CommandError>();
        (string? dataHandle, string? dataCacheMode, string? dataRefreshAction) = ExtractTransportFieldsFromData(response.Data);
        string? workspaceHandle = response.Workspace?.WorkspaceHandle ?? routedHandle ?? request.WorkspaceHandle ?? dataHandle;
        string? alias = routedAlias ?? request.WorkspaceAlias ?? response.Workspace?.Alias;
        string? refreshAction = response.Workspace?.RefreshAction ?? dataRefreshAction;

        CommandTransportMetadata transport = new(
            Mode: "mcp",
            RoutedAlias: alias,
            WorkspaceHandle: workspaceHandle,
            CacheMode: dataCacheMode,
            RefreshPolicy: request.RefreshPolicy ?? ConfiguredRefreshPolicy,
            RefreshAction: refreshAction,
            DaemonEndpoint: WorkspaceHostDaemonManager.FormatEndpoint(endpoint),
            Timings: new CommandTransportTimings(
                HostElapsedMs: response.ElapsedMs,
                RoundtripMs: Math.Round(roundtripTimer.Elapsed.TotalMilliseconds, 3),
                TotalMs: Math.Round(totalTimer.Elapsed.TotalMilliseconds, 3),
                HostFreshnessMs: response.FreshnessMs,
                HostExecuteMs: response.ExecuteMs,
                HostFreshnessMode: response.FreshnessMode),
            Workspace: response.Workspace);

        string summary = !string.IsNullOrWhiteSpace(response.Summary)
            ? response.Summary!
            : response.Ok
                ? $"{commandId} ok"
                : $"{commandId} failed: {errors.FirstOrDefault()?.Code ?? "error"} ({errors.Count} error(s))";

        return new CommandEnvelope(
            Ok: response.Ok,
            CommandId: commandId,
            Version: response.Version ?? EnvelopeVersion,
            Data: response.Data,
            Errors: errors,
            TraceId: response.TraceId,
            Telemetry: response.Telemetry,
            Preview: response.Preview ?? summary,
            Summary: summary,
            Transport: transport);
    }

    private static CommandEnvelope ErrorEnvelope(
        string commandId,
        string code,
        string message,
        WorkspaceHostDaemonEndpoint endpoint,
        Stopwatch totalTimer)
    {
        return new CommandEnvelope(
            Ok: false,
            CommandId: commandId,
            Version: EnvelopeVersion,
            Data: null,
            Errors: new[] { new CommandError(code, message) },
            TraceId: null,
            Preview: $"{commandId} failed",
            Summary: $"{commandId} failed: {code}",
            Transport: new CommandTransportMetadata(
                Mode: "mcp",
                DaemonEndpoint: WorkspaceHostDaemonManager.FormatEndpoint(endpoint),
                Timings: new CommandTransportTimings(TotalMs: Math.Round(totalTimer.Elapsed.TotalMilliseconds, 3))));
    }

    private static (string? WorkspaceHandle, string? CacheMode, string? RefreshAction) ExtractTransportFieldsFromData(object? data)
    {
        if (data is null)
        {
            return (null, null, null);
        }

        JsonElement element = data is JsonElement existingElement
            ? existingElement
            : JsonSerializer.SerializeToElement(data);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        string? handle = GetOptionalString(element, "workspace_handle");
        string? cacheMode = GetOptionalString(element, "workspace_cache_mode");
        string? refreshAction = GetOptionalString(element, "refresh_action");
        if (element.TryGetProperty("query", out JsonElement query) &&
            query.ValueKind == JsonValueKind.Object &&
            query.TryGetProperty("workspace_context", out JsonElement context) &&
            context.ValueKind == JsonValueKind.Object)
        {
            handle ??= GetOptionalString(context, "workspace_handle");
            cacheMode ??= GetOptionalString(context, "workspace_cache_mode");
        }

        return (handle, cacheMode, refreshAction);
    }

    private static bool IsMissingHandleError(WorkspaceHostResponse response)
        => !response.Ok &&
           (HasErrorCode(response.Errors, WorkspaceHostProtocol.ErrorCode.WorkspaceHandleNotFound) ||
            HasErrorCode(response.Errors, "workspace_not_found"));

    private static bool HasErrorCode(IReadOnlyList<CommandError>? errors, string code)
        => errors is not null &&
           errors.Any(error => string.Equals(error.Code, code, StringComparison.OrdinalIgnoreCase));

    private static bool ReadIsRequiredDaemonMode()
    {
        string? raw = Environment.GetEnvironmentVariable("ROSCLI_DAEMON")?.Trim();
        if (string.Equals(raw, "required", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "require", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "1", StringComparison.Ordinal) ||
            string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? requireHot = Environment.GetEnvironmentVariable("ROSCLI_REQUIRE_HOT_WORKSPACE")?.Trim();
        return string.Equals(requireHot, "1", StringComparison.Ordinal) ||
               string.Equals(requireHot, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDefaultAlias() => DefaultAlias;

    private static JsonElement NormalizeInputObject(JsonElement input)
        => input.ValueKind == JsonValueKind.Object
            ? input
            : EmptyObject;

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out JsonElement prop) &&
           prop.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(prop.GetString())
            ? prop.GetString()
            : null;

    private static string? TryGetStringProperty(object? data, string propertyName)
    {
        if (data is null)
        {
            return null;
        }

        JsonElement element = data is JsonElement existing
            ? existing
            : JsonSerializer.SerializeToElement(data);
        return GetOptionalString(element, propertyName);
    }

    private static bool HasNonEmptyString(JsonElement element, string propertyName)
        => GetOptionalString(element, propertyName) is not null;

    private static bool HasNonEmptyArray(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out JsonElement prop) &&
           prop.ValueKind == JsonValueKind.Array &&
           prop.GetArrayLength() > 0;

    public async ValueTask DisposeAsync()
    {
        foreach (PersistentRepoSession session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    private sealed class PersistentRepoSession : IAsyncDisposable
    {
        private readonly WorkspaceHostDaemonManager _daemonManager;
        private readonly WorkspaceHostDaemonEndpoint _endpoint;
        private readonly WorkspaceAliasStore _aliasStore;
        private readonly ConcurrentQueue<WorkspaceHostClient> _pool = new();
        private readonly SemaphoreSlim _concurrencyGate = new(MaxPooledConnectionsPerRepo, MaxPooledConnectionsPerRepo);
        private readonly object _cacheLock = new();

        private WorkspaceHostConnectionInfo? _connectionInfo;
        private long _manifestCheckedTickMs;
        private DateTime _cachedManifestMtime = DateTime.MinValue;

        private long _aliasCheckedTickMs;
        private DateTime _aliasCacheLastWriteUtc = DateTime.MinValue;
        private IReadOnlyDictionary<string, WorkspaceAliasRecord>? _cachedAliases;

        public PersistentRepoSession(string repoRoot, WorkspaceHostDaemonManager daemonManager)
        {
            RepoRoot = repoRoot;
            _daemonManager = daemonManager;
            _endpoint = daemonManager.GetDefaultEndpoint(repoRoot);
            _aliasStore = new WorkspaceAliasStore(repoRoot);
        }

        public string RepoRoot { get; }
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);

        public void RefreshConnectionInfo()
        {
            WorkspaceHostConnectionInfo? latest = _daemonManager.TryGetConnectionInfo(RepoRoot);
            lock (_cacheLock)
            {
                _connectionInfo = latest;
                _cachedManifestMtime = latest?.ManifestLastWriteUtc ?? DateTime.MinValue;
                _manifestCheckedTickMs = Environment.TickCount64;
            }
        }

        public void InvalidateAliasCache()
        {
            lock (_cacheLock)
            {
                _cachedAliases = null;
                _aliasCacheLastWriteUtc = DateTime.MinValue;
                _aliasCheckedTickMs = 0;
            }
        }

        public bool HasLiveConnectionOrManifest()
        {
            long now = Environment.TickCount64;
            lock (_cacheLock)
            {
                if (_connectionInfo is not null &&
                    _cachedManifestMtime != DateTime.MinValue &&
                    _cachedManifestMtime == _connectionInfo.ManifestLastWriteUtc &&
                    now - _manifestCheckedTickMs < StatCacheTtlMs)
                {
                    return true;
                }
            }

            DateTime mtime = GetFileLastWriteTimeUtcSafe(_endpoint.ManifestPath);
            lock (_cacheLock)
            {
                _cachedManifestMtime = mtime;
                _manifestCheckedTickMs = now;
                return mtime != DateTime.MinValue;
            }
        }

        public WorkspaceAliasRecord? GetCachedAliasRecord(string alias)
        {
            long now = Environment.TickCount64;
            lock (_cacheLock)
            {
                if (_cachedAliases is not null && now - _aliasCheckedTickMs < StatCacheTtlMs)
                {
                    return _cachedAliases.TryGetValue(alias, out WorkspaceAliasRecord? cached) ? cached : null;
                }
            }

            DateTime currentMtime = GetFileLastWriteTimeUtcSafe(_aliasStore.Path);
            lock (_cacheLock)
            {
                _aliasCheckedTickMs = now;
                if (_cachedAliases is null || currentMtime != _aliasCacheLastWriteUtc)
                {
                    _cachedAliases = _aliasStore.List();
                    _aliasCacheLastWriteUtc = currentMtime;
                }

                return _cachedAliases.TryGetValue(alias, out WorkspaceAliasRecord? record) ? record : null;
            }
        }

        public async Task<WorkspaceHostResponse?> SendPersistentAsync(
            WorkspaceHostRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    (WorkspaceHostClient? client, string? authToken) = await RentClientAsync(cancellationToken).ConfigureAwait(false);
                    if (client is null)
                    {
                        return null;
                    }

                    WorkspaceHostRequest authorized = string.IsNullOrWhiteSpace(authToken)
                        ? request
                        : request with { AuthToken = authToken };

                    using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestTimeout.CancelAfter(timeout);

                    bool returnToPool = false;
                    try
                    {
                        WorkspaceHostResponse response = await client.SendAsync(authorized, requestTimeout.Token).ConfigureAwait(false);
                        returnToPool = true;
                        if (!string.Equals(response.ProtocolVersion, WorkspaceHostProtocol.ProtocolVersion, StringComparison.Ordinal))
                        {
                            return new WorkspaceHostResponse(
                                Id: request.Id,
                                Ok: false,
                                ProtocolVersion: WorkspaceHostProtocol.ProtocolVersion,
                                Method: request.Method,
                                ElapsedMs: 0,
                                CommandId: request.CommandId,
                                Version: EnvelopeVersion,
                                Errors: new[]
                                {
                                    new CommandError(
                                        WorkspaceHostProtocol.ErrorCode.ProtocolMismatch,
                                        $"Workspace host protocol version '{response.ProtocolVersion}' does not match client protocol '{WorkspaceHostProtocol.ProtocolVersion}'."),
                                });
                        }

                        return response;
                    }
                    catch (Exception ex) when (attempt == 0 &&
                                               (ex is IOException or SocketException or ObjectDisposedException or JsonException))
                    {
                        // Pipe dropped (e.g., daemon restarted or idle-timed-out); dispose and reconnect once.
                        await DisposeClientSafeAsync(client).ConfigureAwait(false);
                        await ResetConnectionAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException or JsonException ||
                                               (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        await DisposeClientSafeAsync(client).ConfigureAwait(false);
                        await ResetConnectionAsync().ConfigureAwait(false);
                        return null;
                    }
                    finally
                    {
                        if (returnToPool)
                        {
                            _pool.Enqueue(client);
                        }
                    }
                }

                return null;
            }
            finally
            {
                _concurrencyGate.Release();
            }
        }

        private async Task<(WorkspaceHostClient? Client, string? AuthToken)> RentClientAsync(CancellationToken cancellationToken)
        {
            WorkspaceHostConnectionInfo? connInfo = await EnsureConnectionInfoAsync(cancellationToken).ConfigureAwait(false);
            if (connInfo is null)
            {
                return (null, null);
            }

            if (_pool.TryDequeue(out WorkspaceHostClient? pooled))
            {
                return (pooled, connInfo.AuthToken);
            }

            WorkspaceHostClientOptions options = connInfo.Endpoint.Transport == "named-pipe"
                ? WorkspaceHostClientOptions.NamedPipe(connInfo.Endpoint.PipeName!)
                : WorkspaceHostClientOptions.UnixSocket(connInfo.Endpoint.SocketPath!);

            try
            {
                WorkspaceHostClient client = await WorkspaceHostClient.ConnectAsync(options, ConnectTimeout, cancellationToken).ConfigureAwait(false);
                return (client, connInfo.AuthToken);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or UnauthorizedAccessException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return (null, null);
            }
        }

        private async Task<WorkspaceHostConnectionInfo?> EnsureConnectionInfoAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            long now = Environment.TickCount64;
            WorkspaceHostConnectionInfo? current;
            lock (_cacheLock)
            {
                current = _connectionInfo;
                if (current is not null &&
                    _cachedManifestMtime != DateTime.MinValue &&
                    _cachedManifestMtime == current.ManifestLastWriteUtc &&
                    now - _manifestCheckedTickMs < StatCacheTtlMs)
                {
                    return current;
                }
            }

            DateTime currentManifestMtime = GetFileLastWriteTimeUtcSafe(_endpoint.ManifestPath);
            lock (_cacheLock)
            {
                _cachedManifestMtime = currentManifestMtime;
                _manifestCheckedTickMs = now;
                if (_connectionInfo is not null &&
                    currentManifestMtime != DateTime.MinValue &&
                    currentManifestMtime == _connectionInfo.ManifestLastWriteUtc)
                {
                    return _connectionInfo;
                }
            }

            WorkspaceHostConnectionInfo? latest = _daemonManager.TryGetConnectionInfo(RepoRoot);
            if (latest is null)
            {
                await ResetConnectionAsync().ConfigureAwait(false);
                return null;
            }

            bool sameDaemon;
            lock (_cacheLock)
            {
                sameDaemon = _connectionInfo is not null &&
                    _connectionInfo.ManifestLastWriteUtc == latest.ManifestLastWriteUtc &&
                    _connectionInfo.DaemonPid == latest.DaemonPid &&
                    string.Equals(_connectionInfo.AuthToken, latest.AuthToken, StringComparison.Ordinal);
                if (sameDaemon)
                {
                    return _connectionInfo;
                }
            }

            await DrainPoolAsync().ConfigureAwait(false);
            lock (_cacheLock)
            {
                _connectionInfo = latest;
                _cachedManifestMtime = latest.ManifestLastWriteUtc;
                _manifestCheckedTickMs = Environment.TickCount64;
            }

            return latest;
        }

        public async Task ResetConnectionAsync()
        {
            await DrainPoolAsync().ConfigureAwait(false);
            lock (_cacheLock)
            {
                _connectionInfo = null;
                _cachedManifestMtime = DateTime.MinValue;
                _manifestCheckedTickMs = 0;
            }
        }

        private async Task DrainPoolAsync()
        {
            while (_pool.TryDequeue(out WorkspaceHostClient? client))
            {
                await DisposeClientSafeAsync(client).ConfigureAwait(false);
            }
        }

        private static async Task DisposeClientSafeAsync(WorkspaceHostClient client)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ResetConnectionAsync().ConfigureAwait(false);
            LifecycleGate.Dispose();
            _concurrencyGate.Dispose();
        }

        private static DateTime GetFileLastWriteTimeUtcSafe(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch (IOException)
            {
                return DateTime.MinValue;
            }
            catch (UnauthorizedAccessException)
            {
                return DateTime.MinValue;
            }
        }
    }
}
