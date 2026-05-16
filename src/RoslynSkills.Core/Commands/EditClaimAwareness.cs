using System.Text.Json;

namespace RoslynSkills.Core.Commands;

internal static class EditClaimAwareness
{
    public static object BuildForFile(string filePath, bool mutationRequested)
    {
        string fullPath = Path.GetFullPath(filePath);
        string? repoRoot = FindRepoRoot(Path.GetDirectoryName(fullPath));
        if (repoRoot is null)
        {
            return new
            {
                mutation_requested = mutationRequested,
                repo_root = (string?)null,
                store_path = (string?)null,
                claimed = false,
                active_claim_count = 0,
                matching_claims = Array.Empty<object>(),
                warning = mutationRequested
                    ? "No repository root was found; run edit.claim from the repo root before mutating shared C# files."
                    : null,
            };
        }

        string relativePath = NormalizeRelativePath(repoRoot, fullPath);
        string storePath = Path.Combine(repoRoot, ".roslynskills", "edit-claims.json");
        ClaimInfo[] activeClaims = ReadActiveClaims(storePath);
        ClaimInfo[] matchingClaims = activeClaims
            .Where(claim => claim.Paths.Any(path => string.Equals(path, relativePath, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return new
        {
            mutation_requested = mutationRequested,
            repo_root = repoRoot,
            store_path = storePath,
            path = relativePath,
            claimed = matchingClaims.Length > 0,
            active_claim_count = activeClaims.Length,
            matching_claims = matchingClaims.Select(claim => new
            {
                claim_id = claim.ClaimId,
                owner = claim.Owner,
                reason = claim.Reason,
                expires_at_utc = claim.ExpiresAtUtc,
            }).ToArray(),
            warning = mutationRequested && matchingClaims.Length == 0
                ? $"No active edit.claim covers '{relativePath}'. Claim the file before further C# mutation, or explain why this single-agent edit is safe."
                : null,
        };
    }

    private static string? FindRepoRoot(string? startDirectory)
    {
        DirectoryInfo? current = string.IsNullOrWhiteSpace(startDirectory)
            ? null
            : new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string NormalizeRelativePath(string repoRoot, string fullPath)
    {
        string relativePath = Path.GetRelativePath(repoRoot, fullPath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static ClaimInfo[] ReadActiveClaims(string storePath)
    {
        if (!File.Exists(storePath))
        {
            return Array.Empty<ClaimInfo>();
        }

        try
        {
            using FileStream stream = File.OpenRead(storePath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("claims", out JsonElement claimsElement) ||
                claimsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ClaimInfo>();
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<ClaimInfo> claims = new();
            foreach (JsonElement claimElement in claimsElement.EnumerateArray())
            {
                string? claimId = GetString(claimElement, "claim_id");
                string? owner = GetString(claimElement, "owner");
                DateTimeOffset? expiresAtUtc = GetDateTimeOffset(claimElement, "expires_at_utc");
                if (string.IsNullOrWhiteSpace(claimId) ||
                    string.IsNullOrWhiteSpace(owner) ||
                    expiresAtUtc is null ||
                    expiresAtUtc <= now)
                {
                    continue;
                }

                string[] paths = GetStringArray(claimElement, "paths")
                    .Select(path => path.Replace('\\', '/'))
                    .ToArray();
                if (paths.Length == 0)
                {
                    continue;
                }

                claims.Add(new ClaimInfo(
                    claimId,
                    owner,
                    GetString(claimElement, "reason"),
                    paths,
                    expiresAtUtc.Value));
            }

            return claims.ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<ClaimInfo>();
        }
        catch (IOException)
        {
            return Array.Empty<ClaimInfo>();
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static string[] GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private sealed record ClaimInfo(
        string ClaimId,
        string Owner,
        string? Reason,
        IReadOnlyList<string> Paths,
        DateTimeOffset ExpiresAtUtc);
}
