namespace RoslynSkills.Core.Commands;

/// <summary>
/// Shared repository-root discovery for RoslynSkills commands and CLI daemon routing.
/// Handles both standard Git repositories (.git directory) and Git worktrees/submodules (.git file).
/// </summary>
public static class RepositoryRootDiscovery
{
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
                return current.FullName;
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
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    public static string ResolveWorkspaceDaemonRoot(string? explicitRepoRoot, string? startPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRepoRoot))
        {
            return Path.GetFullPath(explicitRepoRoot);
        }

        string startDirectory = TryResolveStartDirectory(startPath) ?? Path.GetFullPath(Directory.GetCurrentDirectory());
        return TryFindAncestorWithFile(startDirectory, ".roslynskills", "workspaces.json")
            ?? TryFindGitRepositoryRoot(startDirectory)
            ?? startDirectory;
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
