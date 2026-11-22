using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.Benchmark.Tests;

public sealed class RoscliVsRgScriptTests
{
    [Fact]
    public void BenchmarkRoscliVsRgQueries_IncludesRoscliSingleBatchAndRgModes()
    {
        string script = ReadScript("Benchmark-RoscliVsRgQueries.ps1");

        Assert.Contains("roscli_single", script, StringComparison.Ordinal);
        Assert.Contains("roscli_batch", script, StringComparison.Ordinal);
        Assert.Contains("\"rg\"", script, StringComparison.Ordinal);
        Assert.Contains("nav.find_symbol_batch", script, StringComparison.Ordinal);
        Assert.Contains("workspace_cache_hit", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkRoscliVsRgQueries_IsValidPowerShell()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Benchmark-RoscliVsRgQueries.ps1");

        ProcessStartInfo psi = new()
        {
            FileName = ResolvePowerShellExecutable(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseFile($env:ROSLYNSKILLS_SCRIPT_UNDER_TEST, [ref]$tokens, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }");
        psi.Environment["ROSLYNSKILLS_SCRIPT_UNDER_TEST"] = scriptPath;

        (int exitCode, string stdout, string stderr) = RunProcessWithTimeout(psi, timeoutMs: 30_000);
        Assert.True(exitCode == 0, $"PowerShell parse failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    [Fact]
    public void BenchmarkRoscliVsRgQueries_TreatsRgNoMatchesAsSuccessfulSample()
    {
        string script = ReadScript("Benchmark-RoscliVsRgQueries.ps1");

        Assert.Contains("$rgSucceeded = $rgRun.exit_code -eq 0 -or $rgRun.exit_code -eq 1", script, StringComparison.Ordinal);
        Assert.Contains("succeeded = $rgSucceeded", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkHotWorkspaceHost_IncludesCorrectnessGateFields()
    {
        string script = ReadScript("Benchmark-HotWorkspaceHost.ps1");

        Assert.Contains("daemon_hot_workspace", script, StringComparison.Ordinal);
        Assert.Contains("direct_workspace", script, StringComparison.Ordinal);
        Assert.Contains("hot_uses_workspace_handle", script, StringComparison.Ordinal);
        Assert.Contains("hot_uses_process_hot_cache", script, StringComparison.Ordinal);
        Assert.Contains("hot_no_ad_hoc_fallback", script, StringComparison.Ordinal);
        Assert.Contains("workspace.refresh", script, StringComparison.Ordinal);
        Assert.Contains("--mode", script, StringComparison.Ordinal);
        Assert.Contains("strict", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkHotWorkspaceHost_IsValidPowerShell()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "benchmarks", "scripts", "Benchmark-HotWorkspaceHost.ps1");

        ProcessStartInfo psi = new()
        {
            FileName = ResolvePowerShellExecutable(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseFile($env:ROSLYNSKILLS_SCRIPT_UNDER_TEST, [ref]$tokens, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }");
        psi.Environment["ROSLYNSKILLS_SCRIPT_UNDER_TEST"] = scriptPath;

        (int exitCode, string stdout, string stderr) = RunProcessWithTimeout(psi, timeoutMs: 30_000);
        Assert.True(exitCode == 0, $"PowerShell parse failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    [Fact]
    public void AimsScenario_AnchorsTheKnownMemberLookupQueries()
    {
        string scenario = ReadScenario("roscli-vs-rg-aims-symbol-queries.json");

        Assert.Contains("Source/Aims.sln", scenario, StringComparison.Ordinal);
        Assert.Contains("UpdateContentAt", scenario, StringComparison.Ordinal);
        Assert.Contains("AuditCompletionStoreContentViewModel", scenario, StringComparison.Ordinal);
        Assert.Contains("UpdateCompletionsFirstTime", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void AimsScenario_IsValidJsonWithRequiredQueryShape()
    {
        string scenario = ReadScenario("roscli-vs-rg-aims-symbol-queries.json");

        using JsonDocument document = JsonDocument.Parse(scenario);
        JsonElement root = document.RootElement;
        Assert.Equal("Source/Aims.sln", root.GetProperty("workspace_path").GetString());
        JsonElement queries = root.GetProperty("queries");
        Assert.Equal(JsonValueKind.Array, queries.ValueKind);
        Assert.True(queries.GetArrayLength() > 0);

        foreach (JsonElement query in queries.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(query.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(query.GetProperty("file_path").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(query.GetProperty("symbol_name").GetString()));
        }
    }

    private static string ReadScript(string fileName)
    {
        string repoRoot = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repoRoot, "benchmarks", "scripts", fileName));
    }

    private static string ReadScenario(string fileName)
    {
        string repoRoot = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repoRoot, "benchmarks", "scenarios", fileName));
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
            (int exitCode, _, _) = RunProcessWithTimeout(psi, timeoutMs: 10_000);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcessWithTimeout(ProcessStartInfo psi, int timeoutMs)
    {
        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{psi.FileName}'.");
        try
        {
            Task<string> stdoutTask = psi.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
            Task<string> stderrTask = psi.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
            if (!process.WaitForExit(timeoutMs))
            {
                TryKillProcessTree(process);
                throw new TimeoutException($"Process '{psi.FileName}' timed out after {timeoutMs}ms.");
            }

            string stdout = stdoutTask.Wait(TimeSpan.FromSeconds(5)) ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.Wait(TimeSpan.FromSeconds(5)) ? stderrTask.Result : string.Empty;
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            TryKillProcessTree(process);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
