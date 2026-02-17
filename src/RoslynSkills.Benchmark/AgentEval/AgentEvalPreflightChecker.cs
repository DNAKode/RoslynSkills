using System.Diagnostics;

namespace RoslynSkills.Benchmark.AgentEval;

public sealed class AgentEvalPreflightChecker
{
    private static readonly ProbeSpec[] ProbeSpecs =
    {
        new("dotnet", "--version", Required: true),
        new("git", "--version", Required: true),
        new("rg", "--version", Required: true),
        new("codex", "--version", Required: false, WindowsFallbackCommands: ["codex.cmd", "codex.exe"]),
        new("claude", "--version", Required: false, WindowsFallbackCommands: ["claude.cmd", "claude.exe"]),
        new("gemini", "--version", Required: false, WindowsFallbackCommands: ["gemini.cmd", "gemini.exe"]),
    };

    private readonly ICommandProbe _probe;

    public AgentEvalPreflightChecker()
        : this(new ProcessCommandProbe())
    {
    }

    internal AgentEvalPreflightChecker(ICommandProbe probe)
    {
        _probe = probe;
    }

    public async Task<AgentEvalPreflightReport> RunAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        List<AgentEvalPreflightItem> items = new();
        foreach (ProbeSpec spec in ProbeSpecs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeResult result = await ProbeWithFallbackAsync(spec, cancellationToken).ConfigureAwait(false);
            items.Add(new AgentEvalPreflightItem(
                command: spec.Command,
                required: spec.Required,
                available: result.Available,
                exit_code: result.ExitCode,
                stdout: result.Stdout,
                stderr: result.Stderr));
        }

        bool allRequiredAvailable = items
            .Where(i => i.required)
            .All(i => i.available);

        string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "agent-eval-preflight.json"));
        AgentEvalPreflightReport report = new(
            generated_utc: DateTimeOffset.UtcNow,
            all_required_available: allRequiredAvailable,
            checks: items,
            output_path: outputPath);

        AgentEvalStorage.WriteJson(outputPath, report);
        return report;
    }

    private async Task<ProbeResult> ProbeWithFallbackAsync(ProbeSpec spec, CancellationToken cancellationToken)
    {
        ProbeResult? lastResult = null;
        foreach (string command in EnumerateProbeCommands(spec))
        {
            ProbeResult result = await _probe.ProbeAsync(command, spec.Arguments, cancellationToken).ConfigureAwait(false);
            if (result.Available)
            {
                return result;
            }

            lastResult = result;
        }

        return lastResult ?? new ProbeResult(
            Available: false,
            ExitCode: null,
            Stdout: string.Empty,
            Stderr: $"No probe commands configured for '{spec.Command}'.");
    }

    private static IEnumerable<string> EnumerateProbeCommands(ProbeSpec spec)
    {
        yield return spec.Command;

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        if (spec.WindowsFallbackCommands is null)
        {
            yield break;
        }

        foreach (string fallback in spec.WindowsFallbackCommands)
        {
            if (!string.IsNullOrWhiteSpace(fallback) &&
                !string.Equals(fallback, spec.Command, StringComparison.OrdinalIgnoreCase))
            {
                yield return fallback;
            }
        }
    }
}

internal interface ICommandProbe
{
    Task<ProbeResult> ProbeAsync(string command, string arguments, CancellationToken cancellationToken);
}

internal sealed class ProcessCommandProbe : ICommandProbe
{
    public async Task<ProbeResult> ProbeAsync(string command, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            string stderr = (await stderrTask.ConfigureAwait(false)).Trim();

            return new ProbeResult(
                Available: process.ExitCode == 0,
                ExitCode: process.ExitCode,
                Stdout: stdout,
                Stderr: stderr);
        }
        catch (Exception ex)
        {
            return new ProbeResult(
                Available: false,
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: ex.Message);
        }
    }
}

internal sealed record ProbeSpec(
    string Command,
    string Arguments,
    bool Required,
    IReadOnlyList<string>? WindowsFallbackCommands = null);

internal sealed record ProbeResult(
    bool Available,
    int? ExitCode,
    string Stdout,
    string Stderr);

public sealed record AgentEvalPreflightItem(
    string command,
    bool required,
    bool available,
    int? exit_code,
    string stdout,
    string stderr);

public sealed record AgentEvalPreflightReport(
    DateTimeOffset generated_utc,
    bool all_required_available,
    IReadOnlyList<AgentEvalPreflightItem> checks,
    string output_path);

