using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;
using RoslynSkills.Core;
using RoslynSkills.Core.Commands;
using Xunit;

namespace RoslynSkills.Cli.Tests;

public sealed class DaemonSurvivalAndErgonomicsTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void BinaryLaunchMode_DetectsGlobalToolPublishedCacheCompiledBinaryAndDotnetExec()
    {
        Assert.Equal(
            "published_cache",
            CliApplication.ResolveBinaryLaunchMode(
                baseDirectory: @"C:\Users\dev\.dotnet\tools\.store\roslynskills.cli\0.1.6\tools\net10.0\any\",
                processPath: @"C:\Program Files\dotnet\dotnet.exe",
                publishedModeEnabled: true));

        Assert.Equal(
            "global_tool",
            CliApplication.ResolveBinaryLaunchMode(
                baseDirectory: @"C:\Users\dev\.dotnet\tools\.store\roslynskills.cli\0.1.6-preview.27\roslynskills.cli\0.1.6-preview.27\tools\net10.0\any\",
                processPath: @"C:\Program Files\dotnet\dotnet.exe",
                publishedModeEnabled: false));

        Assert.Equal(
            "global_tool",
            CliApplication.ResolveBinaryLaunchMode(
                baseDirectory: "/home/dev/.dotnet/tools/.store/roslynskills.cli/0.1.6/tools/net10.0/any/",
                processPath: "/usr/bin/dotnet",
                publishedModeEnabled: false));

        Assert.Equal(
            "compiled_binary",
            CliApplication.ResolveBinaryLaunchMode(
                baseDirectory: @"C:\tools\roscli\",
                processPath: @"C:\tools\roscli\roscli.exe",
                publishedModeEnabled: false));

        Assert.Equal(
            "dotnet_exec",
            CliApplication.ResolveBinaryLaunchMode(
                baseDirectory: @"C:\repo\src\RoslynSkills.Cli\bin\Debug\net10.0\",
                processPath: @"C:\Program Files\dotnet\dotnet.exe",
                publishedModeEnabled: false));
    }

    [Fact]
    public void FileLineColumnParser_HandlesWindowsDrivePathsAndRejectsInvalidTokens()
    {
        Assert.True(CliApplication.TrySplitFileLineColumnToken(
            @"C:\Work\DnaCalc\CalcDb\TableCursor.cs:123:10",
            out string winPath,
            out int winLine,
            out int winCol));
        Assert.Equal(@"C:\Work\DnaCalc\CalcDb\TableCursor.cs", winPath);
        Assert.Equal(123, winLine);
        Assert.Equal(10, winCol);

        Assert.True(CliApplication.TrySplitFileLineColumnToken(
            "src/App/Program.cs:7:15",
            out string relPath,
            out int relLine,
            out int relCol));
        Assert.Equal("src/App/Program.cs", relPath);
        Assert.Equal(7, relLine);
        Assert.Equal(15, relCol);

        Assert.False(CliApplication.TrySplitFileLineColumnToken(@"C:\Work\Program.cs", out _, out _, out _));
        Assert.False(CliApplication.TrySplitFileLineColumnToken("Program.cs:0:5", out _, out _, out _));
        Assert.False(CliApplication.TrySplitFileLineColumnToken("Program.cs:12:abc", out _, out _, out _));
        Assert.False(CliApplication.TrySplitFileLineColumnToken(":12:5", out _, out _, out _));
    }

    [Fact]
    public async Task DeadHost_FailsWithinSeconds_WithReasonPidAndLastLogLine()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-deadhost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        int? daemonPid = null;

        try
        {
            string programPath = await CreateSingleProjectSlnxFixtureAsync(tempRoot, "DeadHostFixture", "Greet");

            (int useExit, string useOut, string useErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.use", "DeadHostFixture.slnx", "--require-solution", "true" },
                requireDaemon: false);
            Assert.True(useExit == 0, $"workspace.use failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");

            (int statusExit, string statusOut, string statusErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false);
            Assert.True(statusExit == 0, $"daemon.status failed.\nSTDOUT:\n{statusOut}\nSTDERR:\n{statusErr}");

            using (JsonDocument statusDoc = JsonDocument.Parse(statusOut))
            {
                JsonElement data = statusDoc.RootElement.GetProperty("Data");
                daemonPid = data.GetProperty("daemon_pid").GetInt32();
                string? logPath = data.GetProperty("log_path").GetString();
                Assert.False(string.IsNullOrWhiteSpace(logPath));
                Assert.True(File.Exists(logPath!), $"Expected daemon log file to exist at '{logPath}'.");
            }

            // Simulate an abrupt external daemon death (e.g. OOM or external termination) leaving the manifest behind.
            KillProcessTreeById(daemonPid!.Value);
            await WaitForProcessIdGoneAsync(daemonPid.Value, TimeSpan.FromSeconds(5));

            Stopwatch sw = Stopwatch.StartNew();
            (int navExit, string navOut, string navErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", $"{programPath}:5:24" },
                requireDaemon: true,
                timeoutMilliseconds: 15_000);
            sw.Stop();

            Assert.NotEqual(0, navExit);
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(10),
                $"Dead host call took {sw.Elapsed.TotalSeconds:F2}s (expected <10s, not 300s).\nSTDOUT:\n{navOut}\nSTDERR:\n{navErr}");

            using JsonDocument navDoc = JsonDocument.Parse(navOut);
            Assert.False(navDoc.RootElement.GetProperty("Ok").GetBoolean());
            JsonElement error = navDoc.RootElement.GetProperty("Errors")[0];
            Assert.Equal(WorkspaceHostProtocol.ErrorCode.DaemonUnavailable, error.GetProperty("Code").GetString());
            Assert.Contains("exited", error.GetProperty("Message").GetString(), StringComparison.OrdinalIgnoreCase);

            JsonElement details = error.GetProperty("Details");
            Assert.Equal("host_exited", details.GetProperty("reason").GetString());
            Assert.Equal(daemonPid.Value, details.GetProperty("daemon_pid").GetInt32());
            Assert.Contains("host_started", details.GetProperty("last_log_line").GetString() ?? string.Empty, StringComparison.Ordinal);

            double roundtripMs = navDoc.RootElement
                .GetProperty("Transport")
                .GetProperty("timings")
                .GetProperty("roundtrip_ms")
                .GetDouble();
            Assert.True(roundtripMs < 5_000, $"Expected fast roundtrip failure (<5000 ms), got {roundtripMs} ms.");
        }
        finally
        {
            await CleanupDaemonAndTempRootAsync(tempRoot, daemonPid);
        }
    }

    [Fact]
    public async Task DefaultWorkspace_SnapshotAnalyzeFileLineColAndSymbolLookup_WorkOnHotDaemon()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-ergonomics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        int? daemonPid = null;

        try
        {
            string programPath = await CreateSingleProjectSlnxFixtureAsync(tempRoot, "ErgonomicsFixture", "ComputeValue");

            (int useExit, string useOut, string useErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "workspace.use", "ErgonomicsFixture.slnx", "--require-solution", "true" },
                requireDaemon: false);
            Assert.True(useExit == 0, $"workspace.use failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");

            daemonPid = new WorkspaceHostDaemonManager().TryGetDaemonProcessId(tempRoot);

            // 1. diag.get_workspace_snapshot --compact uses the default workspace without --workspace-path.
            (int snapExit, string snapOut, string snapErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "diag.get_workspace_snapshot", "--compact" },
                requireDaemon: true);
            Assert.True(snapExit == 0, $"diag.get_workspace_snapshot --compact failed.\nSTDOUT:\n{snapOut}\nSTDERR:\n{snapErr}");
            using (JsonDocument snapDoc = JsonDocument.Parse(snapOut))
            {
                Assert.True(snapDoc.RootElement.GetProperty("Ok").GetBoolean());
                Assert.Equal("daemon", snapDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
                Assert.Equal("default", snapDoc.RootElement.GetProperty("Transport").GetProperty("routed_alias").GetString());
            }

            // 2. analyze.* commands use the default workspace with no workspace_path argument.
            foreach (string[] cmdArgs in new[]
            {
                new[] { "analyze.unused_private_symbols" },
                new[] { "analyze.async_risk_scan" },
                new[] { "analyze.override_coverage" },
                new[] { "analyze.dependency_violations", "ErgonomicsFixture", "System" },
            })
            {
                (int cmdExit, string cmdOut, string cmdErr) = await RunCliProcessAsync(
                    workingDirectory: tempRoot,
                    arguments: cmdArgs,
                    requireDaemon: true);
                Assert.True(cmdExit == 0, $"{string.Join(' ', cmdArgs)} failed.\nSTDOUT:\n{cmdOut}\nSTDERR:\n{cmdErr}");
                using JsonDocument cmdDoc = JsonDocument.Parse(cmdOut);
                Assert.True(cmdDoc.RootElement.GetProperty("Ok").GetBoolean());
                Assert.Equal("daemon", cmdDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
            }

            // 3. nav.find_references accepts both relative and absolute file:line:col shorthand.
            (int relExit, string relOut, string relErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", "Program.cs:5:24" },
                requireDaemon: true);
            Assert.True(relExit == 0, $"nav.find_references relative file:line:col failed.\nSTDOUT:\n{relOut}\nSTDERR:\n{relErr}");
            using (JsonDocument relDoc = JsonDocument.Parse(relOut))
            {
                Assert.True(relDoc.RootElement.GetProperty("Ok").GetBoolean());
                Assert.True(relDoc.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32() >= 2);
            }

            (int absExit, string absOut, string absErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", $"{programPath}:5:24" },
                requireDaemon: true);
            Assert.True(absExit == 0, $"nav.find_references absolute file:line:col failed.\nSTDOUT:\n{absOut}\nSTDERR:\n{absErr}");

            // 4. nav.find_references --symbol Type.Member resolves via the default hot workspace.
            (int symExit, string symOut, string symErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", "--symbol", "Greeter.ComputeValue" },
                requireDaemon: true);
            Assert.True(symExit == 0, $"nav.find_references --symbol failed.\nSTDOUT:\n{symOut}\nSTDERR:\n{symErr}");
            using (JsonDocument symDoc = JsonDocument.Parse(symOut))
            {
                Assert.True(symDoc.RootElement.GetProperty("Ok").GetBoolean());
                JsonElement data = symDoc.RootElement.GetProperty("Data");
                Assert.Contains("ComputeValue", data.GetProperty("symbol").GetProperty("symbol_display").GetString());
                Assert.True(data.GetProperty("total_matches").GetInt32() >= 2);
            }

            // 5. invalid_args names both <file-path> <line> <column> and <file-path>:<line>:<column>.
            (int badExit, string badOut, string _) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", "Program.cs" },
                requireDaemon: false);
            Assert.NotEqual(0, badExit);
            using (JsonDocument badDoc = JsonDocument.Parse(badOut))
            {
                JsonElement error = badDoc.RootElement.GetProperty("Errors")[0];
                Assert.Equal("invalid_args", error.GetProperty("Code").GetString());
                string message = error.GetProperty("Message").GetString() ?? string.Empty;
                Assert.Contains("<file-path> <line> <column>", message, StringComparison.Ordinal);
                Assert.Contains("<file-path>:<line>:<column>", message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await CleanupDaemonAndTempRootAsync(tempRoot, daemonPid);
        }
    }

    [Fact]
    public async Task TwoGitWorktreesOfOneRepo_BothHostsAnswerOwnClientsConcurrentlyWithBuild()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-two-wt-{Guid.NewGuid():N}");
        string mainRepo = Path.Combine(tempRoot, "main");
        string wtA = Path.Combine(tempRoot, "wt-a");
        string wtB = Path.Combine(tempRoot, "wt-b");
        Directory.CreateDirectory(mainRepo);
        int? pidA = null;
        int? pidB = null;

        try
        {
            await CreateSingleProjectSlnxFixtureAsync(mainRepo, "SharedRepo", "SharedMethod");
            RunGit(mainRepo, "init");
            RunGit(mainRepo, "config user.email test@example.com");
            RunGit(mainRepo, "config user.name TestUser");
            RunGit(mainRepo, "add .");
            RunGit(mainRepo, "commit -m init");

            RunGit(mainRepo, $"worktree add \"{wtA}\" -b branch-a");
            RunGit(mainRepo, $"worktree add \"{wtB}\" -b branch-b");

            // Give each worktree its own unique method name so we prove zero cross-talk between hosts.
            string progA = Path.Combine(wtA, "Program.cs");
            string progB = Path.Combine(wtB, "Program.cs");
            await File.WriteAllTextAsync(progA, (await File.ReadAllTextAsync(progA)).Replace("SharedMethod", "MethodOnlyInA"), Utf8NoBom);
            await File.WriteAllTextAsync(progB, (await File.ReadAllTextAsync(progB)).Replace("SharedMethod", "MethodOnlyInB"), Utf8NoBom);

            (int useAExit, string useAOut, string useAErr) = await RunCliProcessAsync(
                workingDirectory: wtA,
                arguments: new[] { "workspace.use", "SharedRepo.slnx", "--require-solution", "true" },
                requireDaemon: false);
            Assert.True(useAExit == 0, $"wt-a workspace.use failed.\nSTDOUT:\n{useAOut}\nSTDERR:\n{useAErr}");

            (int useBExit, string useBOut, string useBErr) = await RunCliProcessAsync(
                workingDirectory: wtB,
                arguments: new[] { "workspace.use", "SharedRepo.slnx", "--require-solution", "true" },
                requireDaemon: false);
            Assert.True(useBExit == 0, $"wt-b workspace.use failed.\nSTDOUT:\n{useBOut}\nSTDERR:\n{useBErr}");

            WorkspaceHostDaemonManager manager = new();
            pidA = manager.TryGetDaemonProcessId(wtA);
            pidB = manager.TryGetDaemonProcessId(wtB);
            Assert.NotNull(pidA);
            Assert.NotNull(pidB);
            Assert.NotEqual(pidA.Value, pidB.Value);
            Assert.NotEqual(
                WorkspaceHostDaemonManager.FormatEndpoint(manager.GetDefaultEndpoint(wtA)),
                WorkspaceHostDaemonManager.FormatEndpoint(manager.GetDefaultEndpoint(wtB)));

            // Run a dotnet build in wt-a while querying BOTH worktree daemons concurrently.
            Task<(int ExitCode, string StdOut, string StdErr)> buildTask = RunProcessAsync(
                fileName: "dotnet",
                workingDirectory: wtA,
                arguments: new[] { "build", "SharedRepo.csproj", "--nologo", "--verbosity", "quiet" },
                environmentOverrides: null,
                timeoutMilliseconds: 60_000);
            Task<(int ExitCode, string StdOut, string StdErr)> queryATask = RunCliProcessAsync(
                workingDirectory: wtA,
                arguments: new[] { "nav.find_references", "--symbol", "Greeter.MethodOnlyInA" },
                requireDaemon: true);
            Task<(int ExitCode, string StdOut, string StdErr)> queryBTask = RunCliProcessAsync(
                workingDirectory: wtB,
                arguments: new[] { "nav.find_references", "--symbol", "Greeter.MethodOnlyInB" },
                requireDaemon: true);

            await Task.WhenAll(buildTask, queryATask, queryBTask);
            (int buildExit, string buildOut, string buildErr) = await buildTask;
            (int queryAExit, string queryAOut, string queryAErr) = await queryATask;
            (int queryBExit, string queryBOut, string queryBErr) = await queryBTask;
            Assert.True(buildExit == 0, $"dotnet build in wt-a failed.\n{buildOut}\n{buildErr}");
            Assert.True(queryAExit == 0, $"wt-a nav failed.\n{queryAOut}\n{queryAErr}");
            Assert.True(queryBExit == 0, $"wt-b nav failed.\n{queryBOut}\n{queryBErr}");

            Assert.Contains("MethodOnlyInA", queryAOut, StringComparison.Ordinal);
            Assert.Contains("MethodOnlyInB", queryBOut, StringComparison.Ordinal);

            // Both hosts must still be alive after the concurrent build and queries.
            (int stAExit, string stAOut, string stAErr) = await RunCliProcessAsync(wtA, new[] { "daemon.status", "--repo-root", wtA }, requireDaemon: false);
            (int stBExit, string stBOut, string stBErr) = await RunCliProcessAsync(wtB, new[] { "daemon.status", "--repo-root", wtB }, requireDaemon: false);
            Assert.True(stAExit == 0, $"wt-a daemon.status failed.\n{stAOut}\n{stAErr}");
            Assert.True(stBExit == 0, $"wt-b daemon.status failed.\n{stBOut}\n{stBErr}");
        }
        finally
        {
            await CleanupDaemonAndTempRootAsync(wtA, pidA);
            await CleanupDaemonAndTempRootAsync(wtB, pidB);
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

    [Fact]
    public async Task WindowsKillOnJobClose_WithoutBreakaway_NoDetachReportsCallerJobAndFailsFastAfterShellExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-jobobj-caller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        int? daemonPid = null;

        try
        {
            _ = await CreateSingleProjectSlnxFixtureAsync(tempRoot, "CallerJobFixture", "RunJobCheck");

            string cliDll = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
            string dotnetExe = ResolveDotnetPath();
            string cmdLine = WorkspaceHostDaemonManager.BuildWindowsCommandLine(
                dotnetExe,
                new[] { cliDll, "workspace.use", "CallerJobFixture.slnx", "--require-solution", "true", "--nodetach" });

            (int childExitCode, string useOut, string useErr) = RunCommandInsideKillOnCloseJobWindows(
                cmdLine,
                tempRoot,
                allowBreakaway: false,
                TimeSpan.FromSeconds(60));
            Assert.True(childExitCode == 0, $"workspace.use --nodetach inside no-breakaway job failed ({childExitCode}).\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");

            using (JsonDocument useDoc = JsonDocument.Parse(useOut))
            {
                Assert.True(useDoc.RootElement.GetProperty("Ok").GetBoolean());
                JsonElement data = useDoc.RootElement.GetProperty("Data");
                Assert.Equal("caller_job", data.GetProperty("launch_mode").GetString());
                Assert.Equal(WorkspaceHostDaemonManager.LifetimeCallerJob, data.GetProperty("daemon_lifetime").GetString());
                Assert.Contains("KILL_ON_JOB_CLOSE", data.GetProperty("caller_job_flags").GetString() ?? string.Empty, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("launch_warning").GetString()));
                string dataNote = data.GetProperty("daemon_lifetime_note").GetString() ?? string.Empty;
                Assert.Contains("KILL_ON_JOB_CLOSE", dataNote, StringComparison.Ordinal);
                Assert.Contains("daemon.run", dataNote, StringComparison.Ordinal);

                JsonElement transport = useDoc.RootElement.GetProperty("Transport");
                Assert.Equal("caller_job", transport.GetProperty("launch_mode").GetString());
                Assert.Equal(WorkspaceHostDaemonManager.LifetimeCallerJob, transport.GetProperty("daemon_lifetime").GetString());
                Assert.Contains("KILL_ON_JOB_CLOSE", transport.GetProperty("caller_job_flags").GetString() ?? string.Empty, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(transport.GetProperty("daemon_lifetime_note").GetString()));
                daemonPid = data.GetProperty("daemon_pid").GetInt32();
            }

            // The parent job object is now closed and the daemon inside it was terminated by Windows.
            Assert.NotNull(daemonPid);
            await WaitForProcessIdGoneAsync(daemonPid.Value, TimeSpan.FromSeconds(5));

            Stopwatch sw = Stopwatch.StartNew();
            (int navExit, string navOut, string navErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", "Program.cs:5:24" },
                requireDaemon: true,
                timeoutMilliseconds: 15_000);
            sw.Stop();

            Assert.NotEqual(0, navExit);
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(10),
                $"Dead caller_job host call took {sw.Elapsed.TotalSeconds:F2}s (expected <10s).\nSTDOUT:\n{navOut}\nSTDERR:\n{navErr}");

            using JsonDocument navDoc = JsonDocument.Parse(navOut);
            Assert.False(navDoc.RootElement.GetProperty("Ok").GetBoolean());
            JsonElement error = navDoc.RootElement.GetProperty("Errors")[0];
            Assert.Equal(WorkspaceHostProtocol.ErrorCode.DaemonUnavailable, error.GetProperty("Code").GetString());
            string message = error.GetProperty("Message").GetString() ?? string.Empty;
            Assert.Contains("caller_job", message, StringComparison.Ordinal);
            Assert.Contains("daemon.run", message, StringComparison.Ordinal);

            JsonElement details = error.GetProperty("Details");
            Assert.Equal("host_exited", details.GetProperty("reason").GetString());
            Assert.Equal(WorkspaceHostDaemonManager.LifetimeCallerJob, details.GetProperty("daemon_lifetime").GetString());
            Assert.Equal("caller_job", details.GetProperty("launch_mode").GetString());
            Assert.Contains("KILL_ON_JOB_CLOSE", details.GetProperty("caller_job_flags").GetString() ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(daemonPid.Value, details.GetProperty("daemon_pid").GetInt32());

            JsonElement navTransport = navDoc.RootElement.GetProperty("Transport");
            Assert.Equal(WorkspaceHostDaemonManager.LifetimeCallerJob, navTransport.GetProperty("daemon_lifetime").GetString());

            // Dead-PID stale manifest should be removed after reporting host_exited.
            Assert.False(File.Exists(RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(tempRoot)));
        }
        finally
        {
            await CleanupDaemonAndTempRootAsync(tempRoot, daemonPid);
        }
    }

    [Fact]
    public async Task WindowsKillOnJobClose_WithoutBreakaway_DefaultLaunchesDetachedViaWmiWithLoggedWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-jobobj-wmi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        int? daemonPid = null;

        try
        {
            _ = await CreateSingleProjectSlnxFixtureAsync(tempRoot, "WmiJobFixture", "RunWmiCheck");

            string cliDll = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
            string dotnetExe = ResolveDotnetPath();
            string cmdLine = WorkspaceHostDaemonManager.BuildWindowsCommandLine(
                dotnetExe,
                new[] { cliDll, "workspace.use", "WmiJobFixture.slnx", "--require-solution", "true", "--idle-timeout-seconds", "60" });

            (int childExitCode, string useOut, string useErr) = RunCommandInsideKillOnCloseJobWindows(
                cmdLine,
                tempRoot,
                allowBreakaway: false,
                TimeSpan.FromSeconds(60));
            Assert.True(childExitCode == 0, $"workspace.use inside no-breakaway job (default WMI) failed ({childExitCode}).\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");

            using (JsonDocument useDoc = JsonDocument.Parse(useOut))
            {
                Assert.True(useDoc.RootElement.GetProperty("Ok").GetBoolean());
                JsonElement data = useDoc.RootElement.GetProperty("Data");
                Assert.Equal("wmi_win32_process", data.GetProperty("launch_mode").GetString());
                Assert.Equal(WorkspaceHostDaemonManager.LifetimeDetached, data.GetProperty("daemon_lifetime").GetString());
                Assert.Contains("KILL_ON_JOB_CLOSE", data.GetProperty("caller_job_flags").GetString() ?? string.Empty, StringComparison.Ordinal);

                string warning = data.GetProperty("launch_warning").GetString() ?? string.Empty;
                Assert.Contains("KILL_ON_JOB_CLOSE", warning, StringComparison.Ordinal);
                Assert.Contains("caller_chain=", warning, StringComparison.Ordinal);
                Assert.Contains("WMI", warning, StringComparison.Ordinal);

                JsonElement transport = useDoc.RootElement.GetProperty("Transport");
                Assert.Equal("wmi_win32_process", transport.GetProperty("launch_mode").GetString());
                Assert.Equal(WorkspaceHostDaemonManager.LifetimeDetached, transport.GetProperty("daemon_lifetime").GetString());
                Assert.Equal(warning, transport.GetProperty("launch_warning").GetString());
                daemonPid = data.GetProperty("daemon_pid").GetInt32();
            }

            Assert.NotNull(daemonPid);
            string logPath = RepositoryRootDiscovery.GetWorkspaceDaemonLogPath(tempRoot);
            Assert.True(File.Exists(logPath), $"Expected host log at '{logPath}'.");
            string logText = await File.ReadAllTextAsync(logPath);
            Assert.Contains("launch_warning", logText, StringComparison.Ordinal);
            Assert.Contains("KILL_ON_JOB_CLOSE", logText, StringComparison.Ordinal);
            Assert.Contains("caller_chain=", logText, StringComparison.Ordinal);

            // The parent job object is now closed; the WMI-detached daemon must survive and serve queries.
            (int navExit, string navOut, string navErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", "Program.cs:5:24" },
                requireDaemon: true,
                timeoutMilliseconds: 15_000);
            Assert.True(navExit == 0, $"WMI-detached daemon did not survive KillOnJobClose job termination.\nSTDOUT:\n{navOut}\nSTDERR:\n{navErr}");
            using JsonDocument navDoc = JsonDocument.Parse(navOut);
            Assert.True(navDoc.RootElement.GetProperty("Ok").GetBoolean());
            Assert.Equal("daemon", navDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
            Assert.True(navDoc.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32() >= 2);
        }
        finally
        {
            await CleanupDaemonAndTempRootAsync(tempRoot, daemonPid);
            if (daemonPid.HasValue)
            {
                await WaitForProcessIdGoneAsync(daemonPid.Value, TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task WindowsKillOnJobClose_WithBreakawayAllowed_DaemonSurvivesAndReportsDetached()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-jobobj-breakaway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        int? daemonPid = null;

        try
        {
            _ = await CreateSingleProjectSlnxFixtureAsync(tempRoot, "BreakawayFixture", "RunBreakawayCheck");

            string cliDll = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
            string dotnetExe = ResolveDotnetPath();
            string cmdLine = WorkspaceHostDaemonManager.BuildWindowsCommandLine(
                dotnetExe,
                new[] { cliDll, "workspace.use", "BreakawayFixture.slnx", "--require-solution", "true" });

            (int childExitCode, string useOut, string useErr) = RunCommandInsideKillOnCloseJobWindows(
                cmdLine,
                tempRoot,
                allowBreakaway: true,
                TimeSpan.FromSeconds(60));
            Assert.True(childExitCode == 0, $"workspace.use inside breakaway-ok job failed ({childExitCode}).\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");

            WorkspaceHostDaemonManager manager = new();
            daemonPid = manager.TryGetDaemonProcessId(tempRoot);
            Assert.NotNull(daemonPid);

            using (JsonDocument useDoc = JsonDocument.Parse(useOut))
            {
                Assert.True(useDoc.RootElement.GetProperty("Ok").GetBoolean());
                JsonElement data = useDoc.RootElement.GetProperty("Data");
                Assert.Equal("create_process_breakaway", data.GetProperty("launch_mode").GetString());
                Assert.Equal(WorkspaceHostDaemonManager.LifetimeDetached, data.GetProperty("daemon_lifetime").GetString());
                Assert.False(data.TryGetProperty("launch_warning", out _), "Breakaway launch should not emit launch_warning.");
                Assert.Equal(WorkspaceHostDaemonManager.LifetimeDetached, useDoc.RootElement.GetProperty("Transport").GetProperty("daemon_lifetime").GetString());
                Assert.Equal("create_process_breakaway", useDoc.RootElement.GetProperty("Transport").GetProperty("launch_mode").GetString());
            }

            // The parent job object is now closed; a broken-away daemon must survive and serve queries.
            (int navExit, string navOut, string navErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", "Program.cs:5:24" },
                requireDaemon: true,
                timeoutMilliseconds: 15_000);
            Assert.True(navExit == 0, $"Daemon did not survive KillOnJobClose+BreakawayOk job termination.\nSTDOUT:\n{navOut}\nSTDERR:\n{navErr}");
            using JsonDocument navDoc = JsonDocument.Parse(navOut);
            Assert.True(navDoc.RootElement.GetProperty("Ok").GetBoolean());
            Assert.Equal("daemon", navDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
            Assert.True(navDoc.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32() >= 2);
        }
        finally
        {
            await CleanupDaemonAndTempRootAsync(tempRoot, daemonPid);
        }
    }

    [Fact]
    public async Task ConcurrentWorkspaceUse_ReusesSingleHostProcess()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-concurrent-use-{Guid.NewGuid():N}");
        string stateDir = Path.Combine(tempRoot, ".state");
        string repoDir = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            ["ROSLYNSKILLS_WORKSPACE_HOST_DIR"] = stateDir,
        };
        int? daemonPid = null;

        try
        {
            _ = await CreateSingleProjectSlnxFixtureAsync(repoDir, "ConcurrentFixture", "Ping");

            Task<(int ExitCode, string StdOut, string StdErr)> call1 = RunCliProcessAsync(
                workingDirectory: repoDir,
                arguments: new[] { "workspace.use", "ConcurrentFixture.slnx", "--require-solution", "true", "--idle-timeout-seconds", "60" },
                requireDaemon: false,
                extraEnv: env);
            Task<(int ExitCode, string StdOut, string StdErr)> call2 = RunCliProcessAsync(
                workingDirectory: repoDir,
                arguments: new[] { "workspace.use", "ConcurrentFixture.slnx", "--require-solution", "true", "--idle-timeout-seconds", "60" },
                requireDaemon: false,
                extraEnv: env);

            await Task.WhenAll(call1, call2);
            (int exit1, string out1, string err1) = await call1;
            (int exit2, string out2, string err2) = await call2;
            Assert.True(exit1 == 0, $"First concurrent workspace.use failed.\nSTDOUT:\n{out1}\nSTDERR:\n{err1}");
            Assert.True(exit2 == 0, $"Second concurrent workspace.use failed.\nSTDOUT:\n{out2}\nSTDERR:\n{err2}");

            using JsonDocument doc1 = JsonDocument.Parse(out1);
            using JsonDocument doc2 = JsonDocument.Parse(out2);
            int pid1 = doc1.RootElement.GetProperty("Data").GetProperty("daemon_pid").GetInt32();
            int pid2 = doc2.RootElement.GetProperty("Data").GetProperty("daemon_pid").GetInt32();
            daemonPid = pid1;
            Assert.True(pid1 > 0);
            Assert.Equal(pid1, pid2);
        }
        finally
        {
            await CleanupIsolatedStateDirAsync(stateDir, tempRoot, daemonPid);
        }
    }

    [Fact]
    public async Task DaemonIdleTimeout_ExitsCleanlyLogsReasonAndRemovesManifest()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-idle-timeout-{Guid.NewGuid():N}");
        string stateDir = Path.Combine(tempRoot, ".state");
        string repoDir = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            ["ROSLYNSKILLS_WORKSPACE_HOST_DIR"] = stateDir,
        };
        int? daemonPid = null;

        try
        {
            _ = await CreateSingleProjectSlnxFixtureAsync(repoDir, "IdleFixture", "Sleepy");

            (int startExit, string startOut, string startErr) = await RunCliProcessAsync(
                workingDirectory: repoDir,
                arguments: new[] { "daemon.start", "--repo-root", repoDir, "--idle-timeout-seconds", "2" },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(startExit == 0, $"daemon.start failed.\nSTDOUT:\n{startOut}\nSTDERR:\n{startErr}");

            string manifestPath;
            string logPath;
            using (JsonDocument startDoc = JsonDocument.Parse(startOut))
            {
                JsonElement data = startDoc.RootElement.GetProperty("Data");
                daemonPid = data.GetProperty("daemon_pid").GetInt32();
                manifestPath = data.GetProperty("manifest_path").GetString()!;
                logPath = data.GetProperty("log_path").GetString()!;
                Assert.True(File.Exists(manifestPath));
            }

            // Probe daemon.status once — read-only status pings must NOT reset the idle timer.
            (int statusExit, string statusOut, string statusErr) = await RunCliProcessAsync(
                workingDirectory: repoDir,
                arguments: new[] { "daemon.status", "--repo-root", repoDir },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(statusExit == 0, $"daemon.status failed.\nSTDOUT:\n{statusOut}\nSTDERR:\n{statusErr}");

            await WaitForProcessIdGoneAsync(daemonPid.Value, TimeSpan.FromSeconds(12));
            bool exitedOnOwn = !IsProcessAlive(daemonPid.Value);
            Assert.True(exitedOnOwn, $"Expected WorkspaceHost PID {daemonPid.Value} to exit on 2 s idle timeout.");

            // Wait briefly for finally manifest cleanup to settle on disk.
            Stopwatch manifestSw = Stopwatch.StartNew();
            while (File.Exists(manifestPath) && manifestSw.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(50);
            }

            Assert.False(File.Exists(manifestPath), $"Expected manifest '{manifestPath}' to be removed on clean idle exit.");
            Assert.True(File.Exists(logPath), $"Expected log file '{logPath}' to exist.");
            string logText = await File.ReadAllTextAsync(logPath);
            Assert.Contains("host_idle_timeout", logText, StringComparison.Ordinal);
            daemonPid = null;
        }
        finally
        {
            await CleanupIsolatedStateDirAsync(stateDir, tempRoot, daemonPid);
        }
    }

    [Fact]
    public async Task DaemonHostCap_ListAndPrune_EnforcesCapListsHostsAndPrunesMissingFolders()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-cap-prune-{Guid.NewGuid():N}");
        string stateDir = Path.Combine(tempRoot, ".state");
        string repoA = Path.Combine(tempRoot, "repo-a");
        string repoB = Path.Combine(tempRoot, "repo-b");
        string repoC = Path.Combine(tempRoot, "repo-c");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(repoB);
        Directory.CreateDirectory(repoC);
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            ["ROSLYNSKILLS_WORKSPACE_HOST_DIR"] = stateDir,
            ["ROSLYNSKILLS_MAX_DAEMON_HOSTS"] = "2",
        };
        int? pidA = null;
        int? pidB = null;

        try
        {
            _ = await CreateSingleProjectSlnxFixtureAsync(repoA, "CapA", "CallA");
            _ = await CreateSingleProjectSlnxFixtureAsync(repoB, "CapB", "CallB");
            _ = await CreateSingleProjectSlnxFixtureAsync(repoC, "CapC", "CallC");

            // Drop a dead-PID stale manifest into the isolated state dir to verify cleanup.
            string staleManifestPath = Path.Combine(stateDir, "deadbeef00000000.json");
            await File.WriteAllTextAsync(staleManifestPath, """
                {
                  "ManifestVersion": "1.0",
                  "Transport": "named-pipe",
                  "PipeName": "roslynskills-deadbeef00000000",
                  "SocketPath": null,
                  "RepoRoot": "C:\\nonexistent\\stale",
                  "HostPath": "RoslynSkills.WorkspaceHost.dll",
                  "AuthToken": "dead",
                  "ProcessId": 999999,
                  "StartedAtUtc": "2026-01-01T00:00:00Z"
                }
                """, Utf8NoBom);

            // Start 2 hosts (at cap = 2).
            (int useAExit, string useAOut, string useAErr) = await RunCliProcessAsync(
                repoA,
                new[] { "workspace.use", "CapA.slnx", "--require-solution", "true", "--idle-timeout-seconds", "120" },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(useAExit == 0, $"repo-a workspace.use failed.\nSTDOUT:\n{useAOut}\nSTDERR:\n{useAErr}");
            using (JsonDocument docA = JsonDocument.Parse(useAOut))
            {
                pidA = docA.RootElement.GetProperty("Data").GetProperty("daemon_pid").GetInt32();
            }

            // Starting repo-a should already have cleaned the dead-PID stale manifest during the cap scan.
            Assert.False(File.Exists(staleManifestPath), "Expected dead-PID stale manifest to be cleaned automatically.");

            (int useBExit, string useBOut, string useBErr) = await RunCliProcessAsync(
                repoB,
                new[] { "workspace.use", "CapB.slnx", "--require-solution", "true", "--idle-timeout-seconds", "120" },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(useBExit == 0, $"repo-b workspace.use failed.\nSTDOUT:\n{useBOut}\nSTDERR:\n{useBErr}");
            using (JsonDocument docB = JsonDocument.Parse(useBOut))
            {
                pidB = docB.RootElement.GetProperty("Data").GetProperty("daemon_pid").GetInt32();
            }

            // 3rd workspace.use must be refused with daemon_host_limit_reached and list the 2 active hosts.
            (int useCExit, string useCOut, string useCErr) = await RunCliProcessAsync(
                repoC,
                new[] { "workspace.use", "CapC.slnx", "--require-solution", "true", "--idle-timeout-seconds", "120" },
                requireDaemon: false,
                extraEnv: env);
            Assert.NotEqual(0, useCExit);
            using (JsonDocument docC = JsonDocument.Parse(useCOut))
            {
                Assert.False(docC.RootElement.GetProperty("Ok").GetBoolean());
                JsonElement err = docC.RootElement.GetProperty("Errors")[0];
                Assert.Equal("daemon_host_limit_reached", err.GetProperty("Code").GetString());
                JsonElement details = err.GetProperty("Details");
                Assert.Equal(2, details.GetProperty("max_hosts").GetInt32());
                Assert.Equal(2, details.GetProperty("active_count").GetInt32());
                Assert.Equal(2, details.GetProperty("hosts").GetArrayLength());
            }

            // daemon.list returns both hosts with workspace_key, daemon_pid, launch_mode, idle_seconds, memory_mb.
            (int listExit, string listOut, string listErr) = await RunCliProcessAsync(
                tempRoot,
                new[] { "daemon.list" },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(listExit == 0, $"daemon.list failed.\nSTDOUT:\n{listOut}\nSTDERR:\n{listErr}");
            using (JsonDocument listDoc = JsonDocument.Parse(listOut))
            {
                JsonElement data = listDoc.RootElement.GetProperty("Data");
                Assert.Equal(2, data.GetProperty("active_count").GetInt32());
                Assert.Equal(2, data.GetProperty("max_hosts").GetInt32());
                JsonElement hosts = data.GetProperty("hosts");
                Assert.Equal(2, hosts.GetArrayLength());
                foreach (JsonElement host in hosts.EnumerateArray())
                {
                    Assert.False(string.IsNullOrWhiteSpace(host.GetProperty("workspace_key").GetString()));
                    Assert.True(host.GetProperty("daemon_pid").GetInt32() > 0);
                    Assert.False(string.IsNullOrWhiteSpace(host.GetProperty("launch_mode").GetString()));
                    Assert.Equal(JsonValueKind.Number, host.GetProperty("idle_seconds").ValueKind);
                    Assert.True(host.GetProperty("memory_mb").GetDouble() > 0);
                }
            }

            // Close repo-b's workspace (releasing Windows FileSystemWatcher handle while leaving repo-b's host alive),
            // delete repo-b folder, and run daemon.prune: repo-b host is stopped, repo-a survives!
            (int closeBExit, string closeBOut, string closeBErr) = await RunCliProcessAsync(
                repoB,
                new[] { "workspace.close", "--repo-root", repoB },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(closeBExit == 0, $"repo-b workspace.close failed.\nSTDOUT:\n{closeBOut}\nSTDERR:\n{closeBErr}");
            Assert.True(IsProcessAlive(pidB!.Value), "Expected repo-b host process to remain alive after workspace.close.");

            Directory.Delete(repoB, recursive: true);
            (int pruneExit, string pruneOut, string pruneErr) = await RunCliProcessAsync(
                tempRoot,
                new[] { "daemon.prune" },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(pruneExit == 0, $"daemon.prune failed.\nSTDOUT:\n{pruneOut}\nSTDERR:\n{pruneErr}");
            using (JsonDocument pruneDoc = JsonDocument.Parse(pruneOut))
            {
                JsonElement data = pruneDoc.RootElement.GetProperty("Data");
                Assert.Equal(1, data.GetProperty("stopped_count").GetInt32());
                Assert.Equal(1, data.GetProperty("remaining_live_hosts").GetInt32());
                JsonElement stopped = data.GetProperty("stopped_hosts")[0];
                Assert.Equal(pidB!.Value, stopped.GetProperty("daemon_pid").GetInt32());
                Assert.Equal("worktree_missing", stopped.GetProperty("reason").GetString());
            }

            await WaitForProcessIdGoneAsync(pidB!.Value, TimeSpan.FromSeconds(5));
            Assert.False(IsProcessAlive(pidB.Value));
            Assert.True(IsProcessAlive(pidA!.Value));
            pidB = null;

            // daemon.prune --all stops the remaining host (repo-a).
            (int pruneAllExit, string pruneAllOut, string pruneAllErr) = await RunCliProcessAsync(
                tempRoot,
                new[] { "daemon.prune", "--all" },
                requireDaemon: false,
                extraEnv: env);
            Assert.True(pruneAllExit == 0, $"daemon.prune --all failed.\nSTDOUT:\n{pruneAllOut}\nSTDERR:\n{pruneAllErr}");
            using (JsonDocument pruneAllDoc = JsonDocument.Parse(pruneAllOut))
            {
                JsonElement data = pruneAllDoc.RootElement.GetProperty("Data");
                Assert.Equal(1, data.GetProperty("stopped_count").GetInt32());
                Assert.Equal(0, data.GetProperty("remaining_live_hosts").GetInt32());
            }

            await WaitForProcessIdGoneAsync(pidA.Value, TimeSpan.FromSeconds(5));
            Assert.False(IsProcessAlive(pidA.Value));
            pidA = null;
        }
        finally
        {
            await CleanupIsolatedStateDirAsync(stateDir, tempRoot, pidA, pidB);
        }
    }

    [Fact]
    public async Task DaemonRun_ServesInForegroundUntilStoppedOrStdinCloses()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-daemon-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        int? daemonPid = null;

        try
        {
            _ = await CreateSingleProjectSlnxFixtureAsync(tempRoot, "ForegroundFixture", "RunForeground");

            // 1. Default piped stdin: serves workspace.use (reusing the exact daemon.run PID) + nav.find_references,
            //    then exits cleanly when stdin closes.
            using (Process runProc = StartCliDaemonRunProcess(tempRoot, exitOnStdinEof: null))
            {
                try
                {
                    string readyJson = await ReadJsonEnvelopeFromStreamAsync(runProc.StandardOutput, TimeSpan.FromSeconds(20));
                    using (JsonDocument readyDoc = JsonDocument.Parse(readyJson))
                    {
                        Assert.True(readyDoc.RootElement.GetProperty("Ok").GetBoolean(), $"daemon.run ready envelope failed:\n{readyJson}");
                        Assert.Equal("daemon.run", readyDoc.RootElement.GetProperty("CommandId").GetString());
                        JsonElement data = readyDoc.RootElement.GetProperty("Data");
                        Assert.True(data.GetProperty("foreground").GetBoolean());
                        Assert.Equal(WorkspaceHostDaemonManager.LifetimeForeground, data.GetProperty("daemon_lifetime").GetString());
                        Assert.True(data.GetProperty("exit_on_stdin_eof").GetBoolean());
                        daemonPid = data.GetProperty("daemon_pid").GetInt32();
                        Assert.True(daemonPid > 0);
                    }

                    (int useExit, string useOut, string useErr) = await RunCliProcessAsync(
                        workingDirectory: tempRoot,
                        arguments: new[] { "workspace.use", "ForegroundFixture.slnx", "--require-solution", "true" },
                        requireDaemon: false);
                    Assert.True(useExit == 0, $"workspace.use against daemon.run failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");
                    using (JsonDocument useDoc = JsonDocument.Parse(useOut))
                    {
                        JsonElement useData = useDoc.RootElement.GetProperty("Data");
                        Assert.Equal(WorkspaceHostDaemonManager.LifetimeForeground, useData.GetProperty("daemon_lifetime").GetString());
                        Assert.Equal(daemonPid.Value, useData.GetProperty("daemon_pid").GetInt32());
                        Assert.True(useData.GetProperty("daemon_reused").GetBoolean());
                    }

                    (int navExit, string navOut, string navErr) = await RunCliProcessAsync(
                        workingDirectory: tempRoot,
                        arguments: new[] { "nav.find_references", "Program.cs:5:24" },
                        requireDaemon: true);
                    Assert.True(navExit == 0, $"nav.find_references against daemon.run failed.\nSTDOUT:\n{navOut}\nSTDERR:\n{navErr}");
                    using (JsonDocument navDoc = JsonDocument.Parse(navOut))
                    {
                        Assert.Equal("daemon", navDoc.RootElement.GetProperty("Transport").GetProperty("mode").GetString());
                        Assert.True(navDoc.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32() >= 2);
                    }

                    // Closing stdin pipe must cleanly shut down daemon.run and its WorkspaceHost child.
                    runProc.StandardInput.Close();
                    using CancellationTokenSource exitCts = new(TimeSpan.FromSeconds(15));
                    await runProc.WaitForExitAsync(exitCts.Token);
                    Assert.Equal(0, runProc.ExitCode);
                    await WaitForProcessIdGoneAsync(daemonPid.Value, TimeSpan.FromSeconds(5));
                    Assert.False(File.Exists(RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(tempRoot)));
                    daemonPid = null;
                }
                finally
                {
                    if (!runProc.HasExited)
                    {
                        try { runProc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }

            // 2. With --exit-on-stdin-eof false: ignores stdin EOF and exits cleanly on daemon.stop.
            using (Process runProc = StartCliDaemonRunProcess(tempRoot, exitOnStdinEof: false))
            {
                try
                {
                    string readyJson = await ReadJsonEnvelopeFromStreamAsync(runProc.StandardOutput, TimeSpan.FromSeconds(20));
                    using (JsonDocument readyDoc = JsonDocument.Parse(readyJson))
                    {
                        Assert.True(readyDoc.RootElement.GetProperty("Ok").GetBoolean(), $"daemon.run ready envelope failed:\n{readyJson}");
                        JsonElement data = readyDoc.RootElement.GetProperty("Data");
                        Assert.Equal(WorkspaceHostDaemonManager.LifetimeForeground, data.GetProperty("daemon_lifetime").GetString());
                        Assert.False(data.GetProperty("exit_on_stdin_eof").GetBoolean());
                        daemonPid = data.GetProperty("daemon_pid").GetInt32();
                    }

                    runProc.StandardInput.Close();
                    await Task.Delay(300);
                    Assert.False(runProc.HasExited, "daemon.run exited on stdin EOF despite --exit-on-stdin-eof false.");

                    (int statusExit, string statusOut, string statusErr) = await RunCliProcessAsync(
                        workingDirectory: tempRoot,
                        arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                        requireDaemon: false);
                    Assert.True(statusExit == 0, $"daemon.status failed while daemon.run running.\nSTDOUT:\n{statusOut}\nSTDERR:\n{statusErr}");

                    (int stopExit, string stopOut, string stopErr) = await RunCliProcessAsync(
                        workingDirectory: tempRoot,
                        arguments: new[] { "daemon.stop", "--repo-root", tempRoot },
                        requireDaemon: false);
                    Assert.True(stopExit == 0, $"daemon.stop failed.\nSTDOUT:\n{stopOut}\nSTDERR:\n{stopErr}");

                    using CancellationTokenSource exitCts = new(TimeSpan.FromSeconds(15));
                    await runProc.WaitForExitAsync(exitCts.Token);
                    Assert.Equal(0, runProc.ExitCode);
                    await WaitForProcessIdGoneAsync(daemonPid.Value, TimeSpan.FromSeconds(5));
                    daemonPid = null;
                }
                finally
                {
                    if (!runProc.HasExited)
                    {
                        try { runProc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
        }
        finally
        {
            await CleanupDaemonAndTempRootAsync(tempRoot, daemonPid);
        }
    }

    [Fact]
    public async Task NavigationErrors_NameCandidateSymbolsOnSameLine()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roscli-53bg-hints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string filePath = Path.Combine(tempRoot, "Sample.cs");
            string source = """
                namespace Demo;

                public interface IRowSource { }

                public sealed class KeysetRowSource : IRowSource
                {
                    private static IRowSource? OpenSelect() => null;
                }
                """;
            await File.WriteAllTextAsync(filePath, source, Utf8NoBom);

            // 1. Anchoring nav.find_references on the `class` keyword (line 5, col 15) should fail with
            //    symbol_not_found and name `KeysetRowSource` with its exact line:col.
            (int refExit, string refOut, string refErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.find_references", $"{filePath}:5:15" },
                requireDaemon: false,
                timeoutMilliseconds: 30_000);
            Assert.True(refExit != 0, $"Expected symbol_not_found on 'class' keyword.\nSTDOUT:\n{refOut}\nSTDERR:\n{refErr}");
            using (JsonDocument refDoc = JsonDocument.Parse(refOut))
            {
                JsonElement err = refDoc.RootElement.GetProperty("Errors")[0];
                Assert.Equal("symbol_not_found", err.GetProperty("Code").GetString());
                string msg = err.GetProperty("Message").GetString() ?? string.Empty;
                Assert.Contains("KeysetRowSource", msg, StringComparison.Ordinal);
                Assert.Contains("5:21", msg, StringComparison.Ordinal);
            }

            // 2. Anchoring nav.call_hierarchy on the return type `IRowSource` (line 7, col 20) should fail with
            //    invalid_target and name `OpenSelect` with its exact line:col.
            (int chExit, string chOut, string chErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "nav.call_hierarchy", $"{filePath}:7:20" },
                requireDaemon: false,
                timeoutMilliseconds: 30_000);
            Assert.True(chExit != 0, $"Expected invalid_target on return type.\nSTDOUT:\n{chOut}\nSTDERR:\n{chErr}");
            using (JsonDocument chDoc = JsonDocument.Parse(chOut))
            {
                JsonElement err = chDoc.RootElement.GetProperty("Errors")[0];
                Assert.Equal("invalid_target", err.GetProperty("Code").GetString());
                string msg = err.GetProperty("Message").GetString() ?? string.Empty;
                Assert.Contains("OpenSelect", msg, StringComparison.Ordinal);
                Assert.Contains("7:32", msg, StringComparison.Ordinal);
            }
        }
        finally
        {
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

    private static async Task<string> CreateSingleProjectSlnxFixtureAsync(string rootDir, string projectName, string methodName)
    {
        string csprojPath = Path.Combine(rootDir, $"{projectName}.csproj");
        string slnxPath = Path.Combine(rootDir, $"{projectName}.slnx");
        string programPath = Path.Combine(rootDir, "Program.cs");

        await File.WriteAllTextAsync(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """, Utf8NoBom);

        await File.WriteAllTextAsync(slnxPath, $"""
            <Solution>
              <Project Path="{projectName}.csproj" />
            </Solution>
            """, Utf8NoBom);

        await File.WriteAllTextAsync(programPath, $$"""
            namespace {{projectName}};

            public static class Greeter
            {
                public static int {{methodName}}(int x) => x + 1;

                public static int Run() => {{methodName}}(41);

                private static int UnusedHelper() => 0;
            }
            """, Utf8NoBom);

        return programPath;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliProcessAsync(
        string workingDirectory,
        string[] arguments,
        bool requireDaemon,
        int timeoutMilliseconds = 60_000,
        IReadOnlyDictionary<string, string?>? extraEnv = null)
    {
        string cliDll = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
        string[] fullArgs = new[] { cliDll }.Concat(arguments).ToArray();
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            ["ROSCLI_DAEMON"] = requireDaemon ? "required" : null,
            ["ROSCLI_REQUIRE_HOT_WORKSPACE"] = null,
            ["ROSCLI_WORKSPACE_ALIAS"] = null,
        };
        if (extraEnv is not null)
        {
            foreach ((string key, string? value) in extraEnv)
            {
                env[key] = value;
            }
        }

        return RunProcessAsync(ResolveDotnetPath(), workingDirectory, fullArgs, env, timeoutMilliseconds);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        string workingDirectory,
        string[] arguments,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        int timeoutMilliseconds)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environmentOverrides is not null)
        {
            foreach ((string key, string? value) in environmentOverrides)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource cts = new(timeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Process '{fileName} {string.Join(' ', arguments)}' timed out after {timeoutMilliseconds} ms.");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        ProcessStartInfo psi = new("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"git {arguments} timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
        }
    }

    private static async Task CleanupIsolatedStateDirAsync(string stateDir, string tempRoot, params int?[] knownPids)
    {
        try
        {
            if (Directory.Exists(stateDir))
            {
                Dictionary<string, string?> env = new(StringComparer.Ordinal)
                {
                    ["ROSLYNSKILLS_WORKSPACE_HOST_DIR"] = stateDir,
                };
                string workDir = Directory.Exists(tempRoot) ? tempRoot : Path.GetTempPath();
                _ = await RunCliProcessAsync(workDir, new[] { "daemon.prune", "--all" }, requireDaemon: false, timeoutMilliseconds: 15_000, extraEnv: env);
            }
        }
        catch
        {
        }

        foreach (int? pid in knownPids)
        {
            if (pid.HasValue)
            {
                KillProcessTreeById(pid.Value);
                await WaitForProcessIdGoneAsync(pid.Value, TimeSpan.FromSeconds(3));
            }
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

    private static async Task CleanupDaemonAndTempRootAsync(string tempRoot, int? daemonPid)
    {
        try
        {
            WorkspaceHostDaemonManager manager = new();
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            _ = await manager.StopAsync(tempRoot, cts.Token);
        }
        catch
        {
        }

        if (daemonPid.HasValue)
        {
            KillProcessTreeById(daemonPid.Value);
        }

        try
        {
            string manifest = RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(tempRoot);
            string log = RepositoryRootDiscovery.GetWorkspaceDaemonLogPath(tempRoot);
            string aliases = new WorkspaceAliasStore(tempRoot).Path;
            foreach (string file in new[] { manifest, $"{manifest}.lock", log, aliases })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
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

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void KillProcessTreeById(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task WaitForProcessIdGoneAsync(int pid, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static string ResolveDotnetPath()
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

    private static Process StartCliDaemonRunProcess(string repoRoot, bool? exitOnStdinEof)
    {
        string cliDll = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
        ProcessStartInfo startInfo = new(ResolveDotnetPath())
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliDll);
        startInfo.ArgumentList.Add("daemon.run");
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(repoRoot);
        if (exitOnStdinEof.HasValue)
        {
            startInfo.ArgumentList.Add("--exit-on-stdin-eof");
            startInfo.ArgumentList.Add(exitOnStdinEof.Value ? "true" : "false");
        }

        startInfo.Environment.Remove("ROSCLI_DAEMON");
        startInfo.Environment.Remove("ROSCLI_REQUIRE_HOT_WORKSPACE");
        startInfo.Environment.Remove("ROSCLI_WORKSPACE_ALIAS");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roscli daemon.run.");
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(); } catch { }
        });
        return process;
    }

    private static async Task<string> ReadJsonEnvelopeFromStreamAsync(StreamReader reader, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        StringBuilder buffer = new();
        int braceDepth = 0;
        bool started = false;
        bool inString = false;
        bool escaped = false;

        while (true)
        {
            string? line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                throw new InvalidOperationException($"Stream ended before a complete JSON envelope was emitted. Partial output:\n{buffer}");
            }

            buffer.AppendLine(line);
            foreach (char ch in line)
            {
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    started = true;
                    braceDepth++;
                }
                else if (ch == '}')
                {
                    braceDepth--;
                }
            }

            if (started && braceDepth == 0)
            {
                return buffer.ToString();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static (int ExitCode, string StdOut, string StdErr) RunCommandInsideKillOnCloseJobWindows(
        string commandLine,
        string workingDirectory,
        bool allowBreakaway,
        TimeSpan timeout)
    {
        const uint CreateSuspended = 0x00000004;
        const uint CreateNoWindow = 0x08000000;
        const uint StartfUseStdHandles = 0x00000100;
        const int JobObjectExtendedLimitInformation = 9;
        const uint JobObjectLimitBreakawayOk = 0x00000800;
        const uint JobObjectLimitKillOnJobClose = 0x00002000;
        const uint GenericWrite = 0x40000000;
        const uint FileShareRead = 0x00000001;
        const uint FileShareWrite = 0x00000002;
        const uint CreateAlways = 2;
        const uint FileAttributeNormal = 0x00000080;
        IntPtr invalidHandle = new(-1);

        string stdoutPath = Path.Combine(workingDirectory, $".job-stdout-{Guid.NewGuid():N}.txt");
        string stderrPath = Path.Combine(workingDirectory, $".job-stderr-{Guid.NewGuid():N}.txt");

        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}");
        }

        IntPtr hProcess = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        IntPtr hOutFile = invalidHandle;
        IntPtr hErrFile = invalidHandle;
        try
        {
            JobObjectExtendedLimitInfo limitInfo = new();
            limitInfo.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose |
                (allowBreakaway ? JobObjectLimitBreakawayOk : 0u);
            int size = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limitInfo, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            SecurityAttributes sa = new()
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = 1,
            };
            hOutFile = CreateFileW(stdoutPath, GenericWrite, FileShareRead | FileShareWrite, ref sa, CreateAlways, FileAttributeNormal, IntPtr.Zero);
            hErrFile = CreateFileW(stderrPath, GenericWrite, FileShareRead | FileShareWrite, ref sa, CreateAlways, FileAttributeNormal, IntPtr.Zero);
            if (hOutFile == invalidHandle || hErrFile == invalidHandle)
            {
                throw new InvalidOperationException($"CreateFileW for job stdout/stderr failed: {Marshal.GetLastWin32Error()}");
            }

            StartupInfoW si = new()
            {
                cb = Marshal.SizeOf<StartupInfoW>(),
                dwFlags = StartfUseStdHandles,
                hStdInput = IntPtr.Zero,
                hStdOutput = hOutFile,
                hStdError = hErrFile,
            };
            StringBuilder cmd = new(commandLine);
            if (!CreateProcessW(
                null,
                cmd,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateSuspended | CreateNoWindow,
                IntPtr.Zero,
                workingDirectory,
                ref si,
                out ProcessInformation pi))
            {
                throw new InvalidOperationException($"CreateProcessW failed: {Marshal.GetLastWin32Error()}");
            }

            hProcess = pi.hProcess;
            hThread = pi.hThread;

            if (!AssignProcessToJobObject(job, hProcess))
            {
                TerminateProcess(hProcess, 1);
                throw new InvalidOperationException($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
            }

            if (ResumeThread(hThread) == uint.MaxValue)
            {
                TerminateProcess(hProcess, 1);
                throw new InvalidOperationException($"ResumeThread failed: {Marshal.GetLastWin32Error()}");
            }

            uint waitResult = WaitForSingleObject(hProcess, (uint)timeout.TotalMilliseconds);
            if (waitResult != 0)
            {
                TerminateProcess(hProcess, 1);
                throw new TimeoutException($"Job-wrapped command timed out after {timeout.TotalSeconds:F1}s.");
            }

            GetExitCodeProcess(hProcess, out uint exitCode);

            CloseHandle(hOutFile);
            hOutFile = invalidHandle;
            CloseHandle(hErrFile);
            hErrFile = invalidHandle;
            CloseHandle(hThread);
            hThread = IntPtr.Zero;
            CloseHandle(hProcess);
            hProcess = IntPtr.Zero;

            // Closing the job handle terminates EVERY process still inside the job object
            // before we read the redirected stdout/stderr files.
            CloseHandle(job);
            job = IntPtr.Zero;

            string stdoutText = ReadFileSharedText(stdoutPath);
            string stderrText = ReadFileSharedText(stderrPath);
            return ((int)exitCode, stdoutText, stderrText);
        }
        finally
        {
            if (hOutFile != invalidHandle && hOutFile != IntPtr.Zero) CloseHandle(hOutFile);
            if (hErrFile != invalidHandle && hErrFile != IntPtr.Zero) CloseHandle(hErrFile);
            if (hThread != IntPtr.Zero) CloseHandle(hThread);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            // Closing the job handle terminates EVERY process still inside the job object.
            if (job != IntPtr.Zero) CloseHandle(job);
            try { if (File.Exists(stdoutPath)) File.Delete(stdoutPath); } catch { }
            try { if (File.Exists(stderrPath)) File.Delete(stderrPath); } catch { }
        }
    }

    private static string ReadFileSharedText(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }

        return string.Empty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SecurityAttributes lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

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
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
