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
    /// True when any project in the solution has source documents but no metadata
    /// references at all, which indicates NuGet restore assets were absent at load time.
    /// </summary>
    public static bool NeedsRestore(Solution solution)
    {
        foreach (Project project in solution.Projects)
        {
            bool hasDocuments = false;
            foreach (Document document in project.Documents)
            {
                hasDocuments = true;
                break;
            }

            if (hasDocuments && project.MetadataReferences.Count == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs <c>dotnet restore</c> for the workspace path. Returns true only when the
    /// restore completed successfully and the caller should reload the workspace.
    /// Appends an "auto_restore:" diagnostic describing the outcome either way.
    /// </summary>
    public static async Task<bool> TryRestoreAsync(
        string workspacePath,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(workspacePath))
        {
            diagnostics.Add($"auto_restore skipped: workspace path '{workspacePath}' is not a restorable project or solution file.");
            return false;
        }

        diagnostics.Add(
            $"auto_restore: workspace '{workspacePath}' loaded with zero metadata references; running 'dotnet restore' before reload.");

        ProcessStartInfo startInfo = new("dotnet", $"restore \"{workspacePath}\" --nologo -v q")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Prevent MSBuild node-reuse workers from outliving the restore while holding
        // our redirected output handles open.
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";

        using Process process = new();
        process.StartInfo = startInfo;
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

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

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
        string stdout;
        string stderr;
        using (CancellationTokenSource drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            drainCts.CancelAfter(TimeSpan.FromSeconds(DrainGraceSeconds));
            try
            {
                stdout = await stdoutTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
                stderr = await stderrTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                diagnostics.Add("auto_restore warning: restore output drains incomplete; continuing.");
                stdout = string.Empty;
                stderr = string.Empty;
            }
        }

        if (process.ExitCode == 0)
        {
            return true;
        }

        string detail = TruncateTail(stderr, FailureDetailMaxLength);
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
