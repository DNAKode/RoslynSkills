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
    public async Task WindowsKillOnJobClose_DaemonSurvivesParentShellJobTermination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-jobobj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        int? daemonPid = null;

        try
        {
            string cliDll = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
            string dotnetExe = ResolveDotnetPath();
            string cmdLine = WorkspaceHostDaemonManager.BuildWindowsCommandLine(
                dotnetExe,
                new[] { cliDll, "daemon.start", "--repo-root", tempRoot });

            int childExitCode = RunCommandInsideKillOnCloseJobWindows(cmdLine, tempRoot, TimeSpan.FromSeconds(30));
            Assert.Equal(0, childExitCode);

            // The parent job object is now closed and any process still inside it was terminated by Windows.
            WorkspaceHostDaemonManager manager = new();
            daemonPid = manager.TryGetDaemonProcessId(tempRoot);
            Assert.NotNull(daemonPid);

            // Verify the daemon survived job termination and still answers daemon.status.
            (int statusExit, string statusOut, string statusErr) = await RunCliProcessAsync(
                workingDirectory: tempRoot,
                arguments: new[] { "daemon.status", "--repo-root", tempRoot },
                requireDaemon: false,
                timeoutMilliseconds: 15_000);
            Assert.True(statusExit == 0, $"Daemon did not survive KillOnJobClose termination.\nSTDOUT:\n{statusOut}\nSTDERR:\n{statusErr}");
            using JsonDocument doc = JsonDocument.Parse(statusOut);
            Assert.Equal(daemonPid.Value, doc.RootElement.GetProperty("Data").GetProperty("daemon_pid").GetInt32());
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
        int timeoutMilliseconds = 60_000)
    {
        string cliDll = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
        string[] fullArgs = new[] { cliDll }.Concat(arguments).ToArray();
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            ["ROSCLI_DAEMON"] = requireDaemon ? "required" : null,
            ["ROSCLI_REQUIRE_HOT_WORKSPACE"] = null,
            ["ROSCLI_WORKSPACE_ALIAS"] = null,
        };
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
            foreach (string file in new[] { manifest, log, aliases })
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

    [SupportedOSPlatform("windows")]
    private static int RunCommandInsideKillOnCloseJobWindows(string commandLine, string workingDirectory, TimeSpan timeout)
    {
        const uint CreateSuspended = 0x00000004;
        const uint CreateNoWindow = 0x08000000;
        const int JobObjectExtendedLimitInformation = 9;
        const uint JobObjectLimitKillOnJobClose = 0x00002000;

        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}");
        }

        IntPtr hProcess = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        try
        {
            JobObjectExtendedLimitInfo limitInfo = new();
            limitInfo.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
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

            StartupInfoW si = new() { cb = Marshal.SizeOf<StartupInfoW>() };
            StringBuilder cmd = new(commandLine);
            if (!CreateProcessW(
                null,
                cmd,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
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
            return (int)exitCode;
        }
        finally
        {
            if (hThread != IntPtr.Zero) CloseHandle(hThread);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            // Closing the job handle terminates EVERY process still inside the job object.
            CloseHandle(job);
        }
    }

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
