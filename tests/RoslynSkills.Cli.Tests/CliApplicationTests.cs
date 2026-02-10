using RoslynSkills.Cli;
using RoslynSkills.Core;

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
        Assert.Contains("nav.find_references", output);
        Assert.Contains("nav.find_implementations", output);
        Assert.Contains("nav.find_overrides", output);
        Assert.Contains("ctx.symbol_envelope", output);
        Assert.Contains("ctx.file_outline", output);
        Assert.Contains("ctx.member_source", output);
        Assert.Contains("ctx.call_chain_slice", output);
        Assert.Contains("ctx.dependency_slice", output);
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
        Assert.Contains("edit.transaction", output);
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
        Assert.DoesNotContain("\"InputSchemaVersion\"", output);
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
        }
        finally
        {
            File.Delete(filePath);
        }
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
    public async Task DirectCommand_SessionOpenAndClose_AcceptsPositionalShorthand()
    {
        string filePath = Path.GetTempFileName();
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
    public async Task DirectCommand_SessionApplyTextEdits_AcceptsStructuredInputViaStdin()
    {
        string filePath = Path.GetTempFileName();
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
        string filePath = Path.GetTempFileName();
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
}

