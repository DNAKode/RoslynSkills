using System.Text.Json;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

public sealed class FileOutlineCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "xml.file_outline",
        Summary: "Return a bounded structural outline of an XML document with line-aware node metadata.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        _ = XmlParsingSupport.TryReadRequiredFilePath(input, errors, out _);
        InputParsing.ValidateOptionalBool(input, "include_attributes", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);
        InputParsing.ValidateOptionalInt(input, "max_nodes", errors, 1, 2000);
        if (XmlParsingSupport.TryResolveBackend(input, errors, out XmlParserBackend backend))
        {
            _ = XmlParsingSupport.EnsureBackendEnabled(backend, errors);
        }

        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!XmlParsingSupport.TryReadRequiredFilePath(input, errors, out string filePath) ||
            !XmlParsingSupport.TryResolveBackend(input, errors, out XmlParserBackend backend) ||
            !XmlParsingSupport.EnsureBackendEnabled(backend, errors))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        BackendParseResult result = XmlParsingSupport.ParseWithBackend(filePath, backend);
        if (!result.Success || result.Document is null)
        {
            errors.Add(result.Error ?? new CommandError("parse_failed", $"Failed to parse '{filePath}'."));
            return Task.FromResult(new CommandExecutionResult(
                Data: null,
                Errors: errors,
                Telemetry: XmlParsingSupport.BuildParseTelemetry(result)));
        }

        bool brief = InputParsing.GetOptionalBool(input, "brief", defaultValue: false);
        bool includeAttributes = InputParsing.GetOptionalBool(input, "include_attributes", defaultValue: false);
        int maxNodes = InputParsing.GetOptionalInt(input, "max_nodes", defaultValue: 200, minValue: 1, maxValue: 2000);

        ParsedXmlElement[] allElements = result.Document.Elements.ToArray();
        ParsedXmlElement[] selectedElements = allElements.Take(maxNodes).ToArray();

        object[] nodes = brief
            ? Array.Empty<object>()
            : selectedElements.Select(element => new
            {
                name = element.Name,
                path = element.Path,
                depth = element.Depth,
                line = element.Line,
                column = element.Column,
                attribute_count = element.Attributes.Count,
                attributes = includeAttributes
                    ? element.Attributes.Select(a => new { name = a.Key, value = a.Value }).ToArray()
                    : null,
                text_preview = element.TextPreview,
            }).ToArray<object>();

        object data = new
        {
            file_path = filePath,
            backend = result.Backend,
            language_xml_enabled = XmlParsingSupport.IsLanguageXmlEnabled(),
            strict_well_formed = result.StrictWellFormed,
            duration_ms = result.DurationMs,
            root_name = result.Document.RootName,
            brief,
            include_attributes = includeAttributes,
            max_nodes = maxNodes,
            total_nodes = allElements.Length,
            returned_nodes = selectedElements.Length,
            truncated = allElements.Length > selectedElements.Length,
            nodes,
        };

        return Task.FromResult(new CommandExecutionResult(
            Data: data,
            Errors: Array.Empty<CommandError>(),
            Telemetry: XmlParsingSupport.BuildParseTelemetry(result)));
    }
}
