using RoslynAgent.Benchmark.AgentEval;

namespace RoslynAgent.Benchmark.Tests;

public sealed class AgentEvalGateRunnerTests
{
    [Fact]
    public async Task RunAsync_PassesGate_ForCompleteSampleRunSet()
    {
        string repoRoot = FindRepoRoot();
        string manifestPath = Path.Combine(repoRoot, "benchmarks", "samples", "agent-eval", "manifest.sample.json");
        string runsPath = Path.Combine(repoRoot, "benchmarks", "samples", "agent-eval", "runs");
        string outputDir = Path.Combine(Path.GetTempPath(), $"agent-eval-gate-{Guid.NewGuid():N}");

        try
        {
            AgentEvalGateRunner gateRunner = new();
            AgentEvalGateReport report = await gateRunner.RunAsync(
                manifestPath,
                runsPath,
                outputDir,
                CancellationToken.None);

            Assert.True(report.manifest_valid);
            Assert.True(report.runs_valid);
            Assert.True(report.sufficient_data);
            Assert.True(report.gate_passed);
            Assert.True(File.Exists(report.output_path));
            Assert.True(File.Exists(report.summary_path));
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
    public async Task RunAsync_FailsGate_WhenDataIsInsufficient()
    {
        string repoRoot = FindRepoRoot();
        string manifestPath = Path.Combine(repoRoot, "benchmarks", "experiments", "oss-csharp-pilot-v1", "manifest.json");
        string runsPath = Path.Combine(Path.GetTempPath(), $"agent-eval-gate-runs-{Guid.NewGuid():N}");
        string outputDir = Path.Combine(Path.GetTempPath(), $"agent-eval-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runsPath);

        try
        {
            AgentEvalGateRunner gateRunner = new();
            AgentEvalGateReport report = await gateRunner.RunAsync(
                manifestPath,
                runsPath,
                outputDir,
                CancellationToken.None);

            Assert.True(report.manifest_valid);
            Assert.True(report.runs_valid);
            Assert.False(report.sufficient_data);
            Assert.False(report.gate_passed);
            Assert.Contains(report.notes, n => n.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }

            if (Directory.Exists(runsPath))
            {
                Directory.Delete(runsPath, recursive: true);
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            string candidate = Path.Combine(cursor.FullName, "RoslynSkill.slnx");
            if (File.Exists(candidate))
            {
                return cursor.FullName;
            }

            cursor = cursor.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test execution directory.");
    }
}
