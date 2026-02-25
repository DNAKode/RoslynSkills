using System.Text.Json;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

public sealed class ValidateDocumentCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "xml.validate_document",
        Summary: "Validate XML parseability and structural summary using selected parser backend.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        _ = XmlParsingSupport.TryReadRequiredFilePath(input, errors, out _);
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

        ParsedXmlElement[] elements = result.Document.Elements.ToArray();
        int elementCount = elements.Length;
        int attributeCount = elements.Sum(e => e.Attributes.Count);
        int uniqueElementNames = elements
            .Select(e => e.Name)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int maxDepth = elements.Length == 0 ? 0 : elements.Max(e => e.Depth);

        object data = new
        {
            file_path = filePath,
            backend = result.Backend,
            language_xml_enabled = XmlParsingSupport.IsLanguageXmlEnabled(),
            validation_mode = result.StrictWellFormed ? "strict" : "tolerant",
            parse_succeeded = result.Success,
            strict_well_formed = result.StrictWellFormed,
            duration_ms = result.DurationMs,
            root_name = result.Document.RootName,
            summary = new
            {
                element_count = elementCount,
                attribute_count = attributeCount,
                unique_element_names = uniqueElementNames,
                max_depth = maxDepth,
            },
        };

        return Task.FromResult(new CommandExecutionResult(
            Data: data,
            Errors: Array.Empty<CommandError>(),
            Telemetry: XmlParsingSupport.BuildParseTelemetry(result)));
    }
}
