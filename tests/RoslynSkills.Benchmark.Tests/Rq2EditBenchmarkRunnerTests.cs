using RoslynSkills.Benchmark.Rq2;

namespace RoslynSkills.Benchmark.Tests;

public sealed class Rq2EditBenchmarkRunnerTests
{
    [Fact]
    public async Task RunAsync_ProducesReportWithStructuredEditAdvantage()
    {
        string repoRoot = FindRepoRoot();
        string scenarioPath = Path.Combine(repoRoot, "benchmarks", "scenarios", "rq2-edit-rename-structured-vs-text.json");
        string outputDir = Path.Combine(Path.GetTempPath(), $"roslyn-agent-rq2-{Guid.NewGuid():N}");

        try
        {
            Rq2EditBenchmarkRunner runner = new();
            Rq2EditBenchmarkReport report = await runner.RunAsync(scenarioPath, outputDir, CancellationToken.None);

            Assert.Equal(2, report.Summary.scenario_count);
            Assert.True(report.Summary.structured_accuracy > report.Summary.text_accuracy);
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

