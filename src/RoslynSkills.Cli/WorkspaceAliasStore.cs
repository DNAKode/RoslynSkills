using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSkills.Cli;

public sealed record WorkspaceAliasRecord(
    [property: JsonPropertyName("workspace_path")] string? WorkspacePath,
    [property: JsonPropertyName("workspace_handle")] string WorkspaceHandle,
    [property: JsonPropertyName("daemon_endpoint")] string? DaemonEndpoint,
    [property: JsonPropertyName("daemon_pid")] int? DaemonPid,
    [property: JsonPropertyName("workspace_fingerprint")] string? WorkspaceFingerprint,
    [property: JsonPropertyName("last_seen_utc")] DateTimeOffset LastSeenUtc);

public sealed class WorkspaceAliasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public WorkspaceAliasStore(string repoRoot)
    {
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(repoRoot), ".roslynskills", "workspaces.json");
    }

    public string Path => _path;

    public bool TryGet(string alias, out WorkspaceAliasRecord? record)
    {
        Dictionary<string, WorkspaceAliasRecord> aliases = Load();
        return aliases.TryGetValue(alias, out record);
    }

    public IReadOnlyDictionary<string, WorkspaceAliasRecord> List()
        => Load();

    public void Upsert(string alias, WorkspaceAliasRecord record)
    {
        Dictionary<string, WorkspaceAliasRecord> aliases = Load();
        aliases[alias] = record;
        Save(aliases);
    }

    public void Remove(string alias)
    {
        Dictionary<string, WorkspaceAliasRecord> aliases = Load();
        if (aliases.Remove(alias))
        {
            Save(aliases);
        }
    }

    private Dictionary<string, WorkspaceAliasRecord> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, WorkspaceAliasRecord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            string json = File.ReadAllText(_path);
            Dictionary<string, WorkspaceAliasRecord>? aliases = JsonSerializer.Deserialize<Dictionary<string, WorkspaceAliasRecord>>(json, JsonOptions);
            return aliases is null
                ? new Dictionary<string, WorkspaceAliasRecord>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, WorkspaceAliasRecord>(aliases, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, WorkspaceAliasRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, WorkspaceAliasRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, WorkspaceAliasRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(Dictionary<string, WorkspaceAliasRecord> aliases)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(aliases, JsonOptions));
    }
}
