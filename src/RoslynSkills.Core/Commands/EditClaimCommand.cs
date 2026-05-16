using RoslynSkills.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSkills.Core.Commands;

public sealed class EditClaimCommand : IAgentCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.claim",
        Summary: "Coordinate multi-agent edit ownership using repo-local file claims with TTL and conflict reporting.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "operation", errors, out string operation);
        if (!string.IsNullOrWhiteSpace(operation) &&
            !IsSupportedOperation(operation))
        {
            errors.Add(new CommandError(
                "invalid_input",
                "Property 'operation' must be one of status, claim, or release."));
        }

        if (input.TryGetProperty("paths", out JsonElement paths) &&
            paths.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", "Property 'paths' must be an array when provided."));
        }

        InputParsing.ValidateOptionalInt(input, "ttl_minutes", errors, minValue: 1, maxValue: 1440);
        InputParsing.ValidateOptionalBool(input, "force", errors);
        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "operation", errors, out string operation))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        string repoRoot = ResolveRepoRoot(GetOptionalString(input, "repo_root"));
        string owner = GetOptionalString(input, "owner") ?? Environment.UserName;
        string? reason = GetOptionalString(input, "reason");
        string? claimId = GetOptionalString(input, "claim_id");
        bool force = InputParsing.GetOptionalBool(input, "force", defaultValue: false);
        int ttlMinutes = InputParsing.GetOptionalInt(input, "ttl_minutes", defaultValue: 90, minValue: 1, maxValue: 1440);
        string[] paths = InputParsing.GetOptionalStringArray(input, "paths")
            .Select(path => NormalizeClaimPath(repoRoot, path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string storePath = GetStorePath(repoRoot);
        EditClaimStore store = ReadStore(storePath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        EditClaimRecord[] expired = store.Claims
            .Where(claim => claim.ExpiresAtUtc <= now)
            .ToArray();
        List<EditClaimRecord> activeClaims = store.Claims
            .Where(claim => claim.ExpiresAtUtc > now)
            .ToList();

        object data = operation.ToLowerInvariant() switch
        {
            "status" => BuildStatus(repoRoot, storePath, activeClaims, expired, paths),
            "claim" => Claim(storePath, activeClaims, expired, paths, owner, reason, ttlMinutes, force, now),
            "release" => Release(storePath, activeClaims, expired, paths, owner, claimId, force),
            _ => new { },
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }

    private static object BuildStatus(
        string repoRoot,
        string storePath,
        List<EditClaimRecord> activeClaims,
        EditClaimRecord[] expired,
        string[] paths)
    {
        EditClaimRecord[] visible = paths.Length == 0
            ? activeClaims.ToArray()
            : activeClaims
                .Where(claim => claim.Paths.Any(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                .ToArray();

        return new
        {
            repo_root = repoRoot,
            store_path = storePath,
            active_count = visible.Length,
            expired_removed_count = expired.Length,
            claims = visible,
        };
    }

    private static object Claim(
        string storePath,
        List<EditClaimRecord> activeClaims,
        EditClaimRecord[] expired,
        string[] paths,
        string owner,
        string? reason,
        int ttlMinutes,
        bool force,
        DateTimeOffset now)
    {
        if (paths.Length == 0)
        {
            return new
            {
                ok = false,
                code = "paths_required",
                message = "Claim operation requires at least one path.",
                expired_removed_count = expired.Length,
            };
        }

        EditClaimRecord[] conflicts = activeClaims
            .Where(claim => claim.Paths.Any(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase)))
            .Where(claim => !string.Equals(claim.Owner, owner, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (conflicts.Length > 0 && !force)
        {
            return new
            {
                ok = false,
                code = "claim_conflict",
                message = "One or more paths are already claimed. Use force=true only after human/operator decision.",
                conflicts,
                expired_removed_count = expired.Length,
            };
        }

        if (force && conflicts.Length > 0)
        {
            activeClaims.RemoveAll(claim => conflicts.Any(conflict => string.Equals(conflict.ClaimId, claim.ClaimId, StringComparison.Ordinal)));
        }

        activeClaims.RemoveAll(claim =>
            string.Equals(claim.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            claim.Paths.Any(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase)));

        EditClaimRecord record = new(
            ClaimId: $"claim_{Guid.NewGuid():N}",
            Owner: owner,
            Reason: reason,
            Paths: paths,
            CreatedAtUtc: now,
            ExpiresAtUtc: now.AddMinutes(ttlMinutes));
        activeClaims.Add(record);
        WriteStore(storePath, activeClaims);
        return new
        {
            ok = true,
            claimed = record,
            replaced_conflicts = force ? conflicts : Array.Empty<EditClaimRecord>(),
            expired_removed_count = expired.Length,
        };
    }

    private static object Release(
        string storePath,
        List<EditClaimRecord> activeClaims,
        EditClaimRecord[] expired,
        string[] paths,
        string owner,
        string? claimId,
        bool force)
    {
        EditClaimRecord[] before = activeClaims.ToArray();
        activeClaims.RemoveAll(claim =>
        {
            bool matchesClaim = !string.IsNullOrWhiteSpace(claimId) &&
                                string.Equals(claim.ClaimId, claimId, StringComparison.Ordinal);
            bool matchesPath = paths.Length > 0 &&
                               claim.Paths.Any(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase));
            bool ownerAllowed = matchesClaim || force || string.Equals(claim.Owner, owner, StringComparison.OrdinalIgnoreCase);
            return ownerAllowed && (matchesClaim || matchesPath);
        });

        EditClaimRecord[] released = before
            .Where(claim => !activeClaims.Any(active => string.Equals(active.ClaimId, claim.ClaimId, StringComparison.Ordinal)))
            .ToArray();
        WriteStore(storePath, activeClaims);
        return new
        {
            ok = true,
            released_count = released.Length,
            released,
            expired_removed_count = expired.Length,
        };
    }

    private static bool IsSupportedOperation(string operation)
        => operation.Equals("status", StringComparison.OrdinalIgnoreCase) ||
           operation.Equals("claim", StringComparison.OrdinalIgnoreCase) ||
           operation.Equals("release", StringComparison.OrdinalIgnoreCase);

    private static string? GetOptionalString(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ResolveRepoRoot(string? explicitRepoRoot)
    {
        string start = string.IsNullOrWhiteSpace(explicitRepoRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(explicitRepoRoot);
        DirectoryInfo? current = new(start);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "RoslynSkills.slnx")) ||
                Directory.EnumerateFiles(current.FullName, "*.sln*").Any())
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return start;
    }

    private static string NormalizeClaimPath(string repoRoot, string path)
    {
        string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path));
        string relative = Path.GetRelativePath(repoRoot, fullPath);
        return relative.Replace('\\', '/');
    }

    private static string GetStorePath(string repoRoot)
        => Path.Combine(repoRoot, ".roslynskills", "edit-claims.json");

    private static EditClaimStore ReadStore(string storePath)
    {
        if (!File.Exists(storePath))
        {
            return new EditClaimStore(Array.Empty<EditClaimRecord>());
        }

        try
        {
            EditClaimStore? store = JsonSerializer.Deserialize<EditClaimStore>(File.ReadAllText(storePath), JsonOptions);
            return store ?? new EditClaimStore(Array.Empty<EditClaimRecord>());
        }
        catch (JsonException)
        {
            return new EditClaimStore(Array.Empty<EditClaimRecord>());
        }
    }

    private static void WriteStore(string storePath, IEnumerable<EditClaimRecord> claims)
    {
        string? directory = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        EditClaimStore store = new(claims
            .OrderBy(claim => claim.Paths.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(claim => claim.Owner, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        File.WriteAllText(storePath, JsonSerializer.Serialize(store, JsonOptions));
    }

    private sealed record EditClaimStore(
        [property: JsonPropertyName("claims")] IReadOnlyList<EditClaimRecord> Claims);

    private sealed record EditClaimRecord(
        [property: JsonPropertyName("claim_id")] string ClaimId,
        [property: JsonPropertyName("owner")] string Owner,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
        [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
        [property: JsonPropertyName("expires_at_utc")] DateTimeOffset ExpiresAtUtc);
}
