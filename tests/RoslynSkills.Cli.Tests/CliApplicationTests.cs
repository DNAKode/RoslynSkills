using RoslynSkills.Cli;
using RoslynSkills.Core;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task ListCommands_ContainsExpectedCommands()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "list-commands" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("nav.find_symbol", output);
        Assert.Contains("nav.find_symbol_batch", output);
        Assert.Contains("nav.find_references", output);
        Assert.Contains("nav.find_invocations", output);
        Assert.Contains("nav.call_hierarchy", output);
        Assert.Contains("nav.find_implementations", output);
        Assert.Contains("nav.find_overrides", output);
        Assert.Contains("ctx.symbol_envelope", output);
        Assert.Contains("ctx.file_outline", output);
        Assert.Contains("ctx.member_source", output);
        Assert.Contains("ctx.search_text", output);
        Assert.Contains("ctx.changed_files", output);
        Assert.Contains("ctx.call_chain_slice", output);
        Assert.Contains("ctx.dependency_slice", output);
        Assert.Contains("analyze.unused_private_symbols", output);
        Assert.Contains("analyze.dependency_violations", output);
        Assert.Contains("analyze.impact_slice", output);
        Assert.Contains("analyze.override_coverage", output);
        Assert.Contains("analyze.async_risk_scan", output);
        Assert.Contains("query.batch", output);
        Assert.Contains("diag.get_file_diagnostics", output);
        Assert.Contains("diag.get_after_edit", output);
        Assert.Contains("diag.get_solution_snapshot", output);
        Assert.Contains("diag.diff", output);
        Assert.Contains("edit.rename_symbol", output);
        Assert.Contains("edit.change_signature", output);
        Assert.Contains("edit.add_member", output);
        Assert.Contains("edit.replace_member_body", output);
        Assert.Contains("edit.update_usings", output);
        Assert.Contains("edit.apply_code_fix", output);
        Assert.Contains("edit.create_file", output);
        Assert.Contains("edit.replace_text", output);
        Assert.Contains("edit.insert_text", output);
        Assert.Contains("edit.batch_exact", output);
        Assert.Contains("edit.transaction", output);
        Assert.Contains("edit.claim", output);
        Assert.Contains("repair.propose_from_diagnostics", output);
        Assert.Contains("repair.apply_plan", output);
        Assert.Contains("session.open", output);
        Assert.Contains("session.set_content", output);
        Assert.Contains("session.apply_text_edits", output);
        Assert.Contains("session.apply_and_commit", output);
        Assert.Contains("session.get_diagnostics", output);
        Assert.Contains("session.status", output);
        Assert.Contains("session.diff", output);
        Assert.Contains("session.commit", output);
        Assert.Contains("session.close", output);
        Assert.Contains("daemon.start", output);
        Assert.Contains("daemon.stop", output);
        Assert.Contains("daemon.restart", output);
        Assert.Contains("pit_of_success", output);
        Assert.Contains("quickstart", output);
        Assert.Contains("csharp-start", output);
    }

    [Fact]
    public async Task DescribeCommand_MemberSource_ReturnsLineColumnUsage()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "ctx.member_source" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("ctx.member_source <file-path> <line> <column>", output);
        Assert.Contains("--member-name", output);
        Assert.Contains("member_name", output);
        Assert.Contains("focus_text", output);
        Assert.Contains("workspace_cache_mode", output);
    }

    [Fact]
    public async Task EditClaim_ClaimAndStatus_UsesRepoLocalStore()
    {
        string repoRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-edit-claim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter claimOut = new();
            StringWriter claimErr = new();

            int claimExit = await app.RunAsync(
                new[]
                {
                    "edit.claim",
                    "claim",
                    "src/Demo.cs",
                    "--repo-root",
                    repoRoot,
                    "--owner",
                    "agent-a",
                    "--reason",
                    "test claim",
                    "--ttl-minutes",
                    "30",
                },
                claimOut,
                claimErr,
                CancellationToken.None);

            string claimOutput = claimOut.ToString();
            Assert.Equal(0, claimExit);
            Assert.Contains("\"ok\": true", claimOutput);
            Assert.Contains("src/Demo.cs", claimOutput);
            Assert.True(File.Exists(Path.Combine(repoRoot, ".roslynskills", "edit-claims.json")));

            StringWriter statusOut = new();
            StringWriter statusErr = new();
            int statusExit = await app.RunAsync(
                new[] { "edit.claim", "status", "--repo-root", repoRoot },
                statusOut,
                statusErr,
                CancellationToken.None);

            string statusOutput = statusOut.ToString();
            Assert.Equal(0, statusExit);
            Assert.Contains("\"active_count\": 1", statusOutput);
            Assert.Contains("agent-a", statusOutput);

            StringWriter listOut = new();
            StringWriter listErr = new();
            int listExit = await app.RunAsync(
                new[] { "edit.claim", "list", "--repo-root", repoRoot },
                listOut,
                listErr,
                CancellationToken.None);

            string listOutput = listOut.ToString();
            Assert.Equal(0, listExit);
            Assert.Contains("\"active_count\": 1", listOutput);
            Assert.Contains("agent-a", listOutput);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EditClaim_ReleaseAcceptsClaimIdWithoutOwner()
    {
        string repoRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-edit-claim-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter claimOut = new();
            StringWriter claimErr = new();

            int claimExit = await app.RunAsync(
                new[]
                {
                    "edit.claim",
                    "claim",
                    "src/Demo.cs",
                    "--repo-root",
                    repoRoot,
                    "--owner",
                    "agent-a",
                    "--reason",
                    "test claim",
                },
                claimOut,
                claimErr,
                CancellationToken.None);

            Assert.Equal(0, claimExit);
            using JsonDocument claimJson = JsonDocument.Parse(claimOut.ToString());
            string claimId = claimJson.RootElement.GetProperty("Data").GetProperty("claimed").GetProperty("claim_id").GetString()!;

            StringWriter releaseOut = new();
            StringWriter releaseErr = new();
            int releaseExit = await app.RunAsync(
                new[] { "edit.claim", "release", claimId, "--repo-root", repoRoot },
                releaseOut,
                releaseErr,
                CancellationToken.None);

            string releaseOutput = releaseOut.ToString();
            Assert.Equal(0, releaseExit);
            Assert.Contains("\"released_count\": 1", releaseOutput);

            StringWriter statusOut = new();
            StringWriter statusErr = new();
            int statusExit = await app.RunAsync(
                new[] { "edit.claim", "status", "--repo-root", repoRoot },
                statusOut,
                statusErr,
                CancellationToken.None);

            Assert.Equal(0, statusExit);
            Assert.Contains("\"active_count\": 0", statusOut.ToString());
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DescribeCommand_DaemonStart_ReturnsLifecycleUsage()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "daemon.start" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Id\": \"daemon.start\"", output);
        Assert.Contains("--host-path", output);
        Assert.Contains("Normally no --host-path is needed", output);
    }

    [Fact]
    public async Task ListCommands_IdsOnlyMode_ReturnsCompactIds()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "list-commands", "--ids-only" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"command_ids\": [", output);
        Assert.Contains("\"session.apply_and_commit\"", output);
        Assert.Contains("\"pit_of_success\": {", output);
        Assert.DoesNotContain("\"InputSchemaVersion\"", output);
    }

    [Fact]
    public async Task ListCommands_CompactMode_IncludesMaturityMetadata()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "list-commands", "--compact" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Maturity\":", output);
        Assert.Contains("\"metadata\": {", output);
        Assert.Contains("\"maturity_counts\": {", output);
    }

    [Fact]
    public async Task ListCommands_StableOnly_FiltersAdvancedCommands()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "list-commands", "--stable-only", "--ids-only" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"filter\": \"stable_only\"", output);
        Assert.Contains("\"nav.find_symbol\"", output);
        Assert.DoesNotContain("\"ctx.call_chain_slice\"", output);
        Assert.DoesNotContain("\"diag.get_workspace_snapshot\"", output);
    }

    [Fact]
    public async Task DaemonStartHelp_ReturnsLifecycleUsage()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "daemon.start", "--help" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("daemon.start", output);
        Assert.Contains("--repo-root", output);
        Assert.Contains("--host-path", output);
    }

    [Fact]
    public async Task DaemonStatus_RejectsUnknownOption()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "daemon.status", "--bogus" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("\"CommandId\": \"daemon\"", output);
        Assert.Contains("\"Code\": \"invalid_args\"", output);
    }

    [Fact]
    public async Task WorkspaceUseHelp_ReturnsDaemonWorkspaceUsage()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "workspace.use", "--help" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("workspace.use", output);
        Assert.Contains("--alias default", output);
        Assert.Contains("--repo-root", output);
        Assert.Contains("Start the daemon if needed", output);
    }

    [Fact]
    public async Task WorkspaceStatus_RejectsUnknownOption()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "workspace.status", "--bogus", "true" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("\"CommandId\": \"workspace.status\"", output);
        Assert.Contains("\"Code\": \"invalid_args\"", output);
        Assert.Contains("Unknown workspace option '--bogus'", output);
    }

    [Fact]
    public async Task WorkspacePreload_RejectsInvalidBoolOption()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "workspace.preload", "Demo.slnx", "--require-solution", "maybe" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("\"CommandId\": \"workspace.preload\"", output);
        Assert.Contains("\"Code\": \"invalid_args\"", output);
        Assert.Contains("must be true or false", output);
    }

    [Fact]
    public void WorkspacePreload_InferredDaemonRootUsesWorkspaceGitRoot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-daemon-root-{Guid.NewGuid():N}");
        string repoRoot = Path.Combine(tempRoot, "TargetRepo");
        string nestedDir = Path.Combine(repoRoot, "src", "App");
        string solutionPath = Path.Combine(repoRoot, "Target.slnx");

        try
        {
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(solutionPath, string.Empty);

            System.Reflection.MethodInfo method = typeof(CliApplication).GetMethod(
                "InferWorkspaceDaemonRepoRoot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

            string inferred = (string)method.Invoke(null, new object?[] { null, solutionPath })!;
            string explicitRoot = (string)method.Invoke(null, new object?[] { nestedDir, solutionPath })!;

            Assert.Equal(Path.GetFullPath(repoRoot), inferred);
            Assert.Equal(nestedDir, explicitRoot);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DaemonCapableToolCall_InferredDaemonRootUsesFilePathGitRoot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-tool-root-{Guid.NewGuid():N}");
        string repoRoot = Path.Combine(tempRoot, "TargetRepo");
        string filePath = Path.Combine(repoRoot, "src", "App", "Demo.cs");

        try
        {
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "public class Demo { }\n");

            System.Reflection.MethodInfo method = typeof(CliApplication).GetMethod(
                "InferToolCallDaemonRepoRoot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            JsonElement input = JsonSerializer.SerializeToElement(new
            {
                file_path = filePath,
                line = 1,
                column = 14,
            });

            string inferred = (string)method.Invoke(null, new object?[] { input })!;

            Assert.Equal(Path.GetFullPath(repoRoot), inferred);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DaemonCapableToolCall_InferredDaemonRootUsesNestedBatchOperationPath()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-tool-batch-root-{Guid.NewGuid():N}");
        string repoRoot = Path.Combine(tempRoot, "TargetRepo");
        string filePath = Path.Combine(repoRoot, "src", "App", "Demo.cs");

        try
        {
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "public class Demo { }\n");

            System.Reflection.MethodInfo method = typeof(CliApplication).GetMethod(
                "InferToolCallDaemonRepoRoot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            JsonElement input = JsonSerializer.SerializeToElement(new
            {
                operations = new object[]
                {
                    new
                    {
                        kind = "replace_span",
                        file_path = filePath,
                        span_start = 0,
                        span_length = 6,
                        new_text = "public",
                    },
                },
            });

            string inferred = (string)method.Invoke(null, new object?[] { input })!;

            Assert.Equal(Path.GetFullPath(repoRoot), inferred);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WorkspaceStatus_WhenDaemonUnavailable_ReturnsDaemonUnavailable()
    {
        string repoRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-daemon-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "workspace.status", "--repo-root", repoRoot },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("\"CommandId\": \"workspace.status\"", output);
            Assert.Contains("\"Code\": \"daemon_unavailable\"", output);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceAliasStore_PersistsAndRemovesAliases()
    {
        string repoRoot = Path.Combine(Path.GetTempPath(), $"roslynskills-alias-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            WorkspaceAliasStore store = new(repoRoot);
            WorkspaceAliasRecord record = new(
                WorkspacePath: Path.Combine(repoRoot, "Demo.slnx"),
                WorkspaceHandle: "ws_123",
                DaemonEndpoint: "pipe:roslynskills-test",
                DaemonPid: 1234,
                WorkspaceFingerprint: "abc",
                LastSeenUtc: DateTimeOffset.Parse("2026-05-15T00:00:00Z"));

            store.Upsert("default", record);

            Assert.True(File.Exists(Path.Combine(repoRoot, ".roslynskills", "workspaces.json")));
            Assert.True(store.TryGet("DEFAULT", out WorkspaceAliasRecord? loaded));
            Assert.NotNull(loaded);
            Assert.Equal("ws_123", loaded.WorkspaceHandle);
            Assert.Equal("pipe:roslynskills-test", loaded.DaemonEndpoint);

            store.Remove("default");

            Assert.False(store.TryGet("default", out _));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DaemonCapableCommand_RequiredModeWithoutAlias_FailsClosed()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-daemon-route-{Guid.NewGuid():N}.cs");
        string? previousMode = Environment.GetEnvironmentVariable("ROSCLI_DAEMON");
        string? previousAlias = Environment.GetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS");

        try
        {
            await File.WriteAllTextAsync(filePath, "public class Demo { }");
            Environment.SetEnvironmentVariable("ROSCLI_DAEMON", "required");
            Environment.SetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS", $"missing-{Guid.NewGuid():N}");
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "nav.find_symbol", filePath, "Demo" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("\"CommandId\": \"cli.daemon_route\"", output);
            Assert.Contains("\"Code\": \"hot_workspace_alias_not_found\"", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSCLI_DAEMON", previousMode);
            Environment.SetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS", previousAlias);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DaemonCapableCommand_NoDaemonFlag_UsesDirectPath()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-no-daemon-{Guid.NewGuid():N}.cs");
        string? previousMode = Environment.GetEnvironmentVariable("ROSCLI_DAEMON");
        string? previousAlias = Environment.GetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS");

        try
        {
            await File.WriteAllTextAsync(filePath, "namespace DemoNs { public class Demo { } }");
            Environment.SetEnvironmentVariable("ROSCLI_DAEMON", "required");
            Environment.SetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS", $"missing-{Guid.NewGuid():N}");
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "nav.find_symbol", filePath, "Demo", "--no-daemon" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"nav.find_symbol\"", output);
            Assert.DoesNotContain("hot_workspace_alias_not_found", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSCLI_DAEMON", previousMode);
            Environment.SetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS", previousAlias);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DaemonCapableCommand_DaemonOff_UsesDirectPath()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-daemon-off-{Guid.NewGuid():N}.cs");
        string? previousMode = Environment.GetEnvironmentVariable("ROSCLI_DAEMON");
        string? previousAlias = Environment.GetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS");

        try
        {
            await File.WriteAllTextAsync(filePath, "namespace DemoNs { public class Demo { } }");
            Environment.SetEnvironmentVariable("ROSCLI_DAEMON", "off");
            Environment.SetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS", $"missing-{Guid.NewGuid():N}");
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "nav.find_symbol", filePath, "Demo" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"nav.find_symbol\"", output);
            Assert.DoesNotContain("hot_workspace_alias_not_found", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSCLI_DAEMON", previousMode);
            Environment.SetEnvironmentVariable("ROSCLI_WORKSPACE_ALIAS", previousAlias);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RunPing_ReturnsSuccessEnvelope()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "run", "system.ping" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Ok\": true", output);
        Assert.Contains("\"CommandId\": \"system.ping\"", output);
        Assert.Contains("\"Telemetry\": {", output);
        Assert.Contains("\"validate_ms\":", output);
        Assert.Contains("\"execute_ms\":", output);
        Assert.Contains("\"total_ms\":", output);
        Assert.Contains("\"binary_launch_mode\":", output);
        Assert.Contains("\"Preview\": \"system.ping ok\"", output);
        Assert.Contains("\"Summary\": \"system.ping ok\"", output);
    }

    [Fact]
    public async Task RunPing_AcceptsInputFromStdinWithoutTempFile()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();
        StringReader stdin = new("{}");

        int exitCode = await app.RunAsync(
            new[] { "run", "system.ping", "--input-stdin" },
            stdout,
            stderr,
            CancellationToken.None,
            stdin);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Ok\": true", output);
        Assert.Contains("\"CommandId\": \"system.ping\"", output);
    }

    [Fact]
    public async Task DirectCommand_Ping_ReturnsSuccessEnvelope()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "system.ping" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Ok\": true", output);
        Assert.Contains("\"CommandId\": \"system.ping\"", output);
    }

    [Fact]
    public async Task DirectCommand_FileOutline_AcceptsFilePathShorthand()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public void Run() { }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.file_outline", filePath },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"ctx.file_outline\"", output);
            Assert.Contains("\"type_name\": \"Demo\"", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_FileOutline_AcceptsOptionalFlags()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                using System;

                public class Demo
                {
                    public void Run() { }
                    public void Run2() { }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "ctx.file_outline",
                    filePath,
                    "--include-members", "false",
                    "--include-usings", "true",
                    "--max-types", "1",
                    "--max-members", "1",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"ctx.file_outline\"", output);
            Assert.Contains("\"include_members\": false", output);
            Assert.Contains("\"include_usings\": true", output);
            Assert.Contains("\"max_types\": 1", output);
            Assert.Contains("\"max_members\": 1", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_SolutionSnapshot_AcceptsRepeatedOptionalFlags()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), $"roslyn-agent-cli-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, "Broken.cs");

        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Broken
                {
                    public void Run()
                    {
                        int value = ;
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "diag.get_solution_snapshot",
                    directoryPath,
                    "--mode", "compact",
                    "--severity-filter", "Error",
                    "--severity-filter", "Warning",
                    "--max-diagnostics", "3",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"diag.get_solution_snapshot\"", output);
            Assert.Contains("\"mode\": \"compact\"", output);
            Assert.Contains("\"severity_filter\": [", output);
            Assert.Contains("\"Error\"", output);
            Assert.Contains("\"Warning\"", output);
            Assert.Contains("\"max_diagnostics\": 3", output);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task DirectCommand_MemberSource_AcceptsPositionalShorthand()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Add(int left, int right)
                    {
                        return left + right;
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.member_source", filePath, "3", "22", "body" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"ctx.member_source\"", output);
            Assert.Contains("return left", output);
            Assert.Contains("right;", output);
            Assert.Contains("\"edit_target\"", output);
            Assert.Contains("\"replace_span_operation\"", output);
            Assert.Contains("\"exact_span_text\"", output);
            Assert.Contains("\"trivia\"", output);
            Assert.Contains("\"edit_workflow\"", output);
            Assert.Contains("edit.claim", output);
            Assert.Contains("edit.batch_exact", output);
            Assert.Contains("edit.replace_text", output);
            Assert.Contains("edit.transaction", output);

            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement editTarget = document.RootElement.GetProperty("Data").GetProperty("edit_target");
            string exactSpanText = editTarget.GetProperty("exact_span_text").GetProperty("text").GetString()!;
            string preservedPrefix = editTarget.GetProperty("trivia").GetProperty("preserved_line_prefix_text").GetString()!;
            Assert.StartsWith("{", exactSpanText, StringComparison.Ordinal);
            Assert.Equal("    ", preservedPrefix);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_MemberSource_FocusTextPreviewReportsMatchedLine()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public void Run()
                    {
                        Step1();
                        ImportantMarker();
                        Step2();
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.member_source", filePath, "3", "17", "member", "--focus-text", "ImportantMarker", "--context-lines-before", "1", "--context-lines-after", "1" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("focus=matched:6", output);
            Assert.Contains("\"include_edit_target_text\": false", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_MemberSource_AcceptsMemberNameAnchor()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public void First()
                    {
                        Step1();
                    }

                    public void TargetCase()
                    {
                        ImportantMarker();
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.member_source", filePath, "--member-name", "TargetCase", "--focus-text", "ImportantMarker", "--context-lines-before", "1", "--context-lines-after", "1" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("TargetCase", output);
            Assert.Contains("focus=matched:10", output);
            Assert.Contains("\"member_name\": \"TargetCase\"", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_MemberSourceMissingMemberSuggestsOutlineRecovery()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-member-missing-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public sealed class Demo
                {
                    public void ActualTarget()
                    {
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.member_source", filePath, "--member-name", "MissingTarget" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("\"Code\": \"member_not_found\"", output);
            Assert.Contains("ctx.file_outline", output);
            Assert.Contains("--member-name-contains MissingTarget --max-members 20", output);
            Assert.Contains("line/column anchor", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_SearchText_AcceptsPositionalShorthand()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-search-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Target.cs");
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Remote
                {
                    public string Action => "RemoteUserAction";
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.search_text", "RemoteUserAction", tempDir, "--mode", "literal", "--max-results", "10" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"ctx.search_text\"", output);
            Assert.Contains("\"analysis_mode\": \"directory_scan\"", output);
            Assert.Contains("\"total_matches\": 1", output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_SearchText_AcceptsFilePathAndTextAliases()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-search-alias-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Target.cs");
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Remote
                {
                    public string Action => "RemoteUserAction";
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.search_text", "--file-path", filePath, "--text", "RemoteUserAction", "--mode", "literal", "--max-results", "10" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"ctx.search_text\"", output);
            Assert.Contains("\"analysis_mode\": \"directory_scan\"", output);
            Assert.Contains("\"file_path\":", output);
            Assert.Contains("\"total_matches\": 1", output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_SearchText_InfersSingleTopLevelSolutionWhenScopeOmitted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-search-solution-{Guid.NewGuid():N}");
        string originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.CreateDirectory(tempDir);
            string solutionPath = Path.Combine(tempDir, "Demo.slnx");
            await File.WriteAllTextAsync(solutionPath, "<Solution></Solution>");
            Directory.SetCurrentDirectory(tempDir);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.search_text", "--pattern", "AccessibilityTelemetryScroll", "--file-glob", "*.cs", "--max-results", "20", "--context-lines", "0" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"workspace_path\":", output);
            Assert.Contains("Demo.slnx", output);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_SearchText_SummarizesNarrowingGuidance()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-search-guidance-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Target.cs");
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllLinesAsync(
                filePath,
                Enumerable.Range(0, 25).Select(index => $"public class Remote{index} {{ public string Action => \"RepeatedEvidenceMarker\"; }}"));

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.search_text", "--file-path", filePath, "--text", "RepeatedEvidenceMarker", "--max-results", "80", "--context-lines", "1" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"result_guidance\": {", output);
            Assert.Contains("\"Summary\": \"ctx.search_text ok: matches=25, files=1, guidance=narrow\"", output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_ChangedFiles_ReportsCSharpDirtyCount()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-changed-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await RunProcessAsync("git", tempDir, "init");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "Target.cs"), "public class Target { }");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "ctx.changed_files", tempDir },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"ctx.changed_files\"", output);
            Assert.Contains("\"csharp_changed\": 1", output);
            Assert.Contains("\"Summary\": \"ctx.changed_files ok: changed=1, csharp=1\"", output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_FindSymbol_AcceptsDeclarationAndSnippetOptions()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Worker
                {
                    public Worker Next => new Worker();
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "nav.find_symbol",
                    filePath,
                    "Worker",
                    "--declarations-only", "true",
                    "--first-declaration", "true",
                    "--snippet-single-line", "true",
                    "--max-snippet-chars", "40",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"nav.find_symbol\"", output);
            Assert.Contains("\"declarations_only\": true", output);
            Assert.Contains("\"first_declaration\": true", output);
            Assert.Contains("\"snippet_single_line\": true", output);
            Assert.Contains("\"max_snippet_chars\": 40", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_FindSymbolBatch_AcceptsQueriesFileShorthand()
    {
        string filePath = Path.GetTempFileName();
        string queriesPath = Path.Combine(Path.GetTempPath(), $"roslynskills-queries-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Worker
                {
                    public Worker Next => new Worker();
                }
                """);

            await File.WriteAllTextAsync(
                queriesPath,
                $$"""
                [
                  { "file_path": "{{filePath.Replace("\\", "\\\\")}}", "symbol_name": "Worker", "label": "decl" },
                  { "file_path": "{{filePath.Replace("\\", "\\\\")}}", "symbol_name": "Next" }
                ]
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "nav.find_symbol_batch",
                    "--queries", $"@{queriesPath}",
                    "--brief", "true",
                    "--first-declaration", "true",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"nav.find_symbol_batch\"", output);
            Assert.Contains("\"total_executed\": 2", output);
            Assert.Contains("\"succeeded\": 2", output);
            Assert.Contains("\"label\": \"decl\"", output);
        }
        finally
        {
            File.Delete(filePath);
            File.Delete(queriesPath);
        }
    }

    [Fact]
    public async Task DirectCommand_QueryBatch_AcceptsQueriesFileShorthand()
    {
        string filePath = Path.GetTempFileName();
        string queriesPath = Path.Combine(Path.GetTempPath(), $"roslynskills-batch-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Worker
                {
                    public Worker Next => new Worker();
                }
                """);

            await File.WriteAllTextAsync(
                queriesPath,
                $$"""
                [
                  {
                    "command_id": "ctx.search_text",
                    "input": {
                      "patterns": [ "Worker" ],
                      "mode": "literal",
                      "file_path": "{{filePath.Replace("\\", "\\\\")}}"
                    }
                  },
                  {
                    "command_id": "nav.find_symbol",
                    "input": {
                      "file_path": "{{filePath.Replace("\\", "\\\\")}}",
                      "symbol_name": "Next",
                      "brief": true
                    }
                  }
                ]
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "query.batch",
                    $"@{queriesPath}",
                    "--continue-on-error", "true",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"query.batch\"", output);
            Assert.Contains("\"total_executed\": 2", output);
            Assert.Contains("\"succeeded\": 2", output);
        }
        finally
        {
            File.Delete(filePath);
            File.Delete(queriesPath);
        }
    }

    [Fact]
    public async Task DirectCommand_FindInvocations_AcceptsPositionalShorthand()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Add(int left, int right)
                    {
                        return left + right;
                    }

                    public int Run()
                    {
                        return Add(1, 2);
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "nav.find_invocations", filePath, "3", "16", "--brief", "true", "--max-results", "10" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"nav.find_invocations\"", output);
            Assert.Contains("\"total_matches\": 1", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_CallHierarchy_AcceptsPositionalShorthand()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Add(int left, int right)
                    {
                        return left + right;
                    }

                    public int Run()
                    {
                        return Add(1, 2);
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "nav.call_hierarchy", filePath, "3", "16", "--direction", "incoming", "--max-depth", "1", "--brief", "true" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"nav.call_hierarchy\"", output);
            Assert.Contains("\"direction\": \"incoming\"", output);
            Assert.Contains("\"total_nodes\":", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_AnalyzeUnusedPrivateSymbols_AcceptsPositionalShorthand()
    {
        string root = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-analyze-unused-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "Unused.cs");
        await File.WriteAllTextAsync(
            filePath,
            """
            public class Demo
            {
                private int _unused = 1;
            }
            """);

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "analyze.unused_private_symbols", root, "--brief", "true", "--max-symbols", "20" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"analyze.unused_private_symbols\"", output);
            Assert.Contains("\"unused_symbols\":", output);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_AnalyzeControlFlowGraph_AcceptsPositionalShorthand()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-cfg-{Guid.NewGuid():N}.cs");

        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Compute(int value)
                    {
                        if (value > 0)
                        {
                            return value + 1;
                        }

                        return value - 1;
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "analyze.control_flow_graph", filePath, "3", "20", "--brief", "true", "--max-blocks", "50", "--max-edges", "100" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"analyze.control_flow_graph\"", output);
            Assert.Contains("\"cfg_summary\":", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_AnalyzeCfg_ReturnsUnknownVerb()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "analyze.cfg" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("unknown_verb", output);
        Assert.Contains("analyze.cfg", output);
    }

    [Fact]
    public async Task DirectCommand_RenameSymbol_AcceptsPositionalShorthand()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Add(int left, int right)
                    {
                        return left + right;
                    }

                    public int Run()
                    {
                        return Add(1, 2);
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "edit.rename_symbol", filePath, "3", "16", "Sum" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"edit.rename_symbol\"", output);
            Assert.Contains("\"wrote_file\": true", output);
            Assert.Contains("public int Sum", await File.ReadAllTextAsync(filePath));
            Assert.Contains("return Sum(1, 2);", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_CreateFile_AcceptsPositionalShorthand()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-create-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "NewType.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "edit.create_file",
                    filePath,
                    "--content", "public class NewType { }",
                    "--overwrite", "false",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"edit.create_file\"", output);
            Assert.Contains("\"wrote_file\": true", output);
            Assert.Contains("public class NewType { }", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_ReplaceText_AcceptsExactSnippetShorthand()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-replace-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(filePath, "public class Demo { string Title => \"Help\"; }");

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "edit.replace_text",
                    filePath,
                    "--old-text", "\"Help\"",
                    "--new-text", "BuildHelpTitle(state.HelpOverlayScroll)",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"edit.replace_text\"", output);
            Assert.Contains("\"match_count\": 1", output);
            Assert.Contains("\"wrote_file\": true", output);
            Assert.Contains("\"claim_status\"", output);
            Assert.Contains("\"claimed\": false", output);
            Assert.Contains("unclaimed", output);
            Assert.Contains("BuildHelpTitle(state.HelpOverlayScroll)", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_ReplaceText_UsesWorkspaceDiagnosticsWhenWorkspacePathProvided()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-replace-workspace-{Guid.NewGuid():N}");
        string projectPath = Path.Combine(tempDir, "Demo.csproj");
        string filePath = Path.Combine(tempDir, "Demo.cs");
        string helperPath = Path.Combine(tempDir, "Helper.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <Nullable>disable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                helperPath,
                """
                namespace Smoke;

                public static class Helper
                {
                    public static int Value => 42;
                }
                """);
            await File.WriteAllTextAsync(
                filePath,
                """
                namespace Smoke;

                public class Demo
                {
                    public int Run() => 1;
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "edit.replace_text",
                    filePath,
                    "--old-text", "=> 1",
                    "--new-text", "=> Helper.Value",
                    "--workspace-path", projectPath,
                    "--apply", "false",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"mode\": \"workspace_updated_source\"", output);
            Assert.Contains("\"errors\": 0", output);
            Assert.DoesNotContain("CS0103", output);
            Assert.Contains("public int Run() => 1;", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_ReplaceInMember_ReportsMatchLineInPreview()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-replace-member-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public void First()
                    {
                        var value = 1;
                    }

                    public void Second()
                    {
                        var value = 1;
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "edit.replace_in_member",
                    filePath,
                    "--member-name", "Second",
                    "--old-text", "var value = 1;",
                    "--new-text", "var value = 2;",
                    "--include-diagnostics", "false",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"edit.replace_in_member\"", output);
            Assert.Contains("\"matches\": [", output);
            Assert.Contains("\"line\":", output);
            Assert.Contains("\"text_preview\": \"var value = 1;\"", output);
            Assert.Contains("\"new_text_preview\": \"var value = 2;\"", output);
            Assert.Contains("line=", output);
            Assert.Contains("var value = 2;", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_ReplaceInMember_KeepsCommaBearingTextAsString()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-replace-member-commas-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public void Target()
                    {
                        var controls = ShowcaseFrameHitRegistry.HitTest(state, 75, 20);
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "edit.replace_in_member",
                    filePath,
                    "--member-name", "Target",
                    "--old-text", "var controls = ShowcaseFrameHitRegistry.HitTest(state, 75, 20);",
                    "--new-text", "var controls = ShowcaseFrameHitRegistry.HitTest(state, 75, 22);",
                    "--include-diagnostics", "false",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"edit.replace_in_member\"", output);
            Assert.Contains("line=", output);
            Assert.Contains("HitTest(state, 75, 22);", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectCommand_ReplaceText_RefreshesHotWorkspaceAfterWrite()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-replace-refresh-{Guid.NewGuid():N}");
        string projectPath = Path.Combine(tempDir, "Demo.csproj");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <Nullable>disable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Run() => 1;
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter preloadOut = new();
            StringWriter preloadErr = new();
            int preloadExit = await app.RunAsync(
                new[] { "--no-daemon", "workspace.preload", projectPath, "--alias", "default" },
                preloadOut,
                preloadErr,
                CancellationToken.None);

            Assert.True(preloadExit == 0, preloadOut.ToString());

            StringWriter replaceOut = new();
            StringWriter replaceErr = new();
            int replaceExit = await app.RunAsync(
                new[]
                {
                    "--no-daemon",
                    "edit.replace_text",
                    filePath,
                    "--old-text", "=> 1",
                    "--new-text", "=> 2",
                },
                replaceOut,
                replaceErr,
                CancellationToken.None);

            string replaceOutput = replaceOut.ToString();
            Assert.Equal(0, replaceExit);
            Assert.Contains("\"matched_workspace_count\": 1", replaceOutput);
            Assert.Contains("\"refresh_action\": \"incremental_document_update\"", replaceOutput);

            StringWriter memberOut = new();
            StringWriter memberErr = new();
            int memberExit = await app.RunAsync(
                new[] { "--no-daemon", "ctx.member_source", filePath, "3", "16", "--max-chars", "2000" },
                memberOut,
                memberErr,
                CancellationToken.None);

            string memberOutput = memberOut.ToString();
            Assert.Equal(0, memberExit);
            Assert.Contains("=> 2", memberOutput);
            Assert.DoesNotContain("=> 1", memberOutput);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BatchExactEdit_AppliesSequentialReplaceAndInsert()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-batch-exact-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public string Title => "Help";
                }
                """);

            string input = JsonSerializer.Serialize(new
            {
                file_path = filePath,
                operations = new object[]
                {
                    new
                    {
                        kind = "replace_text",
                        old_text = "\"Help\"",
                        new_text = "BuildTitle()",
                    },
                    new
                    {
                        kind = "insert_text",
                        anchor_text = "    public string Title => BuildTitle();",
                        insert_text = "\n\n    private static string BuildTitle() => \"Help\";",
                        position = "after",
                    },
                },
                apply = true,
            });

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();
            int exitCode = await app.RunAsync(
                new[] { "--no-daemon", "run", "edit.batch_exact", "--input", input },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            string content = await File.ReadAllTextAsync(filePath);
            Assert.Equal(0, exitCode);
            Assert.Contains("\"succeeded_operations\": 2", output);
            Assert.Contains("\"wrote_file_count\": 1", output);
            Assert.Contains("operations=2/2, ok=2, failed=0, wrote_files=1", output);
            Assert.Contains("public string Title => BuildTitle();", content);
            Assert.Contains("private static string BuildTitle() => \"Help\";", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BatchExactEdit_AppliesReplaceSpanWithExpectedTextGuard()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-batch-span-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            string original = """
                public class Demo
                {
                    public string Title => "Help";
                }
                """;
            await File.WriteAllTextAsync(filePath, original);

            int spanStart = original.IndexOf("\"Help\"", StringComparison.Ordinal);
            string input = JsonSerializer.Serialize(new
            {
                file_path = filePath,
                operations = new object[]
                {
                    new
                    {
                        kind = "replace_span",
                        span_start = spanStart,
                        span_length = "\"Help\"".Length,
                        expected_text = "\"Help\"",
                        new_text = "\"Evidence\"",
                    },
                },
                apply = true,
                atomic = true,
            });

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();
            int exitCode = await app.RunAsync(
                new[] { "--no-daemon", "run", "edit.batch_exact", "--input", input },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            string content = await File.ReadAllTextAsync(filePath);
            Assert.Equal(0, exitCode);
            Assert.Contains("\"kind\": \"replace_span\"", output);
            Assert.Contains("\"span_start\":", output);
            Assert.Contains("\"succeeded_operations\": 1", output);
            Assert.Contains("public string Title => \"Evidence\";", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BatchExactEdit_AtomicFailureDoesNotWritePartialChanges()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-batch-exact-atomic-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int A => 1;
                    public int B => 2;
                }
                """);

            string input = JsonSerializer.Serialize(new
            {
                file_path = filePath,
                operations = new object[]
                {
                    new
                    {
                        kind = "replace_text",
                        old_text = "=> 1",
                        new_text = "=> 10",
                    },
                    new
                    {
                        kind = "replace_text",
                        old_text = "missing text",
                        new_text = "never written",
                    },
                },
                apply = true,
                atomic = true,
            });

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();
            int exitCode = await app.RunAsync(
                new[] { "--no-daemon", "run", "edit.batch_exact", "--input", input },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            string content = await File.ReadAllTextAsync(filePath);
            Assert.Equal(1, exitCode);
            Assert.Contains("\"failed_operations\": 1", output);
            Assert.Contains("\"skipped_apply_due_to_errors\": true", output);
            Assert.Contains("old_text_not_found", output);
            Assert.Contains("public int A => 1;", content);
            Assert.DoesNotContain("=> 10", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BatchExactEdit_AmbiguousTextFailureIncludesRecoveryHint()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-batch-exact-ambiguous-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int A => 1;
                    public int B => 1;
                }
                """);

            string input = JsonSerializer.Serialize(new
            {
                file_path = filePath,
                operations = new object[]
                {
                    new
                    {
                        kind = "replace_text",
                        old_text = "=> 1",
                        new_text = "=> 10",
                    },
                },
                apply = true,
                atomic = true,
            });

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();
            int exitCode = await app.RunAsync(
                new[] { "--no-daemon", "run", "edit.batch_exact", "--input", input },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            string content = await File.ReadAllTextAsync(filePath);
            Assert.Equal(1, exitCode);
            Assert.Contains("old_text_ambiguous", output);
            Assert.Contains("recovery_hint", output);
            Assert.Contains("ctx.member_source", output);
            Assert.Contains("replace_span", output);
            Assert.Contains("public int A => 1;", content);
            Assert.Contains("public int B => 1;", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("edit.replace_text")]
    [InlineData("edit.insert_text")]
    [InlineData("edit.batch_exact")]
    public void ExactEditBridgeCommands_AreDaemonCapable(string commandId)
    {
        System.Reflection.MethodInfo method = typeof(CliApplication).GetMethod(
            "IsDaemonCapableCommand",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        bool result = (bool)method.Invoke(null, new object[] { commandId })!;

        Assert.True(result);
    }

    [Fact]
    public void BuildDataSummary_UsesNestedHostEnvelopeDataForDaemonRoutedExactEdit()
    {
        System.Reflection.MethodInfo method = typeof(CliApplication).GetMethod(
            "BuildDataSummary",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object data = new
        {
            envelope = new
            {
                Data = new
                {
                    file_path = @"C:\Temp\Demo.cs",
                    match_count = 1,
                    wrote_file = true,
                },
            },
        };

        string? summary = (string?)method.Invoke(null, new object[] { "edit.replace_text", data });

        Assert.Equal("Demo.cs, matches=1, written", summary);
    }

    [Fact]
    public async Task DescribeCommand_ReplaceText_IncludesMutationBridgeGuidance()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "edit.replace_text" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("edit.replace_text <file-path>", output);
        Assert.Contains("edit.claim", output);
        Assert.Contains("old_text", output);
        Assert.Contains("replace_all", output);
    }


    [Fact]
    public async Task DescribeCommand_ReplaceInMember_IncludesSameMemberGuidance()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "edit.replace_in_member" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("edit.replace_in_member <file-path>", output);
        Assert.Contains("same member", output);
        Assert.Contains("Direct shorthand is intended for short one-line old_text/new_text", output);
        Assert.Contains("preserves comma-bearing C# snippets", output);
        Assert.Contains("--input @payload.json", output);
        Assert.Contains("--input-stdin", output);
        Assert.Contains("do not run parallel edit commands", output);
    }

    [Fact]
    public async Task DirectCommand_InsertText_AcceptsAnchorShorthand()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-insert-{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "Demo.cs");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(filePath, "public class Demo\n{\n    public int A => 1;\n}\n");

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[]
                {
                    "edit.insert_text",
                    filePath,
                    "--anchor-text", "    public int A => 1;",
                    "--insert-text", "\n    public int B => 2;",
                    "--position", "after",
                },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"edit.insert_text\"", output);
            Assert.Contains("\"position\": \"after\"", output);
            Assert.Contains("\"wrote_file\": true", output);
            Assert.Contains("public int B => 2", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DescribeCommand_InsertText_IncludesAnchorGuidance()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "edit.insert_text" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("edit.insert_text <file-path>", output);
        Assert.Contains("anchor_text", output);
        Assert.Contains("insert_text", output);
        Assert.Contains("position", output);
    }

    [Fact]
    public async Task DescribeCommand_SessionOpen_IncludesUsageHints()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "session.open" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"usage\": {", output);
        Assert.Contains("session.open <file-path> [session-id]", output);
        Assert.Contains(".sln/.slnx/.csproj", output);
    }

    [Fact]
    public async Task DescribeCommand_FindSymbol_IncludesWorkspaceHints()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "nav.find_symbol" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"workspace_path\"", output);
        Assert.Contains("\"require_workspace\"", output);
        Assert.Contains("\"declarations_only\"", output);
        Assert.Contains("\"first_declaration\"", output);
        Assert.Contains("workspace_context.mode", output);
    }

    [Fact]
    public async Task DescribeCommand_FindSymbolBatch_IncludesQueriesHints()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "nav.find_symbol_batch" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"queries\"", output);
        Assert.Contains("\"continue_on_error\"", output);
        Assert.Contains("--queries @file.json", output);
    }

    [Fact]
    public async Task DescribeCommand_CallHierarchy_IndicatesAdvancedHeuristic()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "nav.call_hierarchy" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Maturity\": \"advanced\"", output);
        Assert.Contains("\"heuristic\"", output);
    }

    [Fact]
    public async Task DescribeCommand_AsyncRiskScan_IndicatesExperimentalHeuristic()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "describe-command", "analyze.async_risk_scan" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Maturity\": \"experimental\"", output);
        Assert.Contains("\"heuristic\"", output);
    }

    [Fact]
    public async Task Quickstart_ReturnsPitOfSuccessGuidance()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "quickstart" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"CommandId\": \"cli.quickstart\"", output);
        Assert.Contains("pit_of_success", output);
        Assert.Contains("dotnet-inspect", output);
        Assert.Contains("session.open only supports .cs/.csx files", output);
        Assert.Contains("workspace_context.mode", output);
        Assert.Contains("--require-workspace true", output);
        Assert.Contains("MySolution.slnx", output);
        Assert.Contains("project_count", output);
        Assert.Contains("src/MyProject/Program.cs", output);
        Assert.Contains("span_member_edit_without_double_indent", output);
        Assert.Contains("include_edit_target_text=true", output);
        Assert.Contains("edit_target.exact_span_text.text", output);
        Assert.Contains("csharp_fresh_session", output);
        Assert.Contains("workspace.preload MySolution.slnx --alias default --require-solution true", output);
        Assert.Contains("roscli csharp-start", output);
        Assert.Contains("roscli csharp-start --supervised", output);
        Assert.Contains("ctx.file_outline tests/MyTests.cs --member-name-contains Target", output);
        Assert.Contains("edit.replace_in_member tests/MyTests.cs --member-name TargetTest", output);
        Assert.Contains("Do not use git diff, rg, Get-Content, sed, cat, or patch-editor reads for .cs orientation", output);
    }

    [Fact]
    public async Task CSharpStart_ReturnsOperationalAgentGuide()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "csharp-start" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("# roscli csharp-start", output);
        Assert.Contains("workspace.preload MySolution.slnx --alias default --require-solution true", output);
        Assert.Contains("ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text", output);
        Assert.Contains("ctx.search_text --pattern \"class Target\"", output);
        Assert.Contains("If you only know a type/member name but not the file", output);
        Assert.Contains("Data.edit_target.exact_span_text.text", output);
        Assert.Contains("--max-results 20 --context-lines 0", output);
        Assert.Contains("edit.claim list", output);
        Assert.Contains("edit.replace_in_member tests/MyTests.cs --member-name TargetTest", output);
        Assert.Contains("Multi-Agent Coordination", output);
        Assert.Contains("distinct stable `--owner` values", output);
        Assert.Contains("each subagent claims before its first edit", output);
        Assert.Contains("same-member multi-edit", output);
        Assert.Contains("Do not run parallel edit commands against the same member", output);
        Assert.Contains("Do not start `.cs` orientation with `git diff`, `rg`, `Get-Content`, `sed`, `cat`, or patch-editor reads", output);
    }

    [Fact]
    public async Task CSharpStartSupervised_ReturnsTwoTurnProtocol()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "csharp-start", "--supervised" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("Supervised Two-Turn Protocol", output);
        Assert.Contains("Run exactly this command now", output);
        Assert.Contains("Ran roscli csharp-start", output);
        Assert.Contains("do not treat prose promises as compliance", output);
        Assert.Contains("edit.claim list, ctx.changed_files, workspace.preload <solution.sln|.slnx> --alias default --require-solution true, compact ctx.file_outline filters, ctx.member_source with small focus windows", output);
        Assert.Contains("start 3-12 lines, not 80+", output);
        Assert.Contains("edit.claim claim for every file before mutation", output);
        Assert.Contains("If subagents are used, assign disjoint claimed files", output);
        Assert.Contains("Run roscli and dotnet commands sequentially per repo", output);
        Assert.Contains("If a broad ctx.search_text returns many matches, stop broad searching", output);
        Assert.Contains("When consulting non-C# upstream/reference files", output);
        Assert.Contains("rg -n -C 2 -m 40", output);
        Assert.Contains("Use ctx.search_text or ctx.member_source for .cs closeout line anchors", output);
        Assert.Contains("Report startup evidence explicitly", output);
        Assert.Contains("do not fall back to `rg` just to find the line you changed", output);
    }

    [Fact]
    public async Task AgentStart_ReturnsSupervisedFirstCommandProtocol()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "agent-start" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("# roscli agent-start", output);
        Assert.DoesNotContain("# roscli csharp-start", output);
        Assert.Contains("Supervised Two-Turn Protocol", output);
        Assert.Contains("Run exactly this command now, then stop and report the first two headings it prints: roscli agent-start", output);
        Assert.Contains("Ran roscli agent-start", output);
        Assert.Contains("do not treat prose promises as compliance", output);
    }

    [Fact]
    public async Task AgentStart_WithSolution_ReturnsConcretePreload()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "agent-start", "--solution", "FrankenTui.Net.slnx" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("workspace.preload FrankenTui.Net.slnx --alias default --require-solution true", output);
        Assert.Contains("ctx.changed_files", output);
        Assert.Contains("edit.claim claim for every file before mutation", output);
    }

    [Fact]
    public async Task CSharpStartSupervised_WithSolution_ReturnsConcretePreload()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "csharp-start", "--supervised", "--solution", "FrankenTui.Net.sln" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("workspace.preload FrankenTui.Net.sln --alias default --require-solution true", output);
        Assert.DoesNotContain("workspace.preload <solution.sln|.slnx>", output);
        Assert.Contains("roscli workspace.preload FrankenTui.Net.sln --alias default --require-solution true", output);
    }


    [Fact]
    public async Task Llmstxt_Default_ReturnsStableBootstrapGuide()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "llmstxt" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("# roscli llmstxt", output);
        Assert.Contains("run `roscli csharp-start` before `.cs` text reads", output);
        Assert.Contains("roscli csharp-start --supervised", output);
        Assert.Contains("catalog mode: `stable-only`", output);
        Assert.Contains("## Fast Start (Low Round-Trips)", output);
        Assert.Contains("`nav.find_symbol`", output);
        Assert.Contains("Default Rich Lander companion", output);
        Assert.Contains("dotnet-inspect", output);
        Assert.Contains("dotnet-skills", output);
        Assert.Contains("`session.open` supports only `.cs/.csx` files.", output);
        Assert.Contains("MySolution.slnx", output);
        Assert.Contains("project_count", output);
        Assert.Contains("include-edit-target-text true", output);
        Assert.Contains("edit_target.exact_span_text.text", output);
        Assert.DoesNotContain("`ctx.call_chain_slice`", output);
    }

    [Fact]
    public async Task Llmstxt_Full_IncludesAdvancedCatalog()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "llmstxt", "--full" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("catalog mode: `all`", output);
        Assert.Contains("`ctx.call_chain_slice`", output);
        Assert.Contains("| traits:", output);
    }
    [Fact]
    public async Task Help_IncludesQuickstartCommand()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "--help" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("version", output);
        Assert.Contains("quickstart", output);
        Assert.Contains("csharp-start", output);
        Assert.Contains("agent-start", output);
        Assert.Contains("first command", output);
        Assert.Contains("before any `.cs` git diff", output);
        Assert.Contains("ctx.changed_files", output);
        Assert.Contains("llmstxt", output);
        Assert.Contains("pit-of-success", output);
    }

    [Fact]
    public async Task VersionFlag_ReturnsVersionEnvelope()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "--version" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"CommandId\": \"cli.version\"", output);
        Assert.Contains("\"cli_version\":", output);
        Assert.Contains("\"informational_version\":", output);
        Assert.Contains("\"tool_command\": \"roscli\"", output);
    }

    [Fact]
    public async Task VersionVerb_ReturnsVersionEnvelope()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "version" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"CommandId\": \"cli.version\"", output);
        Assert.Contains("\"cli_version\":", output);
        Assert.Contains("\"Summary\": \"cli.version ok: roscli", output);
    }

    [Fact]
    public async Task UnknownCommand_ErrorHintsIncludeQuickstart()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "not-a-command" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("unknown_verb", output);
        Assert.Contains("quickstart", output);
        Assert.Contains("llmstxt", output);
    }

    [Fact]
    public async Task DirectCommand_SessionOpenAndClose_AcceptsPositionalShorthand()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-session-{Guid.NewGuid():N}.cs");
        string sessionId = $"cli-test-{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public void Run() { }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter openStdout = new();
            StringWriter openStderr = new();

            int openExitCode = await app.RunAsync(
                new[] { "session.open", filePath, sessionId },
                openStdout,
                openStderr,
                CancellationToken.None);

            Assert.Equal(0, openExitCode);
            Assert.Contains("\"CommandId\": \"session.open\"", openStdout.ToString());

            StringWriter statusStdout = new();
            StringWriter statusStderr = new();
            int statusExitCode = await app.RunAsync(
                new[] { "session.status", sessionId },
                statusStdout,
                statusStderr,
                CancellationToken.None);

            Assert.Equal(0, statusExitCode);
            Assert.Contains("\"CommandId\": \"session.status\"", statusStdout.ToString());

            StringWriter closeStdout = new();
            StringWriter closeStderr = new();
            int closeExitCode = await app.RunAsync(
                new[] { "session.close", sessionId },
                closeStdout,
                closeStderr,
                CancellationToken.None);

            Assert.Equal(0, closeExitCode);
            Assert.Contains("\"CommandId\": \"session.close\"", closeStdout.ToString());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_SessionOpen_RejectsSolutionFile()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-{Guid.NewGuid():N}.slnx");
        await File.WriteAllTextAsync(filePath, "<Solution />");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "session.open", filePath },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("unsupported_file_type", output);
            Assert.Contains(".cs/.csx", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_SessionOpen_RejectsSolutionAliasForFilePath()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(filePath, "public class Demo { }");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "session.open", "--solution", filePath },
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            string output = stdout.ToString();
            Assert.Contains("invalid_args", output);
            Assert.Contains("session.open <file-path> [session-id]", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_SessionOpen_AcceptsGitBashStyleWindowsPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-{Guid.NewGuid():N}.cs");
        string sessionId = $"bash-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(filePath, "public class Demo { }");

        try
        {
            string bashPath = "/" + char.ToLowerInvariant(filePath[0]) + filePath[2..].Replace('\\', '/');

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "session.open", bashPath, sessionId },
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"session.open\"", stdout.ToString());

            StringWriter closeStdout = new();
            StringWriter closeStderr = new();
            int closeExitCode = await app.RunAsync(
                new[] { "session.close", sessionId },
                closeStdout,
                closeStderr,
                CancellationToken.None);
            Assert.Equal(0, closeExitCode);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_SessionOpen_InvalidArgs_IncludeDescribeCommandHint()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "session.open", "--solution" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("describe-command session.open", output);
    }

    [Fact]
    public async Task DirectCommand_SessionApplyTextEdits_AcceptsStructuredInputViaStdin()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-session-edit-{Guid.NewGuid():N}.cs");
        string sessionId = $"cli-test-edit-{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Sum()
                    {
                        return 1 + 2;
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter openStdout = new();
            StringWriter openStderr = new();

            int openExitCode = await app.RunAsync(
                new[] { "session.open", filePath, sessionId },
                openStdout,
                openStderr,
                CancellationToken.None);

            Assert.Equal(0, openExitCode);

            string applyPayload =
                $$"""
                {
                  "session_id": "{{sessionId}}",
                  "edits": [
                    {
                      "start_line": 5,
                      "start_column": 16,
                      "end_line": 5,
                      "end_column": 21,
                      "new_text": "3 + 4"
                    }
                  ]
                }
                """;

            StringWriter applyStdout = new();
            StringWriter applyStderr = new();
            int applyExitCode = await app.RunAsync(
                new[] { "session.apply_text_edits", "--input-stdin" },
                applyStdout,
                applyStderr,
                CancellationToken.None,
                new StringReader(applyPayload));

            Assert.Equal(0, applyExitCode);
            Assert.Contains("\"CommandId\": \"session.apply_text_edits\"", applyStdout.ToString());
            Assert.Contains("\"changed\": true", applyStdout.ToString());

            StringWriter commitStdout = new();
            StringWriter commitStderr = new();
            int commitExitCode = await app.RunAsync(
                new[]
                {
                    "session.commit",
                    sessionId,
                    "--keep-session", "true",
                    "--require-disk-unchanged", "true",
                    "--expected-generation", "1",
                },
                commitStdout,
                commitStderr,
                CancellationToken.None);
            Assert.Equal(0, commitExitCode);
            Assert.Contains("\"expected_generation\": 1", commitStdout.ToString());
            Assert.Contains("return 3 + 4;", await File.ReadAllTextAsync(filePath));

            StringWriter closeStdout = new();
            StringWriter closeStderr = new();
            int closeExitCode = await app.RunAsync(
                new[] { "session.close", sessionId },
                closeStdout,
                closeStderr,
                CancellationToken.None);
            Assert.Equal(0, closeExitCode);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DirectCommand_SessionApplyAndCommit_AcceptsStructuredInputViaStdin()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-session-apply-{Guid.NewGuid():N}.cs");
        string sessionId = $"cli-test-apply-commit-{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllTextAsync(
                filePath,
                """
                public class Demo
                {
                    public int Sum()
                    {
                        return 1 + 2;
                    }
                }
                """);

            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter openStdout = new();
            StringWriter openStderr = new();
            int openExitCode = await app.RunAsync(
                new[] { "session.open", filePath, sessionId },
                openStdout,
                openStderr,
                CancellationToken.None);
            Assert.Equal(0, openExitCode);

            string applyCommitPayload =
                $$"""
                {
                  "session_id": "{{sessionId}}",
                  "expected_generation": 0,
                  "edits": [
                    {
                      "start_line": 5,
                      "start_column": 16,
                      "end_line": 5,
                      "end_column": 21,
                      "new_text": "7 + 8"
                    }
                  ]
                }
                """;

            StringWriter applyCommitStdout = new();
            StringWriter applyCommitStderr = new();
            int applyCommitExitCode = await app.RunAsync(
                new[] { "session.apply_and_commit", "--input-stdin" },
                applyCommitStdout,
                applyCommitStderr,
                CancellationToken.None,
                new StringReader(applyCommitPayload));
            Assert.Equal(0, applyCommitExitCode);
            Assert.Contains("\"CommandId\": \"session.apply_and_commit\"", applyCommitStdout.ToString());
            Assert.Contains("\"wrote_file\": true", applyCommitStdout.ToString());
            Assert.Contains("return 7 + 8;", await File.ReadAllTextAsync(filePath));

            StringWriter statusStdout = new();
            StringWriter statusStderr = new();
            int statusExitCode = await app.RunAsync(
                new[] { "session.status", sessionId },
                statusStdout,
                statusStderr,
                CancellationToken.None);
            Assert.Equal(1, statusExitCode);
            Assert.Contains("session_not_found", statusStdout.ToString());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static async Task RunProcessAsync(string fileName, string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr) || stderr.Contains("hint:", StringComparison.OrdinalIgnoreCase), stderr);
    }
}

