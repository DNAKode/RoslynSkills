using System.Text.Json;

namespace RoslynSkills.Benchmark;

public static class ScenarioLoader
{
    public static IReadOnlyList<Scenario> LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Scenario file not found.", path);
        }

        string json = File.ReadAllText(path);
        Scenario[] scenarios = JsonSerializer.Deserialize<Scenario[]>(json) ?? Array.Empty<Scenario>();
        return scenarios;
    }
}

