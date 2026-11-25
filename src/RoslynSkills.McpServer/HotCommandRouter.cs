#nullable enable
using System.Text.Json;
using RoslynSkills.Cli;
using RoslynSkills.Contracts;
using RoslynSkills.Core;

namespace RoslynSkills.McpServer;

/// <summary>
/// Routes MCP tool calls for hot-workspace commands through the same path <c>roscli run</c> takes, so a call reaches
/// the warm WorkspaceHost daemon bound for the repository (alias <c>default</c>) instead of reloading the solution
/// inside the MCP process. Routing, refresh policy, fail-closed <c>ROSCLI_DAEMON=required</c> behavior and the daemon
/// protocol are therefore identical to the shell CLI, not a second implementation.
/// </summary>
/// <remarks>
/// <c>ROSCLI_DAEMON=off</c> (or <c>ROSLYNSKILLS_MCP_ROUTING=off</c>) keeps the previous in-process execution.
/// </remarks>
internal sealed class HotCommandRouter
{
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly CliApplication _cli;

    public HotCommandRouter(ICommandRegistry registry)
    {
        _cli = new CliApplication(registry);
    }

    public static bool IsEnabled()
    {
        string? mcpRouting = Environment.GetEnvironmentVariable("ROSLYNSKILLS_MCP_ROUTING");
        if (string.Equals(mcpRouting?.Trim(), "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? daemon = Environment.GetEnvironmentVariable("ROSCLI_DAEMON")?.Trim();
        return !(string.Equals(daemon, "off", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(daemon, "0", StringComparison.Ordinal) ||
                 string.Equals(daemon, "false", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldRoute(IAgentCommand command)
        => command.Descriptor.RunsOnHotWorkspace && IsEnabled();

    /// <summary>
    /// Runs <paramref name="commandId"/> exactly as <c>roscli run &lt;command&gt; --input &lt;json&gt;</c> would and returns its
    /// envelope relabeled with <c>transport.mode = "mcp"</c> (daemon endpoint, handle and timings are kept).
    /// Returns null when the CLI produced no parseable envelope, so the caller can fall back to direct execution.
    /// </summary>
    public async Task<CommandEnvelope?> TryRunAsync(string commandId, JsonElement input, CancellationToken cancellationToken)
    {
        string inputJson = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : input.GetRawText();
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        await _cli.RunAsync(
            new[] { "run", commandId, "--input", inputJson },
            stdout,
            stderr,
            cancellationToken,
            stdin: TextReader.Null).ConfigureAwait(false);

        string text = stdout.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        CommandEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CommandEnvelope>(text, EnvelopeJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope is null)
        {
            return null;
        }

        CommandTransportMetadata transport = envelope.Transport is null
            ? new CommandTransportMetadata(Mode: "mcp")
            : envelope.Transport with { Mode = "mcp" };
        return envelope with { Transport = transport };
    }
}
