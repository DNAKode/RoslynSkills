using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Detects MSBuild workspaces that loaded with zero metadata references (the classic
/// "project was never restored" failure mode: compilation parses but every semantic
/// query degrades to error types) and repairs them by running <c>dotnet restore</c>.
/// Without this, semantic commands silently return empty results on fresh checkouts.
/// </summary>
internal static class WorkspaceAutoRestore
{
    private const int RestoreTimeoutSeconds = 120;
    private const int DrainGraceSeconds = 10;
    private const int FailureDetailMaxLength = 400;

    public static bool IsEnabled
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("ROSLYNSKILLS_AUTO_RESTORE");
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return !value.Equals("false", StringComparison.OrdinalIgnoreCase)
                && value != "0";
        }
    }

    /// <summary>
    /// True when any project in the solution needs NuGet restore (either it has source
    /// documents with zero metadata references, or it depends on NuGet packages while
    /// <c>project.assets.json</c> is missing).
    /// </summary>
    public static bool NeedsRestore(Solution solution)
        => GetProjectsNeedingRestore(solution).Count > 0;

    public static IReadOnlyList<UnresolvedProjectInfo> GetProjectsNeedingRestore(Solution solution)
        => WorkspaceProjectResolution.InspectSolution(solution)
            .Where(project => project.needs_restore)
            .ToArray();

    /// <summary>
    /// Runs <c>dotnet restore</c> for the workspace path. Returns true only when the
    /// restore completed successfully and the caller should reload the workspace.
    /// Appends an "auto_restore:" diagnostic describing the outcome either way.
    /// </summary>
    public static async Task<bool> TryRestoreAsync(
        string workspacePath,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken,
        IReadOnlyList<UnresolvedProjectInfo>? projectsNeedingRestore = null)
    {
        if (!File.Exists(workspacePath))
        {
            diagnostics.Add($"auto_restore skipped: workspace path '{workspacePath}' is not a restorable project or solution file.");
            return false;
        }

        string projectDetail = projectsNeedingRestore is { Count: > 0 }
            ? $"{projectsNeedingRestore.Count} unresolved project(s) ({string.Join(", ", projectsNeedingRestore.Take(5).Select(p => p.project_name))}{(projectsNeedingRestore.Count > 5 ? ", ..." : string.Empty)})"
            : "missing restore assets or metadata references";
        diagnostics.Add(
            $"auto_restore: workspace '{workspacePath}' loaded with {projectDetail}; running 'dotnet restore' before reload.");

        string workingDirectory = Path.GetDirectoryName(workspacePath) ?? Environment.CurrentDirectory;
        ProcessStartInfo startInfo = new("dotnet", $"restore \"{workspacePath}\" --nologo -v q --disable-build-servers")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Prevent MSBuild node-reuse workers from outliving the restore while holding
        // our redirected output handles open, and clear MSBuildLocator-injected vars so
        // the child dotnet CLI resolves its own SDK cleanly.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment.Remove("MSBUILD_EXE_PATH");
        startInfo.Environment.Remove("MSBuildSDKsPath");
        startInfo.Environment.Remove("MSBuildExtensionsPath");

        System.Text.StringBuilder stdoutBuilder = new();
        System.Text.StringBuilder stderrBuilder = new();
        TaskCompletionSource<bool> stdoutDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> stderrDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using Process process = new();
        process.StartInfo = startInfo;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult(true);
            }
            else
            {
                lock (stdoutBuilder)
                {
                    stdoutBuilder.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.TrySetResult(true);
            }
            else
            {
                lock (stderrBuilder)
                {
                    stderrBuilder.AppendLine(e.Data);
                }
            }
        };

        try
        {
            if (!process.Start())
            {
                diagnostics.Add("auto_restore failed: could not start 'dotnet'.");
                return false;
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"auto_restore failed: could not start 'dotnet': {ex.Message}");
            return false;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(RestoreTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            diagnostics.Add($"auto_restore failed: 'dotnet restore' exceeded {RestoreTimeoutSeconds}s and was terminated.");
            return false;
        }

        // Drain remaining output. MSBuild worker children can inherit the redirected
        // handles and outlive the restore process, so bound this drain instead of
        // waiting for pipe EOF indefinitely.
        using (CancellationTokenSource drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            drainCts.CancelAfter(TimeSpan.FromSeconds(DrainGraceSeconds));
            try
            {
                await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                diagnostics.Add("auto_restore warning: restore output drains incomplete; continuing.");
            }
        }

        if (process.ExitCode == 0)
        {
            return true;
        }

        string stderrText;
        string stdoutText;
        lock (stderrBuilder)
        {
            stderrText = stderrBuilder.ToString();
        }

        lock (stdoutBuilder)
        {
            stdoutText = stdoutBuilder.ToString();
        }

        string combined = string.IsNullOrWhiteSpace(stderrText)
            ? stdoutText
            : string.IsNullOrWhiteSpace(stdoutText)
                ? stderrText
                : $"{stderrText.Trim()}{Environment.NewLine}{stdoutText.Trim()}";
        string detail = TruncateTail(combined, FailureDetailMaxLength);
        diagnostics.Add($"auto_restore failed with exit code {process.ExitCode}: {detail}");
        return false;
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited; nothing further to clean up.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort reap; ignore.
        }
    }

    private static string TruncateTail(string text, int maxLength)
    {
        string trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return trimmed[^maxLength..];
    }
}
