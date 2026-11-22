using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;
using Xunit;

namespace RoslynSkills.Cli.Tests;

public sealed class WorkspaceHostRobustnessTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task ThreeProjectFixture_ReloadStrictConcurrentStatusAndRestart_SucceedsWithoutRepoFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-mbvr-vr3g-{Guid.NewGuid():N}");
        string appDir = Path.Combine(tempRoot, "src", "App");
        string coreCsprojPath = Path.Combine(tempRoot, "src", "Core", "Core.csproj");
        Directory.CreateDirectory(tempRoot);

        WorkspaceHostDaemonManager manager = new();
        int? trackedDaemonPid = null;

        try
        {
            await CreateThreeProjectSlnxFixtureAsync(tempRoot);

            // 1. workspace.use must succeed and leave ZERO .roslynskills files inside the analysed directory.
            (int useExit, string useOut, string useErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.use", "ThreeProj.slnx", "--require-solution", "true" },
                requireDaemon: false);
            Assert.True(useExit == 0, $"workspace.use failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");
            Assert.False(Directory.Exists(Path.Combine(tempRoot, ".roslynskills")));
            Assert.False(File.Exists(Path.Combine(tempRoot, ".roslynskills", "workspaces.json")));

            WorkspaceAliasStore aliasStore = new(tempRoot);
            Assert.StartsWith(
                RepositoryRootDiscovery.GetWorkspaceHostStateDirectory(),
                aliasStore.Path,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(aliasStore.Path));

            int initialPid = AssertDaemonStatusRunning(await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false));
            trackedDaemonPid = initialPid;

            // 2. Explicit --mode reload must answer cleanly and leave daemon healthy.
            (int reloadExit, string reloadOut, string reloadErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.refresh", "--mode", "reload", "--repo-root", tempRoot },
                requireDaemon: false);
            Assert.True(reloadExit == 0, $"workspace.refresh --mode reload failed.\nSTDOUT:\n{reloadOut}\nSTDERR:\n{reloadErr}");
            using (JsonDocument reloadDoc = JsonDocument.Parse(reloadOut))
            {
                Assert.True(reloadDoc.RootElement.GetProperty("Ok").GetBoolean());
                Assert.Equal("daemon", reloadDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
                JsonElement innerData = reloadDoc.RootElement.GetProperty("Data");
                Assert.False(innerData.TryGetProperty("envelope", out _));
                Assert.Equal("reload", innerData.GetProperty("refresh_action").GetString());
            }

            // 3. Touch a .csproj file and run --mode strict (triggers full MSBuild reload).
            string coreCsproj = await File.ReadAllTextAsync(coreCsprojPath);
            await File.WriteAllTextAsync(coreCsprojPath, coreCsproj + "\n<!-- touched -->\n", Utf8NoBom);
            await Task.Delay(250);

            (int strictExit, string strictOut, string strictErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.refresh", "--mode", "strict", "--repo-root", tempRoot },
                requireDaemon: false);
            Assert.True(strictExit == 0, $"workspace.refresh --mode strict failed.\nSTDOUT:\n{strictOut}\nSTDERR:\n{strictErr}");
            using (JsonDocument strictDoc = JsonDocument.Parse(strictOut))
            {
                Assert.True(strictDoc.RootElement.GetProperty("Ok").GetBoolean());
                Assert.Equal("daemon", strictDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
                JsonElement innerData = strictDoc.RootElement.GetProperty("Data");
                Assert.False(innerData.TryGetProperty("envelope", out _));
                Assert.Equal("reload", innerData.GetProperty("refresh_action").GetString());
            }

            // 4. Concurrent daemon.status during workspace.refresh --mode reload must NOT block or return daemon_unavailable.
            Task<(int ExitCode, string StdOut, string StdErr)> concurrentReloadTask = RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.refresh", "--mode", "reload", "--repo-root", tempRoot },
                requireDaemon: false);
            await Task.Delay(150);
            (int concurrentStatusExit, string concurrentStatusOut, string concurrentStatusErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false,
                timeoutMilliseconds: 15_000);
            Assert.True(
                concurrentStatusExit == 0,
                $"daemon.status failed during concurrent reload.\nSTDOUT:\n{concurrentStatusOut}\nSTDERR:\n{concurrentStatusErr}");
            int statusPidDuringReload = AssertDaemonStatusRunning((concurrentStatusExit, concurrentStatusOut, concurrentStatusErr));
            Assert.Equal(initialPid, statusPidDuringReload);

            (int concurrentReloadExit, string concurrentReloadOut, string concurrentReloadErr) = await concurrentReloadTask;
            Assert.True(
                concurrentReloadExit == 0,
                $"concurrent workspace.refresh --mode reload failed.\nSTDOUT:\n{concurrentReloadOut}\nSTDERR:\n{concurrentReloadErr}");

            // 5. Subdirectory discovery in a non-git directory resolves via per-user alias store without --repo-root or --workspace-path.
            (int navExit, string navOut, string navErr) = await RunCliProcessAsync(
                workingDirectory: appDir,
                arguments: new[] { "nav.find_symbol", "Program.cs", "ComputeTotal", "--brief", "true" },
                requireDaemon: true);
            Assert.True(navExit == 0, $"nav.find_symbol from non-git subdir failed.\nSTDOUT:\n{navOut}\nSTDERR:\n{navErr}");
            Assert.Contains("process_hot", navOut);

            // 6. daemon.restart must stop the old host and start a healthy new host with a different PID.
            (int restartExit, string restartOut, string restartErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.restart", "--repo-root", tempRoot },
                requireDaemon: false);
            Assert.True(restartExit == 0, $"daemon.restart failed.\nSTDOUT:\n{restartOut}\nSTDERR:\n{restartErr}");
            int restartedPid;
            using (JsonDocument restartDoc = JsonDocument.Parse(restartOut))
            {
                Assert.True(restartDoc.RootElement.GetProperty("Ok").GetBoolean());
                JsonElement data = restartDoc.RootElement.GetProperty("Data");
                Assert.True(data.GetProperty("stop").GetProperty("stopped").GetBoolean());
                restartedPid = data.GetProperty("start").GetProperty("daemon_pid").GetInt32();
            }

            trackedDaemonPid = restartedPid;
            Assert.NotEqual(initialPid, restartedPid);
            Assert.True(await WaitForProcessIdExitAsync(initialPid, TimeSpan.FromSeconds(5)));

            int postRestartStatusPid = AssertDaemonStatusRunning(await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false));
            Assert.Equal(restartedPid, postRestartStatusPid);

            // 7. Rebind and close: per-user alias file is cleaned up and analysed repo stays clean.
            (int reUseExit, string reUseOut, string reUseErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.use", "ThreeProj.slnx", "--require-solution", "true" },
                requireDaemon: false);
            Assert.True(reUseExit == 0, $"workspace.use after restart failed.\nSTDOUT:\n{reUseOut}\nSTDERR:\n{reUseErr}");

            (int closeExit, string closeOut, string closeErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.close", "--repo-root", tempRoot },
                requireDaemon: false);
            Assert.True(closeExit == 0, $"workspace.close failed.\nSTDOUT:\n{closeOut}\nSTDERR:\n{closeErr}");
            Assert.False(File.Exists(aliasStore.Path));
            Assert.False(Directory.Exists(Path.Combine(tempRoot, ".roslynskills")));
        }
        finally
        {
            await CleanupTempDaemonAsync(tempRoot, manager, trackedDaemonPid);
        }
    }

    [Fact]
    public async Task WorkspaceHost_ClientDisconnectDuringReload_SurvivesAndAnswersSubsequentRequests()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-disconnect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        WorkspaceHostDaemonManager manager = new();
        int? trackedDaemonPid = null;

        try
        {
            await CreateThreeProjectSlnxFixtureAsync(tempRoot);

            (int useExit, string useOut, string useErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.use", "ThreeProj.slnx", "--require-solution", "true" },
                requireDaemon: false);
            Assert.True(useExit == 0, $"workspace.use failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");

            int initialPid = AssertDaemonStatusRunning(await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false));
            trackedDaemonPid = initialPid;

            WorkspaceHostDaemonEndpoint endpoint = manager.GetDefaultEndpoint(tempRoot);
            WorkspaceAliasStore aliasStore = new(tempRoot);
            Assert.True(aliasStore.TryGet("default", out WorkspaceAliasRecord? aliasRecord));
            Assert.NotNull(aliasRecord);
            Assert.False(string.IsNullOrWhiteSpace(aliasRecord.WorkspaceHandle));

            string? authToken;
            using (JsonDocument manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(endpoint.ManifestPath)))
            {
                authToken = manifestDoc.RootElement.GetProperty("AuthToken").GetString();
            }

            // Send workspace/refresh (mode=reload) and immediately drop the connection without reading the response.
            WorkspaceHostRequest reloadRequest = new(
                Id: Guid.NewGuid().ToString("N"),
                Method: WorkspaceHostProtocol.Method.WorkspaceRefresh,
                AuthToken: authToken,
                WorkspaceHandle: aliasRecord.WorkspaceHandle,
                WorkspaceAlias: "default",
                Input: JsonSerializer.SerializeToElement(new
                {
                    workspace_handle = aliasRecord.WorkspaceHandle,
                    mode = "reload",
                }));
            await SendRequestAndDisconnectImmediatelyAsync(endpoint, reloadRequest);

            // Wait for the disconnected reload to run and finish writing to the broken pipe,
            // then verify the SAME host process survived and still serves requests cleanly.
            (int statusExit, string statusOut, string statusErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.status", "--repo-root", tempRoot },
                requireDaemon: false);
            Assert.True(statusExit == 0, $"workspace.status failed after client disconnect.\nSTDOUT:\n{statusOut}\nSTDERR:\n{statusErr}");

            int afterPid = AssertDaemonStatusRunning(await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false));
            Assert.Equal(initialPid, afterPid);
        }
        finally
        {
            await CleanupTempDaemonAsync(tempRoot, manager, trackedDaemonPid);
        }
    }

    [Fact]
    public async Task DaemonRestart_UnresponsiveHostProcess_KillsOldProcessTreeAndStartsFreshHost()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-unresponsive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        WorkspaceHostDaemonManager manager = new();
        WorkspaceHostDaemonEndpoint endpoint = manager.GetDefaultEndpoint(tempRoot);
        Process? unresponsiveHost = null;
        Task<string>? stdoutDrain = null;
        Task<string>? stderrDrain = null;
        int? restartedPid = null;

        try
        {
            string hostDllPath = ResolveWorkspaceHostDllPath();
            ProcessStartInfo startInfo = new(ResolveDotnetExecutable())
            {
                WorkingDirectory = tempRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(hostDllPath);
            startInfo.ArgumentList.Add("--transport");
            startInfo.ArgumentList.Add("stdio");

            unresponsiveHost = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start dummy stdio workspace host.");
            stdoutDrain = unresponsiveHost.StandardOutput.ReadToEndAsync();
            stderrDrain = unresponsiveHost.StandardError.ReadToEndAsync();
            int unresponsivePid = unresponsiveHost.Id;

            // Point the daemon manifest at this live process that is NOT listening on the daemon endpoint.
            Directory.CreateDirectory(Path.GetDirectoryName(endpoint.ManifestPath)!);
            var fakeManifest = new
            {
                ManifestVersion = "1.0",
                Transport = endpoint.Transport,
                PipeName = endpoint.PipeName,
                SocketPath = endpoint.SocketPath,
                RepoRoot = endpoint.RepoRoot,
                HostPath = hostDllPath,
                AuthToken = Guid.NewGuid().ToString("N"),
                ProcessId = unresponsivePid,
                StartedAtUtc = DateTimeOffset.UtcNow,
            };
            await File.WriteAllTextAsync(
                endpoint.ManifestPath,
                JsonSerializer.Serialize(fakeManifest, new JsonSerializerOptions { WriteIndented = true }),
                Utf8NoBom);

            // Confirm daemon.status reports daemon_unavailable while the fake host stays alive.
            (int preStatusExit, string preStatusOut, string preStatusErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false,
                timeoutMilliseconds: 20_000);
            Assert.Equal(1, preStatusExit);
            Assert.Contains("daemon_unavailable", preStatusOut);
            Assert.False(unresponsiveHost.HasExited, $"Expected unresponsive host {unresponsivePid} to remain alive before restart.\nSTDERR:\n{preStatusErr}");

            // daemon.restart must force-kill the unresponsive host and bring up a fresh answering daemon.
            (int restartExit, string restartOut, string restartErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.restart", "--repo-root", tempRoot },
                requireDaemon: false,
                timeoutMilliseconds: 30_000);
            Assert.True(restartExit == 0, $"daemon.restart failed to recover unresponsive host.\nSTDOUT:\n{restartOut}\nSTDERR:\n{restartErr}");

            using (JsonDocument restartDoc = JsonDocument.Parse(restartOut))
            {
                Assert.True(restartDoc.RootElement.GetProperty("Ok").GetBoolean());
                JsonElement data = restartDoc.RootElement.GetProperty("Data");
                JsonElement stop = data.GetProperty("stop");
                JsonElement start = data.GetProperty("start");

                Assert.True(stop.GetProperty("stopped").GetBoolean());
                Assert.True(stop.GetProperty("forced_kill").GetBoolean());
                Assert.Equal(unresponsivePid, stop.GetProperty("daemon_pid").GetInt32());

                Assert.True(start.GetProperty("started").GetBoolean());
                restartedPid = start.GetProperty("daemon_pid").GetInt32();
                Assert.NotEqual(unresponsivePid, restartedPid);
            }

            Assert.True(
                await WaitForProcessExitAsync(unresponsiveHost, TimeSpan.FromSeconds(5)),
                $"Unresponsive host process {unresponsivePid} was still alive after daemon.restart.");

            int livePid = AssertDaemonStatusRunning(await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false,
                timeoutMilliseconds: 15_000));
            Assert.Equal(restartedPid, livePid);
        }
        finally
        {
            if (unresponsiveHost is not null)
            {
                TryKillProcessTree(unresponsiveHost);
                try { unresponsiveHost.WaitForExit(2000); } catch { }
                try
                {
                    if (stdoutDrain is not null && stderrDrain is not null)
                    {
                        await Task.WhenAny(Task.WhenAll(stdoutDrain, stderrDrain), Task.Delay(1000));
                    }
                }
                catch
                {
                }

                unresponsiveHost.Dispose();
            }

            await CleanupTempDaemonAsync(tempRoot, manager, restartedPid);
        }
    }

    [Fact]
    public async Task RepositoryRootDiscovery_UsesPerUserAliasesAndIgnoresLegacyRepoFile()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-discovery-{Guid.NewGuid():N}");
        string outerRoot = Path.Combine(tempRoot, "outer");
        string outerSubdir = Path.Combine(outerRoot, "src", "App");
        string nestedGitRepo = Path.Combine(outerRoot, "nested-repo");
        string nestedGitSubdir = Path.Combine(nestedGitRepo, "src", "Sub");
        Directory.CreateDirectory(outerSubdir);
        Directory.CreateDirectory(nestedGitSubdir);

        WorkspaceAliasStore outerStore = new(outerRoot);

        try
        {
            // Legacy .roslynskills/workspaces.json must be completely ignored.
            string legacyDir = Path.Combine(outerRoot, ".roslynskills");
            Directory.CreateDirectory(legacyDir);
            await File.WriteAllTextAsync(Path.Combine(legacyDir, "workspaces.json"), "{}", Utf8NoBom);

            string resolvedWithoutPerUserAlias = RepositoryRootDiscovery.ResolveWorkspaceDaemonRoot(
                explicitRepoRoot: null,
                startPath: outerSubdir);
            Assert.Equal(
                RepositoryRootDiscovery.NormalizePathRoot(outerSubdir),
                resolvedWithoutPerUserAlias);

            // Once an alias is persisted in the per-user state store, nested non-git subdirectories resolve to outerRoot.
            outerStore.Upsert(
                "default",
                new WorkspaceAliasRecord(
                    WorkspacePath: Path.Combine(outerRoot, "Outer.slnx"),
                    WorkspaceHandle: "ws_outer",
                    DaemonEndpoint: "pipe:test",
                    DaemonPid: 42,
                    WorkspaceFingerprint: "fp",
                    LastSeenUtc: DateTimeOffset.UtcNow));

            string resolvedWithPerUserAlias = RepositoryRootDiscovery.ResolveWorkspaceDaemonRoot(
                explicitRepoRoot: null,
                startPath: outerSubdir);
            Assert.Equal(
                RepositoryRootDiscovery.NormalizePathRoot(outerRoot),
                resolvedWithPerUserAlias);

            // A nested Git repo/worktree must stop at its own .git boundary and never inherit outerRoot's daemon.
            Directory.CreateDirectory(Path.Combine(nestedGitRepo, ".git"));
            string resolvedNestedGit = RepositoryRootDiscovery.ResolveWorkspaceDaemonRoot(
                explicitRepoRoot: null,
                startPath: nestedGitSubdir);
            Assert.Equal(
                RepositoryRootDiscovery.NormalizePathRoot(nestedGitRepo),
                resolvedNestedGit);
        }
        finally
        {
            outerStore.Clear();
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task SendRequestAndDisconnectImmediatelyAsync(
        WorkspaceHostDaemonEndpoint endpoint,
        WorkspaceHostRequest request)
    {
        string line = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = false }) + "\n";
        byte[] payload = Utf8NoBom.GetBytes(line);

        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(5));
        if (endpoint.Transport == "named-pipe")
        {
            await using NamedPipeClientStream pipe = new(".", endpoint.PipeName!, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectCts.Token);
            await pipe.WriteAsync(payload, connectCts.Token);
            await pipe.FlushAsync(connectCts.Token);
            await Task.Delay(100, connectCts.Token);
            return;
        }

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath!), connectCts.Token);
        await socket.SendAsync(payload, SocketFlags.None, connectCts.Token);
        await Task.Delay(100, connectCts.Token);
    }

    private static int AssertDaemonStatusRunning((int ExitCode, string StdOut, string StdErr) result)
    {
        Assert.True(result.ExitCode == 0, $"daemon.status failed.\nSTDOUT:\n{result.StdOut}\nSTDERR:\n{result.StdErr}");
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.True(doc.RootElement.GetProperty("Ok").GetBoolean());
        JsonElement data = doc.RootElement.GetProperty("Data");
        Assert.True(data.GetProperty("running").GetBoolean());
        return data.GetProperty("daemon_pid").GetInt32();
    }

    private static async Task CreateThreeProjectSlnxFixtureAsync(string root)
    {
        string coreDir = Path.Combine(root, "src", "Core");
        string domainDir = Path.Combine(root, "src", "Domain");
        string appDir = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(domainDir);
        Directory.CreateDirectory(appDir);

        await File.WriteAllTextAsync(
            Path.Combine(coreDir, "Core.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(coreDir, "MathUtil.cs"),
            """
            namespace Core;

            public static class MathUtil
            {
                public static int Add(int a, int b) => a + b;
            }
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(domainDir, "Domain.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(domainDir, "Calculator.cs"),
            """
            using Core;

            namespace Domain;

            public sealed class Calculator
            {
                public int Sum(int x, int y) => MathUtil.Add(x, y);
            }
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(appDir, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Domain\Domain.csproj" />
              </ItemGroup>
            </Project>
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(appDir, "Program.cs"),
            """
            using Domain;

            namespace App;

            public sealed class Runner
            {
                public int ComputeTotal(int a, int b) => new Calculator().Sum(a, b);
            }
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(root, "ThreeProj.slnx"),
            """
            <Solution>
              <Project Path="src/Core/Core.csproj" />
              <Project Path="src/Domain/Domain.csproj" />
              <Project Path="src/App/App.csproj" />
            </Solution>
            """,
            Utf8NoBom);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliProcessAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool requireDaemon,
        int timeoutMilliseconds = 120_000)
    {
        string cliDllPath = typeof(CliApplication).Assembly.Location;
        ProcessStartInfo startInfo = new(ResolveDotnetExecutable())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliDllPath);
        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ROSCLI_DAEMON"] = requireDaemon ? "required" : "auto";
        startInfo.Environment.Remove("ROSCLI_WORKSPACE_ALIAS");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roscli child process.");
        try
        {
            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource waitCts = new(timeoutMilliseconds);
            try
            {
                await process.WaitForExitAsync(waitCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw new TimeoutException($"roscli process timed out after {timeoutMilliseconds}ms for args: {string.Join(' ', arguments)}");
            }

            Task allDrains = Task.WhenAll(stdOutTask, stdErrTask);
            if (await Task.WhenAny(allDrains, Task.Delay(TimeSpan.FromSeconds(10))) != allDrains)
            {
                TryKillProcessTree(process);
                throw new TimeoutException(
                    $"roscli process exited (code={process.ExitCode}) but stdout/stderr did not close within 10s for args: {string.Join(' ', arguments)}.");
            }

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        finally
        {
            TryKillProcessTree(process);
        }
    }

    private static string ResolveWorkspaceHostDllPath()
    {
        string sibling = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.WorkspaceHost.dll");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RoslynSkills.slnx")))
            {
                foreach (string config in new[] { "Release", "Debug" })
                {
                    string candidate = Path.Combine(
                        current.FullName,
                        "src",
                        "RoslynSkills.WorkspaceHost",
                        "bin",
                        config,
                        "net10.0",
                        "RoslynSkills.WorkspaceHost.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate RoslynSkills.WorkspaceHost.dll for test.");
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

        return "dotnet";
    }

    private static async Task CleanupTempDaemonAsync(
        string tempRoot,
        WorkspaceHostDaemonManager manager,
        int? trackedPid)
    {
        int? manifestPid = null;
        try
        {
            manifestPid = manager.TryGetDaemonProcessId(tempRoot);
            using CancellationTokenSource stopCts = new(TimeSpan.FromSeconds(15));
            await manager.StopAsync(tempRoot, stopCts.Token);
        }
        catch
        {
        }
        finally
        {
            TryKillProcessById(manifestPid);
            TryKillProcessById(trackedPid);
            try { new WorkspaceAliasStore(tempRoot).Clear(); } catch { }
            try
            {
                string manifestPath = RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(tempRoot);
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { return process.HasExited; } catch { return true; }
        }
    }

    private static async Task<bool> WaitForProcessIdExitAsync(int pid, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
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

    private static void TryKillProcessById(int? processId)
    {
        if (processId is null or <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
