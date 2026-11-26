#nullable enable
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
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WorkspacePreloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly WorkspaceHostDaemonManager _daemonManager = new();
    private readonly ConcurrentDictionary<string, PersistentRepoSession> _sessions = new(PathComparer);

    public HotCommandRouter(ICommandRegistry registry)
    {
        _ = registry;
    }

    public static bool IsEnabled()
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

    public static bool IsAutoBindEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("ROSLYNSKILLS_MCP_AUTOBIND")?.Trim();
        return !(string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(raw, "0", StringComparison.Ordinal) ||
                 string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldRoute(IAgentCommand command)
        => IsEnabled() && (command.Descriptor.RunsOnHotWorkspace || IsWorkspaceLifecycleCommand(command.Descriptor.Id));

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
        if (!IsEnabled())
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
                                   IsRequiredDaemonMode();

        JsonElement normalizedInput = NormalizeInputObject(input);
        string repoRoot = CliApplication.InferDaemonRepoRoot(normalizedInput);
        PersistentRepoSession session = _sessions.GetOrAdd(repoRoot, root => new PersistentRepoSession(root, _daemonManager));

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isWorkspaceLifecycle)
            {
                return await ExecuteWorkspaceLifecycleLockedAsync(
                    session,
                    commandId,
                    normalizedCommandId,
                    normalizedInput,
                    totalTimer,
                    cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteToolCallLockedAsync(
                session,
                command,
                normalizedCommandId,
                normalizedInput,
                requireHotWorkspace,
                totalTimer,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
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
            RefreshPolicy: Environment.GetEnvironmentVariable("ROSCLI_REFRESH_POLICY"),
            CommandId: normalizedCommandId,
            Input: resolvedInput,
            KeepAlive: true);

        Stopwatch roundtripTimer = Stopwatch.StartNew();
        WorkspaceHostResponse? response = await session.SendPersistentAsync(
            request,
            normalizedCommandId is "workspace.preload" or "workspace.refresh" ? WorkspacePreloadTimeout : TimeSpan.FromSeconds(30),
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

    private async Task<CommandEnvelope?> ExecuteToolCallLockedAsync(
        PersistentRepoSession session,
        IAgentCommand command,
        string commandId,
        JsonElement input,
        bool requireHotWorkspace,
        Stopwatch totalTimer,
        CancellationToken cancellationToken)
    {
        WorkspaceHostDaemonEndpoint endpoint = _daemonManager.GetDefaultEndpoint(session.RepoRoot);
        string alias = GetDefaultAlias();

        (bool hasBinding, string? boundAlias, string? boundHandle, string? boundWorkspacePath) = ResolveBinding(session, input, alias);

        // If no daemon/alias is bound yet, auto-bind on the first semantic workspace-scoped call when a solution/project exists.
        bool canAutoBind = !command.Descriptor.HotWorkspaceOptional && IsAutoBindEnabled();
        if (!hasBinding || !session.HasLiveConnectionOrManifest())
        {
            if (canAutoBind)
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
            RefreshPolicy: Environment.GetEnvironmentVariable("ROSCLI_REFRESH_POLICY"),
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
                await session.ResetConnectionAsync().ConfigureAwait(false);
                (bool reboundOk, string? reboundHandle, CommandEnvelope? rebindError) = await AutoBindWorkspaceLockedAsync(
                    session,
                    endpoint,
                    alias,
                    targetWorkspace,
                    commandId,
                    totalTimer,
                    cancellationToken).ConfigureAwait(false);
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
        Dictionary<string, object?> augmented = new(StringComparer.Ordinal);
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in input.EnumerateObject())
            {
                augmented[property.Name] = property.Value.Clone();
            }
        }

        if (!augmented.ContainsKey("workspace_handle"))
        {
            augmented["workspace_handle"] = workspaceHandle;
        }

        if (requireHotWorkspace && !augmented.ContainsKey("require_workspace"))
        {
            augmented["require_workspace"] = true;
        }

        // Apply default workspace scope for ctx.search_text / diag.get_solution_snapshot when omitted, matching CLI behavior.
        if (!string.IsNullOrWhiteSpace(boundWorkspacePath) &&
            (File.Exists(boundWorkspacePath) || Directory.Exists(boundWorkspacePath)) &&
            (string.Equals(commandId, "ctx.search_text", StringComparison.Ordinal) ||
             string.Equals(commandId, "diag.get_solution_snapshot", StringComparison.Ordinal)))
        {
            bool hasScope =
                HasNonEmptyString(input, "directory_path") ||
                HasNonEmptyString(input, "workspace_path") ||
                HasNonEmptyString(input, "file_path") ||
                HasNonEmptyArray(input, "file_paths") ||
                HasNonEmptyArray(input, "roots");
            if (!hasScope)
            {
                if (string.Equals(commandId, "ctx.search_text", StringComparison.Ordinal))
                {
                    augmented["workspace_path"] = boundWorkspacePath;
                }
                else
                {
                    string? directory = Directory.Exists(boundWorkspacePath)
                        ? boundWorkspacePath
                        : Path.GetDirectoryName(boundWorkspacePath);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    {
                        augmented["directory_path"] = directory;
                    }
                }
            }
        }

        return CliApplication.ResolveRelativePathsForDaemon(JsonSerializer.SerializeToElement(augmented));
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
            RefreshPolicy: request.RefreshPolicy ?? Environment.GetEnvironmentVariable("ROSCLI_REFRESH_POLICY"),
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

    private static bool IsRequiredDaemonMode()
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

    private static string GetDefaultAlias()
        => Environment.GetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS") ?? "default";

    private static JsonElement NormalizeInputObject(JsonElement input)
        => input.ValueKind == JsonValueKind.Object
            ? input
            : JsonSerializer.SerializeToElement(new { });

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
        private WorkspaceHostClient? _client;
        private WorkspaceHostConnectionInfo? _connectionInfo;
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
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void RefreshConnectionInfo()
        {
            _connectionInfo = _daemonManager.TryGetConnectionInfo(RepoRoot);
        }

        public void InvalidateAliasCache()
        {
            _cachedAliases = null;
            _aliasCacheLastWriteUtc = DateTime.MinValue;
        }

        public bool HasLiveConnectionOrManifest()
        {
            if (_client is not null &&
                _connectionInfo is not null &&
                GetFileLastWriteTimeUtcSafe(_endpoint.ManifestPath) == _connectionInfo.ManifestLastWriteUtc &&
                _connectionInfo.ManifestLastWriteUtc != DateTime.MinValue)
            {
                return true;
            }

            return File.Exists(_endpoint.ManifestPath);
        }

        public WorkspaceAliasRecord? GetCachedAliasRecord(string alias)
        {
            DateTime currentMtime = GetFileLastWriteTimeUtcSafe(_aliasStore.Path);
            if (_cachedAliases is null || currentMtime != _aliasCacheLastWriteUtc)
            {
                _cachedAliases = _aliasStore.List();
                _aliasCacheLastWriteUtc = currentMtime;
            }

            return _cachedAliases.TryGetValue(alias, out WorkspaceAliasRecord? record) ? record : null;
        }

        public async Task<WorkspaceHostResponse?> SendPersistentAsync(
            WorkspaceHostRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                WorkspaceHostRequest authorized = string.IsNullOrWhiteSpace(_connectionInfo?.AuthToken)
                    ? request
                    : request with { AuthToken = _connectionInfo.AuthToken };

                using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(timeout);

                try
                {
                    WorkspaceHostResponse response = await _client!.SendAsync(authorized, requestTimeout.Token).ConfigureAwait(false);
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
                    // Pipe dropped (e.g., daemon restarted or idle-timed-out); reconnect once.
                    await ResetConnectionAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException or JsonException ||
                                           (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    await ResetConnectionAsync().ConfigureAwait(false);
                    return null;
                }
            }

            return null;
        }

        private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            DateTime currentManifestMtime = GetFileLastWriteTimeUtcSafe(_endpoint.ManifestPath);
            if (_client is not null &&
                _connectionInfo is not null &&
                currentManifestMtime != DateTime.MinValue &&
                currentManifestMtime == _connectionInfo.ManifestLastWriteUtc)
            {
                return true;
            }

            WorkspaceHostConnectionInfo? latest = _daemonManager.TryGetConnectionInfo(RepoRoot);
            if (latest is null)
            {
                await ResetConnectionAsync().ConfigureAwait(false);
                return false;
            }

            if (_client is not null &&
                _connectionInfo is not null &&
                _connectionInfo.ManifestLastWriteUtc == latest.ManifestLastWriteUtc &&
                _connectionInfo.DaemonPid == latest.DaemonPid &&
                string.Equals(_connectionInfo.AuthToken, latest.AuthToken, StringComparison.Ordinal))
            {
                return true;
            }

            await ResetConnectionAsync().ConfigureAwait(false);

            WorkspaceHostClientOptions options = latest.Endpoint.Transport == "named-pipe"
                ? WorkspaceHostClientOptions.NamedPipe(latest.Endpoint.PipeName!)
                : WorkspaceHostClientOptions.UnixSocket(latest.Endpoint.SocketPath!);

            try
            {
                _client = await WorkspaceHostClient.ConnectAsync(options, ConnectTimeout, cancellationToken).ConfigureAwait(false);
                _connectionInfo = latest;
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or UnauthorizedAccessException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _client = null;
                _connectionInfo = null;
                return false;
            }
        }

        public async Task ResetConnectionAsync()
        {
            if (_client is not null)
            {
                try
                {
                    await _client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                _client = null;
            }

            _connectionInfo = null;
        }

        public async ValueTask DisposeAsync()
        {
            await ResetConnectionAsync().ConfigureAwait(false);
            Gate.Dispose();
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
