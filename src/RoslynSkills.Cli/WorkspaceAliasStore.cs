using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynSkills.Core.Commands;

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
        _path = RepositoryRootDiscovery.GetWorkspaceAliasesPath(repoRoot);
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
        if (!aliases.Remove(alias))
        {
            return;
        }

        if (aliases.Count == 0)
        {
            TryDeleteFile(_path);
            return;
        }

        Save(aliases);
    }

    public void Clear()
        => TryDeleteFile(_path);

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
        string directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string payload = JsonSerializer.Serialize(aliases, JsonOptions);
        string tempPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, payload);
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (IOException)
        {
            File.WriteAllText(_path, payload);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
