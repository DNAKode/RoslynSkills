using System.Security.Cryptography;
using System.Text;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Shared repository-root discovery and per-user daemon state path helpers for RoslynSkills commands and CLI daemon routing.
/// Handles both standard Git repositories (.git directory) and Git worktrees/submodules (.git file).
/// </summary>
public static class RepositoryRootDiscovery
{
    public static string NormalizePathRoot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string GetWorkspaceHostStateDirectory()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("ROSLYNSKILLS_WORKSPACE_HOST_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return NormalizePathRoot(overrideDir);
        }

        string? root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "RoslynSkills", "workspace-hosts");
    }

    public static string ComputeWorkspaceStateHash(string repoRoot)
    {
        string normalized = NormalizePathRoot(repoRoot);
        string hashInput = OperatingSystem.IsWindows()
            ? normalized.ToLowerInvariant()
            : normalized;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))
            .ToLowerInvariant()[..16];
    }

    public static string GetWorkspaceDaemonManifestPath(string repoRoot)
        => Path.Combine(GetWorkspaceHostStateDirectory(), $"{ComputeWorkspaceStateHash(repoRoot)}.json");

    public static string GetWorkspaceDaemonLogPath(string repoRoot)
        => Path.Combine(GetWorkspaceHostStateDirectory(), $"{ComputeWorkspaceStateHash(repoRoot)}.log");

    public static string GetWorkspaceAliasesPath(string repoRoot)
        => Path.Combine(GetWorkspaceHostStateDirectory(), $"{ComputeWorkspaceStateHash(repoRoot)}.workspaces.json");

    public static bool IsGitRepositoryRoot(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        string dotGit = Path.Combine(directoryPath, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    public static string? TryFindGitRepositoryRoot(string? startPath)
    {
        string? startDirectory = TryResolveStartDirectory(startPath);
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            if (IsGitRepositoryRoot(current.FullName))
            {
                return NormalizePathRoot(current.FullName);
            }

            current = current.Parent;
        }

        return null;
    }

    public static string? TryFindAncestorWithFile(string? startPath, params string[] relativePathSegments)
    {
        if (relativePathSegments.Length == 0)
        {
            return null;
        }

        string? startDirectory = TryResolveStartDirectory(startPath);
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string candidate = current.FullName;
            for (int i = 0; i < relativePathSegments.Length; i++)
            {
                candidate = Path.Combine(candidate, relativePathSegments[i]);
            }

            if (File.Exists(candidate))
            {
                return NormalizePathRoot(current.FullName);
            }

            current = current.Parent;
        }

        return null;
    }

    public static string ResolveWorkspaceDaemonRoot(string? explicitRepoRoot, string? startPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRepoRoot))
        {
            return NormalizePathRoot(explicitRepoRoot);
        }

        string startDirectory = TryResolveStartDirectory(startPath) ?? NormalizePathRoot(Directory.GetCurrentDirectory());
        return TryFindAncestorWithWorkspaceAliases(startDirectory)
            ?? TryFindGitRepositoryRoot(startDirectory)
            ?? NormalizePathRoot(startDirectory);
    }

    private static string? TryFindAncestorWithWorkspaceAliases(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string normalized = NormalizePathRoot(current.FullName);
            if (File.Exists(GetWorkspaceAliasesPath(normalized)))
            {
                return normalized;
            }

            // Never cross above an enclosing Git repository or worktree boundary: a worktree or nested repo
            // should bind its own daemon instead of inheriting an outer repository's workspace aliases.
            if (IsGitRepositoryRoot(normalized))
            {
                break;
            }

            current = current.Parent;
        }

        return null;
    }

    public static string? TryFindEditClaimRepositoryRoot(string? startPath)
        => TryFindGitRepositoryRoot(startPath)
            ?? TryFindAncestorWithFile(startPath, ".roslynskills", "edit-claims.json");

    public static string ResolveEditClaimRepositoryRoot(string? explicitRepoRoot, string? startPath = null)
    {
        string startDirectory = !string.IsNullOrWhiteSpace(explicitRepoRoot)
            ? TryResolveStartDirectory(explicitRepoRoot) ?? Path.GetFullPath(explicitRepoRoot)
            : TryResolveStartDirectory(startPath) ?? Path.GetFullPath(Directory.GetCurrentDirectory());

        string? discovered = TryFindEditClaimRepositoryRoot(startDirectory);
        if (discovered is not null)
        {
            return discovered;
        }

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            if (Directory.Exists(current.FullName) &&
                (File.Exists(Path.Combine(current.FullName, "RoslynSkills.slnx")) ||
                 Directory.EnumerateFiles(current.FullName, "*.sln*").Any()))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return startDirectory;
    }

    private static string? TryResolveStartDirectory(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(startPath);
        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        if (File.Exists(fullPath))
        {
            return Path.GetDirectoryName(fullPath);
        }

        // For paths that do not exist yet, walk up to the nearest existing ancestor directory.
        string? candidate = Path.HasExtension(fullPath)
            ? Path.GetDirectoryName(fullPath)
            : fullPath;
        while (!string.IsNullOrWhiteSpace(candidate) && !Directory.Exists(candidate))
        {
            string? parent = Path.GetDirectoryName(candidate);
            if (string.Equals(parent, candidate, StringComparison.Ordinal))
            {
                break;
            }

            candidate = parent;
        }

        return !string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)
            ? candidate
            : Path.GetDirectoryName(fullPath);
    }
}
