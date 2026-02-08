using RoslynAgent.Cli;
using RoslynAgent.Core;

namespace RoslynAgent.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task ListCommands_ContainsExpectedCommands()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "list-commands" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("nav.find_symbol", output);
        Assert.Contains("diag.get_file_diagnostics", output);
    }

    [Fact]
    public async Task RunPing_ReturnsSuccessEnvelope()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(
            new[] { "run", "system.ping" },
            stdout,
            stderr,
            CancellationToken.None);

        string output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("\"Ok\": true", output);
        Assert.Contains("\"CommandId\": \"system.ping\"", output);
    }
}
