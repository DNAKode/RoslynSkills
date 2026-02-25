using XmlSkills.Cli;
using XmlSkills.Core;

namespace XmlSkills.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task ListCommands_IncludesXmlCommands()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(new[] { "list-commands", "--ids-only" }, stdout, stderr, CancellationToken.None);
        string output = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("xml.validate_document", output);
        Assert.Contains("xml.backend_capabilities", output);
        Assert.Contains("xml.file_outline", output);
        Assert.Contains("xml.find_elements", output);
        Assert.Contains("xml.replace_element_text", output);
        Assert.Contains("xml.parse_compare", output);
    }

    [Fact]
    public async Task Llmstxt_ReturnsBootstrapGuide()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(new[] { "llmstxt" }, stdout, stderr, CancellationToken.None);
        string output = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("# xmlcli llmstxt", output);
        Assert.Contains("xml.validate_document", output);
        Assert.Contains("Intent Recipes", output);
        Assert.Contains("Output Interpretation", output);
        Assert.Contains("--brief true", output);
    }

    [Fact]
    public async Task Quickstart_IncludesIntentRecipesAndOutputHints()
    {
        CliApplication app = new(DefaultRegistryFactory.Create());
        StringWriter stdout = new();
        StringWriter stderr = new();

        int exitCode = await app.RunAsync(new[] { "quickstart" }, stdout, stderr, CancellationToken.None);
        string output = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("\"intent_recipes\": {", output);
        Assert.Contains("\"safe_edit\": [", output);
        Assert.Contains("\"output_hints\": [", output);
        Assert.Contains("brief true returns summary counts", output);
    }

    [Fact]
    public async Task ValidateDocument_OnValidXml_Succeeds()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><item id=\"1\">ok</item></root>");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(new[] { "xml.validate_document", filePath }, stdout, stderr, CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"xml.validate_document\"", output);
            Assert.Contains("\"parse_succeeded\": true", output);
            Assert.Contains("\"strict_well_formed\": true", output);
            Assert.Contains("\"Telemetry\": {", output);
            Assert.Contains("\"validate_ms\":", output);
            Assert.Contains("\"execute_ms\":", output);
            Assert.Contains("\"total_ms\":", output);
            Assert.Contains("\"command_telemetry\": {", output);
            Assert.Contains("\"parse_cache_mode\": \"none\"", output);
            Assert.Contains("\"parse_cache_hit\": false", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ValidateDocument_OnInvalidXml_Fails()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><item></root>");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(new[] { "xml.validate_document", filePath }, stdout, stderr, CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(1, exitCode);
            Assert.Contains("invalid_xml", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task FindElements_AppliesMaxResultsOption()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><item>A</item><item>B</item></root>");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.find_elements", filePath, "item", "--max-results", "1" },
                stdout,
                stderr,
                CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(0, exitCode);
            Assert.Contains("\"total_matches\": 2", output);
            Assert.Contains("\"returned_matches\": 1", output);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task LanguageXmlBackend_IsFeatureGated()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><item>A</item></root>");

        string? previous = Environment.GetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML");
        Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", null);
        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.find_elements", filePath, "item", "--backend", "language_xml" },
                stdout,
                stderr,
                CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(1, exitCode);
            Assert.Contains("backend_not_enabled", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", previous);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task LanguageXmlBackend_WhenEnabled_ParsesSuccessfully()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><item>A</item></root>");

        string? previous = Environment.GetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML");
        Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", "1");
        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.find_elements", filePath, "item", "--backend", "language_xml" },
                stdout,
                stderr,
                CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(0, exitCode);
            Assert.Contains("\"backend\": \"language_xml\"", output);
            Assert.Contains("\"total_matches\": 1", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", previous);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task BackendCapabilities_ReturnsBackendStatus()
    {
        string? previous = Environment.GetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML");
        Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", "1");
        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.backend_capabilities" },
                stdout,
                stderr,
                CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"xml.backend_capabilities\"", output);
            Assert.Contains("\"id\": \"xdocument\"", output);
            Assert.Contains("\"id\": \"language_xml\"", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", previous);
        }
    }

    [Fact]
    public async Task ParseCompare_ReturnsStrictAndTolerantSlices()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><item>A</item></root>");

        string? previous = Environment.GetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML");
        Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", "1");
        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.parse_compare", filePath },
                stdout,
                stderr,
                CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(0, exitCode);
            Assert.Contains("\"CommandId\": \"xml.parse_compare\"", output);
            Assert.Contains("\"strict_backend\": {", output);
            Assert.Contains("\"tolerant_backend\": {", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", previous);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ReplaceElementText_DryRun_DoesNotWrite()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><title>Old</title></root>");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.replace_element_text", filePath, "title", "New" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            string fileContent = await File.ReadAllTextAsync(filePath);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"dry_run\": true", output);
            Assert.Contains("<title>Old</title>", fileContent);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ReplaceElementText_WithApply_WritesFile()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><title>Old</title></root>");

        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.replace_element_text", filePath, "title", "New", "--apply", "true" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            string fileContent = await File.ReadAllTextAsync(filePath);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"persisted\": true", output);
            Assert.Contains("<title>New</title>", fileContent);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ReplaceElementText_LanguageXml_DryRunSimulation_Works()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><title>Old</title></root>");

        string? previous = Environment.GetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML");
        Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", "1");
        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.replace_element_text", filePath, "title", "New", "--backend", "language_xml" },
                stdout,
                stderr,
                CancellationToken.None);

            string output = stdout.ToString();
            string fileContent = await File.ReadAllTextAsync(filePath);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"backend\": \"language_xml\"", output);
            Assert.Contains("\"simulated\": true", output);
            Assert.Contains("\"persisted\": false", output);
            Assert.Contains("<title>Old</title>", fileContent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", previous);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ReplaceElementText_LanguageXml_ApplyRejected()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xmlcli-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(filePath, "<root><title>Old</title></root>");

        string? previous = Environment.GetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML");
        Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", "1");
        try
        {
            CliApplication app = new(DefaultRegistryFactory.Create());
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exitCode = await app.RunAsync(
                new[] { "xml.replace_element_text", filePath, "title", "New", "--backend", "language_xml", "--apply", "true" },
                stdout,
                stderr,
                CancellationToken.None);
            string output = stdout.ToString();

            Assert.Equal(1, exitCode);
            Assert.Contains("unsupported_write_mode", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML", previous);
            File.Delete(filePath);
        }
    }
}
