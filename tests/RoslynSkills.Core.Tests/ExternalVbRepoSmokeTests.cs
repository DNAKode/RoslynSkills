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

        CfgCommand cfg = new();
        DataflowSliceCommand dataflow = new();

        int attempted = 0;
        foreach (MethodAnchor anchor in anchors)
        {
            attempted++;

            JsonElement cfgInput = ToJsonElement(new
            {
                file_path = anchor.FilePath,
                line = anchor.Line,
                column = anchor.Column,
                brief = true,
                max_blocks = 160,
                max_edges = 320,
                workspace_path = anchor.WorkspacePath,
                require_workspace = false,
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
                workspace_path = anchor.WorkspacePath,
                require_workspace = false,
            });

            CommandExecutionResult dataflowResult = await dataflow.ExecuteAsync(dataflowInput, CancellationToken.None);
            if (!dataflowResult.Ok)
            {
                continue;
            }

            string cfgJson = JsonSerializer.Serialize(cfgResult.Data);
            string dataflowJson = JsonSerializer.Serialize(dataflowResult.Data);

            Assert.Contains("\"cfg_summary\":", cfgJson);
            Assert.Contains("\"dataflow\":", dataflowJson);
            return;
        }

        Assert.Fail($"No anchor produced successful cfg+dataflow smoke on workspace '{workspacePath}'. Anchors attempted: {attempted}.");
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

                if (LooksBodylessSignature(lines[i]))
                {
                    continue;
                }

                if (!TryFindMethodBodyAnchor(lines, i, out int bodyLine, out int bodyColumn))
                {
                    continue;
                }

                string methodName = match.Groups[1].Value;
                string candidateWorkspacePath = ResolveNearestWorkspacePath(workspacePath, filePath);
                anchors.Add(new MethodAnchor(Path.GetFullPath(filePath), candidateWorkspacePath, methodName, bodyLine, bodyColumn));

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

    private static bool LooksBodylessSignature(string line)
    {
        return line.Contains("MustOverride", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Declare ", StringComparison.OrdinalIgnoreCase) ||
               line.Contains(" Interface ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindMethodBodyAnchor(string[] lines, int signatureIndex, out int line, out int column)
    {
        line = 0;
        column = 0;

        int endIndex = -1;
        for (int i = signatureIndex + 1; i < lines.Length && i < signatureIndex + 240; i++)
        {
            string text = lines[i].Trim();
            if (text.StartsWith("End Function", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("End Sub", StringComparison.OrdinalIgnoreCase))
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex <= signatureIndex + 1)
        {
            return false;
        }

        for (int i = signatureIndex + 1; i < endIndex; i++)
        {
            string text = lines[i];
            string trimmed = text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("'", StringComparison.Ordinal))
            {
                continue;
            }

            int firstNonSpace = 0;
            while (firstNonSpace < text.Length && char.IsWhiteSpace(text[firstNonSpace]))
            {
                firstNonSpace++;
            }

            line = i + 1;
            column = Math.Max(1, firstNonSpace + 1);
            return true;
        }

        return false;
    }

    private static string ResolveNearestWorkspacePath(string rootWorkspacePath, string filePath)
    {
        DirectoryInfo? cursor = new(Path.GetDirectoryName(filePath)!);
        while (cursor is not null)
        {
            string? vbproj = Directory.EnumerateFiles(cursor.FullName, "*.vbproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(vbproj))
            {
                return Path.GetFullPath(vbproj);
            }

            string? sln = Directory.EnumerateFiles(cursor.FullName, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(sln))
            {
                return Path.GetFullPath(sln);
            }

            cursor = cursor.Parent;
        }

        return Path.GetFullPath(rootWorkspacePath);
    }

    private static JsonElement ToJsonElement(object value)
    {
        string json = JsonSerializer.Serialize(value);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed record MethodAnchor(string FilePath, string WorkspacePath, string MethodName, int Line, int Column);
}
