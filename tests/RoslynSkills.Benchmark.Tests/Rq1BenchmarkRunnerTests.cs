using RoslynSkills.Benchmark.Rq1;

namespace RoslynSkills.Benchmark.Tests;

public sealed class Rq1BenchmarkRunnerTests
{
    [Fact]
    public async Task RunAsync_ProducesReportWithStructuredAdvantage()
    {
        string repoRoot = FindRepoRoot();
        string scenarioPath = Path.Combine(repoRoot, "benchmarks", "scenarios", "rq1-structured-vs-grep.json");
        string outputDir = Path.Combine(Path.GetTempPath(), $"roslyn-agent-rq1-{Guid.NewGuid():N}");

        try
        {
            Rq1BenchmarkRunner runner = new();
            Rq1BenchmarkReport report = await runner.RunAsync(scenarioPath, outputDir, CancellationToken.None);

            Assert.Equal(3, report.Summary.scenario_count);
            Assert.True(report.Summary.structured_accuracy > report.Summary.grep_accuracy);
            Assert.True(File.Exists(report.ArtifactPath));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            string candidate = Path.Combine(cursor.FullName, "RoslynSkills.slnx");
            if (File.Exists(candidate))
            {
                return cursor.FullName;
            }

            cursor = cursor.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test execution directory.");
    }
}

