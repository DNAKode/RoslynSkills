using RoslynSkills.Cli;
using RoslynSkills.Core;
using System.Text.Json;

namespace RoslynSkills.Cli.Tests;

/// <summary>
/// CLI-level coverage of the Pass 3 fast paths: <c>--symbol</c> resolution through the per-compilation
/// <c>SymbolNameIndex</c>, the pre-filtered <c>nav.find_invocations</c>, and <c>nav.call_hierarchy</c> incoming callers
/// of interface implementations (<c>854z</c>). The MCP server and the hot daemon execute these same command classes.
/// </summary>
public sealed class NavFastPathCliTests
{
    private const string DemoSource = """
        using Alias = Demo.Widget;
        using static Demo.Util;

        namespace Demo;

        public interface IStore { void Save(int id); }

        public sealed class FileStore : IStore
        {
            public void Save(int id) { }
        }

        public sealed record Item(string Kind);

        public class Widget
        {
            public Widget() { }
            public Widget(int x) { }
            public int Run<T>(T value) => 1;
            public int Run(int value) => 2;
        }

        public static class Util
        {
            public static int Make(int n) => n;
        }

        public sealed class Consumer
        {
            private readonly System.Func<int, int> factory = Make;

            public void Persist(IStore store) { store.Save(1); }

            public void Go(Widget w, Item item)
            {
                _ = new Alias(5);
                Widget made = new(7);
                w.Run(1);
                w.Run<string>("s");
                Make(3);
                _ = item is { Kind: "a" };
            }
        }

        public static class Program
        {
            public static void Main() { new Consumer().Persist(new FileStore()); }
        }
        """;

    [Fact]
    public async Task NavCommands_SymbolFastPaths_ResolveAndSearchThroughCli()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-pass3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string slnx = await WriteSolutionAsync(tempDir);
            CliApplication app = new(DefaultRegistryFactory.Create());

            // Positional record property (synthesized, absent from a declaration scan) and its pattern reference.
            using (JsonDocument refs = await RunOkAsync(app, "nav.find_references", "--symbol", "Item.Kind", "--workspace-path", slnx, "--no-daemon"))
            {
                Assert.Equal(2, refs.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32());
            }

            // `using static` call + method group conversion.
            using (JsonDocument make = await RunOkAsync(app, "nav.find_invocations", "--symbol", "Util.Make", "--workspace-path", slnx, "--no-daemon"))
            {
                Assert.Equal(2, make.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32());
            }

            // Generic overload versus non-generic overload.
            using (JsonDocument generic = await RunOkAsync(app, "nav.find_invocations", "--symbol", "Widget.Run<T>", "--workspace-path", slnx, "--no-daemon"))
            {
                Assert.Equal(1, generic.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32());
            }

            using (JsonDocument plain = await RunOkAsync(app, "nav.find_invocations", "--symbol", "Widget.Run(int)", "--workspace-path", slnx, "--no-daemon"))
            {
                Assert.Equal(1, plain.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32());
            }

            // Alias and target-typed constructor calls.
            using (JsonDocument ctor = await RunOkAsync(app, "nav.find_invocations", "--symbol", "Widget..ctor(int)", "--workspace-path", slnx, "--no-daemon"))
            {
                Assert.Equal(2, ctor.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32()); // new Alias(5), new(7)
            }

            using (JsonDocument type = await RunOkAsync(app, "nav.find_invocations", "--symbol", "Demo.Widget", "--workspace-path", slnx, "--no-daemon"))
            {
                Assert.Equal(2, type.RootElement.GetProperty("Data").GetProperty("total_matches").GetInt32()); // new Alias(5), new(7)
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CallHierarchy_IncomingForInterfaceImplementation_FollowsInterfaceCallersThroughCli()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslynskills-cli-854z-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string slnx = await WriteSolutionAsync(tempDir);
            CliApplication app = new(DefaultRegistryFactory.Create());

            using JsonDocument hier = await RunOkAsync(
                app,
                "nav.call_hierarchy",
                "--symbol", "FileStore.Save",
                "--direction", "incoming",
                "--max-depth", "2",
                "--workspace-path", slnx,
                "--no-daemon");

            JsonElement data = hier.RootElement.GetProperty("Data");
            string nodes = data.GetProperty("nodes").GetRawText();
            Assert.Contains("Consumer.Persist", nodes); // calls IStore.Save
            Assert.Contains("Program.Main", nodes);     // depth 2
            Assert.Contains(
                data.GetProperty("edges").EnumerateArray(),
                edge => edge.GetProperty("implemented_member").GetString()?.Contains("IStore.Save", StringComparison.Ordinal) == true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<string> WriteSolutionAsync(string tempDir)
    {
        string csproj = Path.Combine(tempDir, "App.csproj");
        string slnx = Path.Combine(tempDir, "App.slnx");
        await File.WriteAllTextAsync(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(slnx, """
            <Solution>
              <Project Path="App.csproj" />
            </Solution>
            """);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "Demo.cs"), DemoSource);
        return slnx;
    }

    private static async Task<JsonDocument> RunOkAsync(CliApplication app, params string[] cliArgs)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        int exitCode = await app.RunAsync(cliArgs, stdout, stderr, CancellationToken.None);
        string text = stdout.ToString();
        Assert.True(exitCode == 0, $"{string.Join(" ", cliArgs)} failed:\nSTDOUT:\n{text}\nSTDERR:\n{stderr}");
        JsonDocument doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("Ok").GetBoolean(), text);
        return doc;
    }
}
