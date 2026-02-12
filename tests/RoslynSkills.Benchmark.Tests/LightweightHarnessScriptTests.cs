namespace RoslynSkills.Benchmark.Tests;

public sealed class LightweightHarnessScriptTests
{
    [Fact]
    public void LightweightHarness_CodexReasoningEffort_IsWired()
    {
        string script = ReadScript();

        Assert.Contains("[string]$CodexReasoningEffort", script, StringComparison.Ordinal);
        Assert.Contains("model_reasoning_effort", script, StringComparison.Ordinal);
    }

    private static string ReadScript()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Run-LightweightUtilityGameRealRuns.ps1");
        return File.ReadAllText(scriptPath);
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