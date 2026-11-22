using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynSkills.Cli;
using RoslynSkills.Core;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.Cli.Tests;

/// <summary>
/// End-to-end verification in a real Git worktree (<c>git worktree add</c>, where <c>.git</c> is a
/// file containing <c>gitdir: ...</c>): shared repository-root discovery, edit claim awareness across
/// edit commands, and hot-workspace daemon routing under <c>ROSCLI_DAEMON=required</c> without
/// passing <c>--workspace-path</c> on every call.
/// </summary>
public sealed class GitWorktreeTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task RealGitWorktree_ResolvesRepoRoot_Claims_AndHotWorkspaceWithoutWorkspacePath()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-worktree-{Guid.NewGuid():N}");
        string mainRepo = Path.Combine(tempRoot, "main-repo");
        string worktreeRoot = Path.Combine(tempRoot, "wt-feature");
        Directory.CreateDirectory(mainRepo);

        try
        {
            await CreateAndCommitSampleRepoAsync(mainRepo);

            // Create a real Git worktree where .git is a file pointing to main-repo/.git/worktrees/wt-feature.
            RunGit(mainRepo, $"worktree add \"{worktreeRoot}\" -b feature/worktree-test");

            string dotGitPath = Path.Combine(worktreeRoot, ".git");
            Assert.True(File.Exists(dotGitPath), $"Expected .git to be a file in worktree '{worktreeRoot}'.");
            Assert.False(Directory.Exists(dotGitPath));
            string dotGitContent = await File.ReadAllTextAsync(dotGitPath);
            Assert.StartsWith("gitdir:", dotGitContent, StringComparison.OrdinalIgnoreCase);

            string appDir = Path.Combine(worktreeRoot, "src", "App");
            string demoPath = Path.Combine(appDir, "Demo.cs");
            string solutionPath = Path.Combine(worktreeRoot, "WorktreeDemo.slnx");
            Assert.True(File.Exists(demoPath));
            Assert.True(File.Exists(solutionPath));

            // 1. Shared RepositoryRootDiscovery finds the worktree root from root, subdir, and file paths.
            string expectedWorktreeRoot = Path.GetFullPath(worktreeRoot);
            Assert.Equal(expectedWorktreeRoot, RepositoryRootDiscovery.TryFindGitRepositoryRoot(worktreeRoot));
            Assert.Equal(expectedWorktreeRoot, RepositoryRootDiscovery.TryFindGitRepositoryRoot(appDir));
            Assert.Equal(expectedWorktreeRoot, RepositoryRootDiscovery.TryFindGitRepositoryRoot(demoPath));
            Assert.Equal(expectedWorktreeRoot, RepositoryRootDiscovery.ResolveWorkspaceDaemonRoot(null, demoPath));
            Assert.Equal(expectedWorktreeRoot, RepositoryRootDiscovery.ResolveEditClaimRepositoryRoot(null, demoPath));

            // 2. Before claiming: edit.replace_in_member must still detect the worktree repo_root (never null!).
            CliApplication app = new(DefaultRegistryFactory.Create());
            {
                StringWriter stdout = new();
                StringWriter stderr = new();
                int exitCode = await app.RunAsync(
                    new[]
                    {
                        "--no-daemon",
                        "edit.replace_in_member",
                        demoPath,
                        "--member-name", "Compute",
                        "--old-text", "return x + 1;",
                        "--new-text", "return x + 2;",
                        "--apply", "false",
                    },
                    stdout,
                    stderr,
                    CancellationToken.None);

                Assert.Equal(0, exitCode);
                using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
                JsonElement claimStatus = doc.RootElement.GetProperty("Data").GetProperty("claim_status");
                Assert.Equal(expectedWorktreeRoot, claimStatus.GetProperty("repo_root").GetString());
                Assert.False(claimStatus.GetProperty("claimed").GetBoolean());
                Assert.DoesNotContain("No repository root was found", stdout.ToString());
            }

            // 3. Claim the file using edit.claim from the worktree (no --repo-root passed, rooted path infers worktree root).
            string claimId;
            {
                StringWriter stdout = new();
                StringWriter stderr = new();
                int exitCode = await app.RunAsync(
                    new[]
                    {
                        "edit.claim",
                        "claim",
                        demoPath,
                        "--owner", "wt-agent",
                        "--reason", "worktree verification",
                    },
                    stdout,
                    stderr,
                    CancellationToken.None);

                Assert.Equal(0, exitCode);
                Assert.True(File.Exists(Path.Combine(worktreeRoot, ".roslynskills", "edit-claims.json")));
                using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
                claimId = doc.RootElement.GetProperty("Data").GetProperty("claimed").GetProperty("claim_id").GetString()!;
            }

            // 4. After claiming: edit.replace_in_member, edit.replace_text, and edit.insert_text all see repo_root and claimed=true.
            {
                StringWriter stdout = new();
                StringWriter stderr = new();
                int exitCode = await app.RunAsync(
                    new[]
                    {
                        "--no-daemon",
                        "edit.replace_in_member",
                        demoPath,
                        "--member-name", "Compute",
                        "--old-text", "return x + 1;",
                        "--new-text", "return x + 2;",
                        "--apply", "true",
                    },
                    stdout,
                    stderr,
                    CancellationToken.None);

                string output = stdout.ToString();
                Assert.Equal(0, exitCode);
                using JsonDocument doc = JsonDocument.Parse(output);
                JsonElement claimStatus = doc.RootElement.GetProperty("Data").GetProperty("claim_status");
                Assert.Equal(expectedWorktreeRoot, claimStatus.GetProperty("repo_root").GetString());
                Assert.True(claimStatus.GetProperty("claimed").GetBoolean());
                Assert.Contains($"claimed={claimId[..8]}", output);
                Assert.DoesNotContain("unclaimed", output);
            }

            {
                StringWriter stdout = new();
                StringWriter stderr = new();
                int exitCode = await app.RunAsync(
                    new[]
                    {
                        "--no-daemon",
                        "edit.replace_text",
                        demoPath,
                        "--old-text", "return x + 2;",
                        "--new-text", "return x + 3;",
                        "--apply", "true",
                    },
                    stdout,
                    stderr,
                    CancellationToken.None);

                string output = stdout.ToString();
                Assert.Equal(0, exitCode);
                using JsonDocument doc = JsonDocument.Parse(output);
                JsonElement claimStatus = doc.RootElement.GetProperty("Data").GetProperty("claim_status");
                Assert.Equal(expectedWorktreeRoot, claimStatus.GetProperty("repo_root").GetString());
                Assert.True(claimStatus.GetProperty("claimed").GetBoolean());
                Assert.Contains($"claimed={claimId[..8]}", output);
            }

            {
                StringWriter stdout = new();
                StringWriter stderr = new();
                int exitCode = await app.RunAsync(
                    new[]
                    {
                        "--no-daemon",
                        "edit.insert_text",
                        demoPath,
                        "--anchor-text", "        return x + 3;",
                        "--insert-text", "        // worktree insert\n",
                        "--position", "before",
                        "--apply", "true",
                    },
                    stdout,
                    stderr,
                    CancellationToken.None);

                Assert.Equal(0, exitCode);
                using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
                JsonElement claimStatus = doc.RootElement.GetProperty("Data").GetProperty("claim_status");
                Assert.Equal(expectedWorktreeRoot, claimStatus.GetProperty("repo_root").GetString());
                Assert.True(claimStatus.GetProperty("claimed").GetBoolean());
                Assert.Contains($"claimed={claimId[..8]}", stdout.ToString());
            }

            // 5. ctx.changed_files from a subdirectory inside the worktree resolves the worktree root.
            {
                StringWriter stdout = new();
                StringWriter stderr = new();
                int exitCode = await app.RunAsync(
                    new[] { "ctx.changed_files", appDir },
                    stdout,
                    stderr,
                    CancellationToken.None);

                Assert.Equal(0, exitCode);
                using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
                Assert.Equal(expectedWorktreeRoot, doc.RootElement.GetProperty("Data").GetProperty("repo_root").GetString());
                Assert.Equal(1, doc.RootElement.GetProperty("Data").GetProperty("csharp_changed").GetInt32());
            }

            // 6. Real CLI process with ROSCLI_DAEMON=required inside the git worktree:
            //    bind workspace.use once, then run commands from both the worktree root and a subdirectory
            //    WITHOUT passing --workspace-path or --repo-root.
            try
            {
                (int useExit, string useOut, string useErr) = RunCliProcess(
                    workingDirectory: worktreeRoot,
                    arguments: new[] { "workspace.use", "WorktreeDemo.slnx", "--require-solution", "true" },
                    requireDaemon: false);
                Assert.True(useExit == 0, $"workspace.use failed.\nSTDOUT:\n{useOut}\nSTDERR:\n{useErr}");
                Assert.True(File.Exists(Path.Combine(worktreeRoot, ".roslynskills", "workspaces.json")));

                // Run nav.find_symbol with ROSCLI_DAEMON=required and NO --workspace-path from worktree root.
                (int navExit, string navOut, string navErr) = RunCliProcess(
                    workingDirectory: worktreeRoot,
                    arguments: new[] { "nav.find_symbol", "src/App/Demo.cs", "Compute", "--brief", "true" },
                    requireDaemon: true);
                Assert.True(navExit == 0, $"nav.find_symbol failed under ROSCLI_DAEMON=required.\nSTDOUT:\n{navOut}\nSTDERR:\n{navErr}");
                Assert.Contains("process_hot", navOut);

                // Run edit.replace_in_member from a nested subdirectory with ROSCLI_DAEMON=required and NO --workspace-path.
                (int editExit, string editOut, string editErr) = RunCliProcess(
                    workingDirectory: appDir,
                    arguments: new[]
                    {
                        "edit.replace_in_member",
                        "Demo.cs",
                        "--member-name", "Compute",
                        "--old-text", "return x + 3;",
                        "--new-text", "return x + 4;",
                        "--apply", "true",
                    },
                    requireDaemon: true);
                Assert.True(editExit == 0, $"edit.replace_in_member failed from worktree subdir.\nSTDOUT:\n{editOut}\nSTDERR:\n{editErr}");
                using (JsonDocument editDoc = JsonDocument.Parse(editOut))
                {
                    JsonElement innerData = editDoc.RootElement
                        .GetProperty("Data")
                        .GetProperty("envelope")
                        .GetProperty("Data");
                    JsonElement claimStatus = innerData.GetProperty("claim_status");
                    Assert.Equal(expectedWorktreeRoot, claimStatus.GetProperty("repo_root").GetString());
                    Assert.True(claimStatus.GetProperty("claimed").GetBoolean());
                }

                // Run edit.transaction with replace_text from the nested subdirectory under ROSCLI_DAEMON=required and NO --workspace-path.
                string txInput = JsonSerializer.Serialize(new
                {
                    apply = true,
                    require_workspace = true,
                    operations = new object[]
                    {
                        new
                        {
                            file_path = demoPath,
                            operation = "replace_text",
                            old_text = "return x + 4;",
                            new_text = "return x + 5;",
                        },
                    },
                });
                (int txExit, string txOut, string txErr) = RunCliProcess(
                    workingDirectory: appDir,
                    arguments: new[] { "run", "edit.transaction", "--input", txInput },
                    requireDaemon: true);
                Assert.True(txExit == 0, $"edit.transaction failed from worktree subdir.\nSTDOUT:\n{txOut}\nSTDERR:\n{txErr}");
                Assert.Contains("process_hot", txOut);
                Assert.Contains("return x + 5;", await File.ReadAllTextAsync(demoPath));

                // Run workspace.status from the nested subdirectory WITHOUT --repo-root.
                (int statusExit, string statusOut, string statusErr) = RunCliProcess(
                    workingDirectory: appDir,
                    arguments: new[] { "workspace.status" },
                    requireDaemon: true);
                Assert.True(statusExit == 0, $"workspace.status failed from worktree subdir.\nSTDOUT:\n{statusOut}\nSTDERR:\n{statusErr}");
            }
            finally
            {
                // Always stop the daemon started for the temp worktree.
                RunCliProcess(
                    workingDirectory: worktreeRoot,
                    arguments: new[] { "daemon.stop", "--repo-root", worktreeRoot },
                    requireDaemon: false);
            }
        }
        finally
        {
            TryCleanupTempGitRoot(mainRepo, worktreeRoot, tempRoot);
        }
    }

    private static async Task CreateAndCommitSampleRepoAsync(string repoRoot)
    {
        RunGit(repoRoot, "init -b main");
        RunGit(repoRoot, "config user.name \"RoslynSkills Test\"");
        RunGit(repoRoot, "config user.email \"test@example.com\"");

        string appDir = Path.Combine(repoRoot, "src", "App");
        Directory.CreateDirectory(appDir);

        await File.WriteAllTextAsync(
            Path.Combine(appDir, "App.csproj"),
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
            Path.Combine(appDir, "Demo.cs"),
            """
            namespace App;

            public sealed class Demo
            {
                public int Compute(int x)
                {
                    return x + 1;
                }
            }
            """,
            Utf8NoBom);

        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "WorktreeDemo.slnx"),
            """
            <Solution>
              <Project Path="src/App/App.csproj" />
            </Solution>
            """,
            Utf8NoBom);

        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -m \"Initial sample repo\"");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCliProcess(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool requireDaemon)
    {
        string cliDllPath = typeof(CliApplication).Assembly.Location;
        ProcessStartInfo startInfo = new("dotnet")
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
        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"roscli process timed out for args: {string.Join(' ', arguments)}");
        }

        return (process.ExitCode, stdOutTask.GetAwaiter().GetResult(), stdErrTask.GetAwaiter().GetResult());
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        ProcessStartInfo startInfo = new("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed (exit={process.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
    }

    private static void TryCleanupTempGitRoot(string mainRepo, string worktreeRoot, string tempRoot)
    {
        try
        {
            if (Directory.Exists(mainRepo) && Directory.Exists(worktreeRoot))
            {
                RunGit(mainRepo, $"worktree remove --force \"{worktreeRoot}\"");
            }
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(tempRoot))
            {
                foreach (string file in Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
