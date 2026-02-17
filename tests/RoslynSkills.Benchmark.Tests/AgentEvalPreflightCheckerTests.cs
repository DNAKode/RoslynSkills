using RoslynSkills.Benchmark.AgentEval;

namespace RoslynSkills.Benchmark.Tests;

public sealed class AgentEvalPreflightCheckerTests
{
    [Fact]
    public async Task RunAsync_ReturnsRequiredFailureWhenMissingRequiredTool()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), $"preflight-test-{Guid.NewGuid():N}");
        try
        {
            FakeProbe probe = new();
            probe.Add("dotnet", true);
            probe.Add("git", false);
            probe.Add("rg", true);
            probe.Add("codex", false);
            probe.Add("claude", false);
            probe.Add("gemini", false);

            AgentEvalPreflightChecker checker = new(probe);
            AgentEvalPreflightReport report = await checker.RunAsync(outputDir, CancellationToken.None);

            Assert.False(report.all_required_available);
            Assert.Equal(6, report.checks.Count);
            Assert.True(File.Exists(report.output_path));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_OnWindows_UsesCmdShimFallbackForAgentCli()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string outputDir = Path.Combine(Path.GetTempPath(), $"preflight-test-{Guid.NewGuid():N}");
        try
        {
            FakeProbe probe = new();
            probe.Add("dotnet", true);
            probe.Add("git", true);
            probe.Add("rg", true);
            probe.Add("codex", false);
            probe.Add("codex.cmd", true);
            probe.Add("claude", false);
            probe.Add("claude.cmd", true);
            probe.Add("gemini", false);
            probe.Add("gemini.cmd", true);

            AgentEvalPreflightChecker checker = new(probe);
            AgentEvalPreflightReport report = await checker.RunAsync(outputDir, CancellationToken.None);

            Assert.True(report.all_required_available);
            Assert.Contains(report.checks, c => c.command == "codex" && c.available);
            Assert.Contains(report.checks, c => c.command == "claude" && c.available);
            Assert.Contains(report.checks, c => c.command == "gemini" && c.available);

            int codexIndex = probe.Calls.IndexOf("codex");
            int codexCmdIndex = probe.Calls.IndexOf("codex.cmd");
            int claudeIndex = probe.Calls.IndexOf("claude");
            int claudeCmdIndex = probe.Calls.IndexOf("claude.cmd");
            int geminiIndex = probe.Calls.IndexOf("gemini");
            int geminiCmdIndex = probe.Calls.IndexOf("gemini.cmd");
            Assert.True(codexIndex >= 0 && codexCmdIndex > codexIndex);
            Assert.True(claudeIndex >= 0 && claudeCmdIndex > claudeIndex);
            Assert.True(geminiIndex >= 0 && geminiCmdIndex > geminiIndex);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    private sealed class FakeProbe : ICommandProbe
    {
        private readonly Dictionary<string, bool> _availability = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = new();

        public void Add(string command, bool available)
        {
            _availability[command] = available;
        }

        public Task<ProbeResult> ProbeAsync(string command, string arguments, CancellationToken cancellationToken)
        {
            Calls.Add(command);
            bool available = _availability.TryGetValue(command, out bool value) && value;
            return Task.FromResult(new ProbeResult(
                Available: available,
                ExitCode: available ? 0 : 127,
                Stdout: available ? $"{command} ok" : string.Empty,
                Stderr: available ? string.Empty : "missing"));
        }
    }
}

