namespace RoslynSkills.Benchmark.Tests;

public sealed class PairedRunHarnessScriptTests
{
    [Fact]
    public void ProjectTaskShapeTemplate_ExcludesTargetOriginalFromCompilation()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Run-PairedAgentRuns.ps1");

        string script = File.ReadAllText(scriptPath);

        Assert.Contains("<Compile Remove=\"Target.original.cs\" />", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceTelemetryParser_UsesJsonAwareExtraction()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Run-PairedAgentRuns.ps1");

        string script = File.ReadAllText(scriptPath);

        Assert.Contains("function Add-WorkspaceModeFromEnvelope", script, StringComparison.Ordinal);
        Assert.Contains("function Add-WorkspaceModesFromObject", script, StringComparison.Ordinal);
        Assert.Contains("function Add-WorkspaceModesFromText", script, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json -ErrorAction Stop", script, StringComparison.Ordinal);
        Assert.Contains("workspace_context", script, StringComparison.Ordinal);
        Assert.Contains("Add-WorkspaceModesFromText -Text $aggregatedOutput -Modes $modes", script, StringComparison.Ordinal);
        Assert.Contains("Add-WorkspaceModesFromText -Text $content -Modes $modes", script, StringComparison.Ordinal);
        Assert.Contains("Get-RoslynWorkspaceContextUsage", script, StringComparison.Ordinal);
    }

    [Fact]
    public void McpPrompting_PrefersRenameThenDiagnosticsWhenCoordinatesAreKnown()
    {
        string script = ReadScript();

        Assert.Contains("If a precise line/column is provided, do not run pre-rename nav lookups.", script, StringComparison.Ordinal);
        Assert.Contains("skip nav.find_symbol prechecks when line/column is already provided", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceGuardrail_IncludesRequireWorkspaceFailClosedHints()
    {
        string script = ReadScript();

        Assert.Contains("--require-workspace true", script, StringComparison.Ordinal);
        Assert.Contains("workspace_path=TargetHarness.csproj&require_workspace=true", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RoslynHelperScripts_IncludeProjectWorkspaceArgsForNavAndDiagnostics()
    {
        string script = ReadScript();

        Assert.Contains("if (Test-Path \".\\TargetHarness.csproj\")", script, StringComparison.Ordinal);
        Assert.Contains("--workspace-path\", \"TargetHarness.csproj\", \"--require-workspace\", \"true\"", script, StringComparison.Ordinal);
        Assert.Contains("diag.get_file_diagnostics", script, StringComparison.Ordinal);
        Assert.Contains("function Get-WorkspaceContext", script, StringComparison.Ordinal);
        Assert.Contains("workspace_context = `$workspaceContextForTelemetry", script, StringComparison.Ordinal);
    }

    private static string ReadScript()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Run-PairedAgentRuns.ps1");
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
