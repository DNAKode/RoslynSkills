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

        using Process process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"PowerShell parse failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    [Fact]
    public void BenchmarkRoscliVsRgQueries_TreatsRgNoMatchesAsSuccessfulSample()
    {
        string script = ReadScript("Benchmark-RoscliVsRgQueries.ps1");

        Assert.Contains("$rgSucceeded = $rgRun.exit_code -eq 0 -or $rgRun.exit_code -eq 1", script, StringComparison.Ordinal);
        Assert.Contains("succeeded = $rgSucceeded", script, StringComparison.Ordinal);
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
