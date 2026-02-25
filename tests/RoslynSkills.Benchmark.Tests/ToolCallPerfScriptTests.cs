namespace RoslynSkills.Benchmark.Tests;

public sealed class ToolCallPerfScriptTests
{
    [Fact]
    public void BenchmarkToolCallPerf_IncludesRoscliAndXmlcliProfiles()
    {
        string script = ReadScript("Benchmark-ToolCallPerf.ps1");

        Assert.Contains("roscli", script, StringComparison.Ordinal);
        Assert.Contains("xmlcli", script, StringComparison.Ordinal);
        Assert.Contains("published_cached_stale_off", script, StringComparison.Ordinal);
        Assert.Contains("published_prewarmed_stale_off", script, StringComparison.Ordinal);
        Assert.Contains("IncludeStaleCheckOnProfiles", script, StringComparison.Ordinal);
        Assert.Contains("IncludeDotnetRunNoBuildProfiles", script, StringComparison.Ordinal);
        Assert.Contains("IncludeJitSensitivityProfiles", script, StringComparison.Ordinal);
        Assert.Contains("IncludeBootstrapCi", script, StringComparison.Ordinal);
        Assert.Contains("BootstrapResamples", script, StringComparison.Ordinal);
        Assert.Contains("IncludeRoscliTransportProfile", script, StringComparison.Ordinal);
        Assert.Contains("transport_persistent_server", script, StringComparison.Ordinal);
        Assert.Contains("jit_forced", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkToolCallPerf_CapturesEnvelopeTelemetryAndOverhead()
    {
        string script = ReadScript("Benchmark-ToolCallPerf.ps1");

        Assert.Contains("telemetry_total_ms", script, StringComparison.Ordinal);
        Assert.Contains("startup_overhead_ms", script, StringComparison.Ordinal);
        Assert.Contains("binary_launch_mode", script, StringComparison.Ordinal);
        Assert.Contains("command_parse_ms", script, StringComparison.Ordinal);
        Assert.Contains("workspace_load_ms", script, StringComparison.Ordinal);
        Assert.Contains("parse_cache_mode", script, StringComparison.Ordinal);
        Assert.Contains("best_profiles", script, StringComparison.Ordinal);
        Assert.Contains("baseline_deltas", script, StringComparison.Ordinal);
        Assert.Contains("request_chars_avg", script, StringComparison.Ordinal);
        Assert.Contains("first_measure_wall_ms", script, StringComparison.Ordinal);
        Assert.Contains("steady_wall_ms_avg", script, StringComparison.Ordinal);
        Assert.Contains("wall_ms_ci95_low", script, StringComparison.Ordinal);
        Assert.Contains("wall_ms_bootstrap_ci95_low", script, StringComparison.Ordinal);
        Assert.Contains("wall_ms_ratio_bootstrap_ci95_low", script, StringComparison.Ordinal);
        Assert.Contains("confidence_method", script, StringComparison.Ordinal);
        Assert.Contains("cold_warm_summary", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkToolCallPerf_SupportsCommandFilteringAndBaselineDeltaReport()
    {
        string script = ReadScript("Benchmark-ToolCallPerf.ps1");

        Assert.Contains("IncludeCommands", script, StringComparison.Ordinal);
        Assert.Contains("ExcludeCommands", script, StringComparison.Ordinal);
        Assert.Contains("Normalize-CommandPatterns", script, StringComparison.Ordinal);
        Assert.Contains("Select-CommandsForTool", script, StringComparison.Ordinal);
        Assert.Contains("## Baseline Deltas (vs dotnet_run)", script, StringComparison.Ordinal);
        Assert.Contains("## Cold vs Steady Summary", script, StringComparison.Ordinal);
        Assert.Contains("Steady Bootstrap CI95", script, StringComparison.Ordinal);
        Assert.Contains("Wall Ratio Bootstrap CI95", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RoscliWrappers_SupportNoBuildAndRunConfigurationFlags()
    {
        string bash = ReadRepoFile("scripts/roscli");
        string batch = ReadRepoFile("scripts/roscli.cmd");

        Assert.Contains("ROSCLI_DOTNET_RUN_NO_BUILD", bash, StringComparison.Ordinal);
        Assert.Contains("ROSCLI_DOTNET_RUN_CONFIGURATION", bash, StringComparison.Ordinal);
        Assert.Contains("--no-build", bash, StringComparison.Ordinal);

        Assert.Contains("ROSCLI_DOTNET_RUN_NO_BUILD", batch, StringComparison.Ordinal);
        Assert.Contains("ROSCLI_DOTNET_RUN_CONFIGURATION", batch, StringComparison.Ordinal);
        Assert.Contains("--no-build", batch, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlcliWrappers_SupportNoBuildAndRunConfigurationFlags()
    {
        string bash = ReadRepoFile("scripts/xmlcli");
        string batch = ReadRepoFile("scripts/xmlcli.cmd");

        Assert.Contains("XMLCLI_DOTNET_RUN_NO_BUILD", bash, StringComparison.Ordinal);
        Assert.Contains("XMLCLI_DOTNET_RUN_CONFIGURATION", bash, StringComparison.Ordinal);
        Assert.Contains("--no-build", bash, StringComparison.Ordinal);

        Assert.Contains("XMLCLI_DOTNET_RUN_NO_BUILD", batch, StringComparison.Ordinal);
        Assert.Contains("XMLCLI_DOTNET_RUN_CONFIGURATION", batch, StringComparison.Ordinal);
        Assert.Contains("--no-build", batch, StringComparison.Ordinal);
    }

    private static string ReadScript(string fileName)
    {
        string repoRoot = FindRepoRoot();
        string path = Path.Combine(repoRoot, "benchmarks", "scripts", fileName);
        return File.ReadAllText(path);
    }

    private static string ReadRepoFile(string relativePath)
    {
        string repoRoot = FindRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(path);
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
