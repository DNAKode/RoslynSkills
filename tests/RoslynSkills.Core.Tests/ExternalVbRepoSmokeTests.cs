using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynSkills.Core.Tests;

public sealed class ExternalVbRepoSmokeTests
{
    private static readonly Regex MethodPattern = new(
        @"\b(?:Function|Sub)\s+([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public async Task DwsimStyleVbRepository_SmokeCommandsSucceed_WhenPathProvided()
    {
        string? workspacePath = Environment.GetEnvironmentVariable("ROSLYNSKILLS_DWSIM_PATH");
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return;
        }

        List<MethodAnchor> anchors = DiscoverMethodAnchors(workspacePath, maxAnchors: 120);
        Assert.True(anchors.Count > 0, $"No VB method anchors found under '{workspacePath}'.");

        FindSymbolCommand findSymbol = new();
        CfgCommand cfg = new();
        DataflowSliceCommand dataflow = new();

        int attempted = 0;
        foreach (MethodAnchor anchor in anchors)
        {
            attempted++;

            JsonElement symbolInput = ToJsonElement(new
            {
                file_path = anchor.FilePath,
                symbol_name = anchor.MethodName,
                brief = true,
                max_results = 10,
                workspace_path = workspacePath,
                require_workspace = true,
            });

            CommandExecutionResult symbolResult = await findSymbol.ExecuteAsync(symbolInput, CancellationToken.None);
            if (!symbolResult.Ok)
            {
                continue;
            }

            JsonElement cfgInput = ToJsonElement(new
            {
                file_path = anchor.FilePath,
                line = anchor.Line,
                column = anchor.Column,
                brief = true,
                max_blocks = 160,
                max_edges = 320,
                workspace_path = workspacePath,
                require_workspace = true,
            });

            CommandExecutionResult cfgResult = await cfg.ExecuteAsync(cfgInput, CancellationToken.None);
            if (!cfgResult.Ok)
            {
                continue;
            }

            JsonElement dataflowInput = ToJsonElement(new
            {
                file_path = anchor.FilePath,
                line = anchor.Line,
                column = anchor.Column,
                brief = true,
                max_symbols = 120,
                workspace_path = workspacePath,
                require_workspace = true,
            });

            CommandExecutionResult dataflowResult = await dataflow.ExecuteAsync(dataflowInput, CancellationToken.None);
            if (!dataflowResult.Ok)
            {
                continue;
            }

            string symbolJson = JsonSerializer.Serialize(symbolResult.Data);
            string cfgJson = JsonSerializer.Serialize(cfgResult.Data);
            string dataflowJson = JsonSerializer.Serialize(dataflowResult.Data);

            if (!symbolJson.Contains("\"workspace_context\":{\"mode\":\"workspace\"", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains("\"cfg_summary\":", cfgJson);
            Assert.Contains("\"dataflow\":", dataflowJson);
            return;
        }

        Assert.True(false, $"No anchor produced successful symbol+cfg+dataflow smoke on workspace '{workspacePath}'. Anchors attempted: {attempted}.");
    }

    private static List<MethodAnchor> DiscoverMethodAnchors(string workspacePath, int maxAnchors)
    {
        List<MethodAnchor> anchors = new(capacity: maxAnchors);

        foreach (string filePath in Directory.EnumerateFiles(workspacePath, "*.vb", SearchOption.AllDirectories))
        {
            if (ShouldSkipFile(filePath))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                Match match = MethodPattern.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                string methodName = match.Groups[1].Value;
                int column = match.Groups[1].Index + 1;
                anchors.Add(new MethodAnchor(Path.GetFullPath(filePath), methodName, i + 1, column));

                if (anchors.Count >= maxAnchors)
                {
                    return anchors;
                }
            }
        }

        return anchors;
    }

    private static bool ShouldSkipFile(string filePath)
    {
        string normalized = filePath.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement ToJsonElement(object value)
    {
        string json = JsonSerializer.Serialize(value);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed record MethodAnchor(string FilePath, string MethodName, int Line, int Column);
}
