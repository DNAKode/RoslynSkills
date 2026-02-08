using RoslynAgent.Benchmark.AgentEval;

namespace RoslynAgent.Benchmark.Tests;

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

            AgentEvalPreflightChecker checker = new(probe);
            AgentEvalPreflightReport report = await checker.RunAsync(outputDir, CancellationToken.None);

            Assert.False(report.all_required_available);
            Assert.Equal(5, report.checks.Count);
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

    private sealed class FakeProbe : ICommandProbe
    {
        private readonly Dictionary<string, bool> _availability = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string command, bool available)
        {
            _availability[command] = available;
        }

        public Task<ProbeResult> ProbeAsync(string command, string arguments, CancellationToken cancellationToken)
        {
            bool available = _availability.TryGetValue(command, out bool value) && value;
            return Task.FromResult(new ProbeResult(
                Available: available,
                ExitCode: available ? 0 : 127,
                Stdout: available ? $"{command} ok" : string.Empty,
                Stderr: available ? string.Empty : "missing"));
        }
    }
}
