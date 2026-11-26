using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.FastClient;

/// <summary>
/// <c>roscli-fast</c>: forwards a roscli invocation to the warm daemon of the current repository and prints exactly what
/// the full CLI would. See the project file for the contract; every doubt falls back to the full CLI.
/// </summary>
internal static class Program
{
    private const int ConnectTimeoutMs = 250;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static int Main(string[] args)
    {
        if (args.Length > 0 && !IsDisabled() && !NeedsFullCli(args))
        {
            try
            {
                int? exitCode = TryRunOnDaemon(args);
                if (exitCode.HasValue)
                {
                    return exitCode.Value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or TimeoutException or InvalidOperationException)
            {
                // Fall through to the full CLI; a thin client never fails a call the full CLI could serve.
            }
        }

        return RunFullCli(args);
    }

    private static bool IsDisabled()
    {
        string? mode = Environment.GetEnvironmentVariable("ROSCLI_FAST");
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? daemon = Environment.GetEnvironmentVariable("ROSCLI_DAEMON");
        return string.Equals(daemon, "off", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(daemon, "0", StringComparison.Ordinal) ||
               string.Equals(daemon, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsFullCli(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg is "--no-daemon" or "--input-stdin" or "--help" or "-h" or "--version")
            {
                return true;
            }
        }

        return false;
    }

    private static int? TryRunOnDaemon(string[] args)
    {
        string workingDirectory = Directory.GetCurrentDirectory();
        string repoRoot = RepositoryRootDiscovery.ResolveWorkspaceDaemonRoot(explicitRepoRoot: null);
        string manifestPath = RepositoryRootDiscovery.GetWorkspaceDaemonManifestPath(repoRoot);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        string pipeName;
        string? authToken;
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)))
        {
            JsonElement root = manifest.RootElement;
            if (!root.TryGetProperty("PipeName", out JsonElement pipeElement) ||
                pipeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(pipeElement.GetString()))
            {
                return null;
            }

            pipeName = pipeElement.GetString()!;
            authToken = root.TryGetProperty("AuthToken", out JsonElement tokenElement) && tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
        }

        if (OperatingSystem.IsWindows() && !File.Exists(@"\\.\pipe\" + pipeName))
        {
            return null;
        }

        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(ConnectTimeoutMs);

        using (StreamWriter writer = new(pipe, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = true })
        {
            writer.WriteLine(BuildRequest(args, workingDirectory, authToken));
        }

        using StreamReader reader = new(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
        string? responseLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            return null;
        }

        using JsonDocument response = JsonDocument.Parse(responseLine);
        JsonElement responseRoot = response.RootElement;
        if (!responseRoot.TryGetProperty("ok", out JsonElement ok) ||
            ok.ValueKind != JsonValueKind.True ||
            !responseRoot.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("exit_code", out JsonElement exitCodeElement) ||
            !data.TryGetProperty("stdout", out JsonElement stdoutElement))
        {
            return null;
        }

        string stdout = stdoutElement.GetString() ?? string.Empty;
        using Stream output = Console.OpenStandardOutput();
        byte[] bytes = Utf8NoBom.GetBytes(stdout);
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
        return exitCodeElement.GetInt32();
    }

    private static string BuildRequest(string[] args, string workingDirectory, string? authToken)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteString("id", "fast");
            json.WriteString("method", "cli/run");
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                json.WriteString("auth_token", authToken);
            }

            json.WriteStartObject("input");
            json.WriteStartArray("args");
            foreach (string arg in args)
            {
                json.WriteStringValue(arg);
            }

            json.WriteEndArray();
            json.WriteString("cwd", workingDirectory);
            json.WriteBoolean("stdout_redirected", Console.IsOutputRedirected);
            json.WriteStartObject("env");
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name &&
                    (name.StartsWith("ROSCLI_", StringComparison.Ordinal) || name.StartsWith("ROSLYNSKILLS_", StringComparison.Ordinal)) &&
                    entry.Value is string value)
                {
                    json.WriteString(name, value);
                }
            }

            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Utf8NoBom.GetString(buffer.ToArray());
    }

    private static int RunFullCli(string[] args)
    {
        string? fullCli = FindFullCli();
        if (fullCli is null)
        {
            Console.Error.WriteLine("roscli-fast: no daemon answered and the full roscli was not found. Set ROSCLI_FULL_PATH to its location.");
            return 127;
        }

        ProcessStartInfo startInfo = new(fullCli) { UseShellExecute = false };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fullCli}'.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string? FindFullCli()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("ROSCLI_FULL_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        string self = Path.GetFullPath(Environment.ProcessPath ?? string.Empty);
        string[] names = OperatingSystem.IsWindows()
            ? new[] { "roscli-full.exe", "roscli.exe", "RoslynSkills.Cli.exe" }
            : new[] { "roscli-full", "roscli", "RoslynSkills.Cli" };
        string? selfDirectory = Path.GetDirectoryName(self);
        IEnumerable<string> directories = (selfDirectory is null ? Array.Empty<string>() : new[] { selfDirectory })
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (string directory in directories)
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) && !string.Equals(Path.GetFullPath(candidate), self, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
