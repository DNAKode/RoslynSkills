using System.Text.Json;

namespace RoslynAgent.Benchmark.AgentEval;

internal static class AgentEvalStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    public static AgentEvalManifest LoadManifest(string manifestPath)
    {
        string fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Agent eval manifest was not found.", fullPath);
        }

        string json = File.ReadAllText(fullPath);
        AgentEvalManifest? manifest = JsonSerializer.Deserialize<AgentEvalManifest>(json, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException("Failed to parse agent eval manifest.");
        }

        if (manifest.RunsPerCell <= 0)
        {
            throw new InvalidOperationException("Manifest runs_per_cell must be greater than zero.");
        }

        AgentEvalValidation.ValidateManifest(manifest);
        return manifest;
    }

    public static AgentEvalRun[] LoadRuns(string runsDirectory)
    {
        string fullDirectory = Path.GetFullPath(runsDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"Runs directory '{fullDirectory}' does not exist.");
        }

        string[] files = Directory.GetFiles(fullDirectory, "*.json", SearchOption.TopDirectoryOnly);
        List<AgentEvalRun> runs = new();
        foreach (string file in files)
        {
            string json = File.ReadAllText(file);
            AgentEvalRun? run = JsonSerializer.Deserialize<AgentEvalRun>(json, JsonOptions);
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return runs.ToArray();
    }

    public static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(path, json);
    }
}
