using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.Benchmark.Tests;

public sealed class ToolThinkingSplitScriptTests
{
    [Fact]
    public void NewToolThinkingSplitExperiment_ContainsControlAndTreatmentLanes()
    {
        string script = ReadScript("New-ToolThinkingSplitExperiment.ps1");

        Assert.Contains("experiment_type = \"tool-thinking-split\"", script, StringComparison.Ordinal);
        Assert.Contains("lane_id = \"control\"", script, StringComparison.Ordinal);
        Assert.Contains("lane_id = \"treatment\"", script, StringComparison.Ordinal);
        Assert.Contains("tool_policy = \"text_only\"", script, StringComparison.Ordinal);
        Assert.Contains("tool_policy = \"roslyn_enabled\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NewToolThinkingSplitExperiment_SupportsGeminiLaneScaffolding()
    {
        string script = ReadScript("New-ToolThinkingSplitExperiment.ps1");

        Assert.Contains("ValidateSet(\"claude\", \"codex\", \"gemini\")", script, StringComparison.Ordinal);
        Assert.Contains("TreatmentGuidanceProfile", script, StringComparison.Ordinal);
        Assert.Contains("ValidateSet(\"standard\", \"tight\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeToolThinkingSplit_ReportsPreEditOverheadSignals()
    {
        string script = ReadScript("Analyze-ToolThinkingSplit.ps1");

        Assert.Contains("events_before_first_edit", script, StringComparison.Ordinal);
        Assert.Contains("roslyn_command_count", script, StringComparison.Ordinal);
        Assert.Contains("discovery_commands_before_first_edit", script, StringComparison.Ordinal);
        Assert.Contains("retry_recoveries", script, StringComparison.Ordinal);
        Assert.Contains("productive_roslyn_command_count", script, StringComparison.Ordinal);
        Assert.Contains("semantic_edit_first_try_success_rate", script, StringComparison.Ordinal);
        Assert.Contains("verify_after_edit_rate", script, StringComparison.Ordinal);
        Assert.Contains("roslyn_used_well_score", script, StringComparison.Ordinal);
        Assert.Contains("schema_version = \"2.0\"", script, StringComparison.Ordinal);
        Assert.Contains("Tool-Thinking Split Summary", script, StringComparison.Ordinal);
        Assert.Contains("Control should have Roslyn commands = 0", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RunToolThinkingSplitExperiment_PerformsTwoLaneExecutionAndAnalysis()
    {
        string script = ReadScript("Run-ToolThinkingSplitExperiment.ps1");

        Assert.Contains("Initialize-LaneWorkspace", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-LaneRun", script, StringComparison.Ordinal);
        Assert.Contains("-LaneId \"control\"", script, StringComparison.Ordinal);
        Assert.Contains("-LaneId \"treatment\"", script, StringComparison.Ordinal);
        Assert.Contains("Analyze-ToolThinkingSplit.ps1", script, StringComparison.Ordinal);
        Assert.Contains("FailOnControlContamination", script, StringComparison.Ordinal);
        Assert.Contains("Resolve-RoscliLauncher", script, StringComparison.Ordinal);
        Assert.Contains("roscli_launcher_path", script, StringComparison.Ordinal);
        Assert.Contains("-TreatmentGuidanceProfile $TreatmentGuidanceProfile", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeToolThinkingSplit_HandlesSingleEventTranscripts()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Analyze-ToolThinkingSplit.ps1");
        string tempRoot = Path.Combine(Path.GetTempPath(), "roslynskills-tool-thinking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string controlTranscript = Path.Combine(tempRoot, "control.jsonl");
            string treatmentTranscript = Path.Combine(tempRoot, "treatment.jsonl");
            string outputJson = Path.Combine(tempRoot, "metrics.json");
            string outputMarkdown = Path.Combine(tempRoot, "summary.md");

            File.WriteAllText(
                controlTranscript,
                """
                {"type":"item.completed","item":{"type":"command_execution","command":"echo hello","aggregated_output":"ok","exit_code":0,"status":"completed"}}
                """.Trim());

            File.WriteAllText(
                treatmentTranscript,
                """
                {"type":"item.completed","item":{"type":"command_execution","command":"scripts\\roscli.cmd list-commands --ids-only","aggregated_output":"{\"Ok\":true,\"CommandId\":\"cli.list_commands\",\"Data\":{}}","exit_code":0,"status":"completed"}}
                """.Trim());

            ProcessStartInfo psi = new()
            {
                FileName = ResolvePowerShellExecutable(),
                Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ControlTranscript \"{controlTranscript}\" -TreatmentTranscript \"{treatmentTranscript}\" -OutputJson \"{outputJson}\" -OutputMarkdown \"{outputMarkdown}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = Process.Start(psi)!;
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(outputJson), $"Expected output json: {outputJson}.");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputJson));
            JsonElement root = document.RootElement;
            Assert.Equal("2.0", root.GetProperty("schema_version").GetString());
            Assert.True(root.GetProperty("control").GetProperty("command_round_trips").GetInt32() >= 0);
            Assert.True(root.GetProperty("treatment").GetProperty("roslyn_command_count").GetInt32() >= 0);
            Assert.True(root.GetProperty("control").TryGetProperty("roslyn_used_well_score", out _));
            Assert.True(root.GetProperty("treatment").TryGetProperty("verify_after_edit_rate", out _));
            Assert.True(root.GetProperty("deltas").TryGetProperty("productive_roslyn_command_count", out _));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup in test environments.
            }
        }
    }

    private static string ReadScript(string fileName)
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", fileName);
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

    private static string ResolvePowerShellExecutable()
    {
        if (IsCommandAvailable("pwsh"))
        {
            return "pwsh";
        }

        if (OperatingSystem.IsWindows() && IsCommandAvailable("powershell"))
        {
            return "powershell";
        }

        return OperatingSystem.IsWindows() ? "powershell" : "pwsh";
    }

    private static bool IsCommandAvailable(string command)
    {
        string locator = OperatingSystem.IsWindows() ? "where" : "which";
        ProcessStartInfo psi = new()
        {
            FileName = locator,
            Arguments = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
