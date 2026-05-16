using RoslynSkills.Contracts;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class ChangedFilesCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.changed_files",
        Summary: "List changed files in the current Git worktree, grouped for C#-first orientation without reading file contents.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (input.TryGetProperty("repo_root", out JsonElement repoRoot) &&
            repoRoot.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            errors.Add(new CommandError("invalid_input", "Property 'repo_root' must be a string when provided."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        string requestedRoot = GetOptionalTrimmedString(input, "repo_root") ?? Directory.GetCurrentDirectory();
        string fullRequestedRoot = Path.GetFullPath(requestedRoot);
        if (!Directory.Exists(fullRequestedRoot))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("directory_not_found", $"Directory '{requestedRoot}' does not exist.") });
        }

        (int rootExitCode, string rootStdout, string rootStderr) = await RunGitAsync(
            fullRequestedRoot,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);
        if (rootExitCode != 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("not_git_repository", $"Could not resolve Git root from '{fullRequestedRoot}': {rootStderr.Trim()}") });
        }

        string gitRoot = rootStdout.Trim();
        (int statusExitCode, string statusStdout, string statusStderr) = await RunGitAsync(
            gitRoot,
            ["status", "--porcelain=v1"],
            cancellationToken).ConfigureAwait(false);
        if (statusExitCode != 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("git_status_failed", $"git status failed for '{gitRoot}': {statusStderr.Trim()}") });
        }

        ChangedFile[] files = statusStdout
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseStatusLine)
            .Where(file => file is not null)
            .Select(file => file!)
            .ToArray();

        ChangedFile[] csharpFiles = files.Where(file => file.category == "csharp").ToArray();
        object data = new
        {
            repo_root = gitRoot,
            requested_root = fullRequestedRoot,
            dirty = files.Length > 0,
            total_changed = files.Length,
            csharp_changed = csharpFiles.Length,
            docs_changed = files.Count(file => file.category == "docs"),
            other_changed = files.Count(file => file.category == "other"),
            files,
            csharp_files = csharpFiles,
            suggested_next_steps = BuildSuggestedNextSteps(csharpFiles),
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static ChangedFile? ParseStatusLine(string line)
    {
        if (line.Length < 4)
        {
            return null;
        }

        char indexStatus = line[0];
        char worktreeStatus = line[1];
        string path = line[3..].Trim();
        int renameMarker = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameMarker >= 0)
        {
            path = path[(renameMarker + 4)..].Trim();
        }

        string extension = Path.GetExtension(path);
        string category = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".csx", StringComparison.OrdinalIgnoreCase)
            ? "csharp"
            : extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
              extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase)
                ? "docs"
                : "other";

        return new ChangedFile(
            path,
            indexStatus: indexStatus.ToString(),
            worktreeStatus: worktreeStatus.ToString(),
            category,
            is_csharp: category == "csharp");
    }

    private static string[] BuildSuggestedNextSteps(IReadOnlyList<ChangedFile> csharpFiles)
    {
        if (csharpFiles.Count == 0)
        {
            return
            [
                "No changed C# files found. Use ctx.search_text/nav.find_symbol to choose a new C# slice before editing.",
            ];
        }

        string firstPath = csharpFiles[0].path;
        return
        [
            $"roscli ctx.file_outline {firstPath} --max-members 40",
            $"roscli edit.claim claim {firstPath} --reason <slice-name>",
            "Use ctx.member_source for focused context before any C# edit; avoid git diff for .cs content reads.",
        ];
    }

    private static string? GetOptionalTrimmedString(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string value = (property.GetString() ?? string.Empty).Trim();
        return value.Length == 0 ? null : value;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private sealed record ChangedFile(
        string path,
        string indexStatus,
        string worktreeStatus,
        string category,
        bool is_csharp);
}
