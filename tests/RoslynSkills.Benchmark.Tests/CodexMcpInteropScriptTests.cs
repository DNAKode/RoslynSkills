namespace RoslynSkills.Benchmark.Tests;

public sealed class CodexMcpInteropScriptTests
{
    [Fact]
    public void LspCommandDiscovery_IncludesMcpBridgeCandidates()
    {
        string script = ReadScript();

        Assert.Contains("\"cclsp\"", script, StringComparison.Ordinal);
        Assert.Contains("\"mcp-lsp\"", script, StringComparison.Ordinal);
        Assert.Contains("csharp-lsp-mcp.cmd", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CclspAdapterPath_BootstrapsRunLocalConfig()
    {
        string script = ReadScript();

        Assert.Contains("function Write-CclspConfig", script, StringComparison.Ordinal);
        Assert.Contains("cclsp.json", script, StringComparison.Ordinal);
        Assert.Contains("command = @(\"csharp-ls\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexRuns_ConfigureReasoningEffortPerLane()
    {
        string script = ReadScript();

        Assert.Contains("model_reasoning_effort", script, StringComparison.Ordinal);
        Assert.Contains("[string[]]$ReasoningEfforts", script, StringComparison.Ordinal);
        Assert.Contains("duration_seconds", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LspScenarioSkips_AreExplicitForMissingOrInvalidCommand()
    {
        string script = ReadScript();

        Assert.Contains("No LSP MCP command configured/found", script, StringComparison.Ordinal);
        Assert.Contains("Configured LSP MCP command is not resolvable", script, StringComparison.Ordinal);
    }

    private static string ReadScript()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Run-CodexMcpInteropExperiments.ps1");
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
