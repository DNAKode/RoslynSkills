using System.Text.Json;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

public sealed class FindElementsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "xml.find_elements",
        Summary: "Find XML elements by local name with optional case sensitivity and bounded results.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        _ = XmlParsingSupport.TryReadRequiredFilePath(input, errors, out _);
        _ = InputParsing.TryGetRequiredString(input, "element_name", errors, out _);
        InputParsing.ValidateOptionalBool(input, "case_sensitive", errors);
        InputParsing.ValidateOptionalBool(input, "include_attributes", errors);
        InputParsing.ValidateOptionalInt(input, "max_results", errors, 1, 2000);
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
            !InputParsing.TryGetRequiredString(input, "element_name", errors, out string elementName) ||
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

        bool caseSensitive = InputParsing.GetOptionalBool(input, "case_sensitive", defaultValue: false);
        bool includeAttributes = InputParsing.GetOptionalBool(input, "include_attributes", defaultValue: false);
        int maxResults = InputParsing.GetOptionalInt(input, "max_results", defaultValue: 200, minValue: 1, maxValue: 2000);

        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        ParsedXmlElement[] matches = result.Document.Elements
            .Where(e => string.Equals(e.Name, elementName, comparison))
            .ToArray();
        ParsedXmlElement[] selectedMatches = matches.Take(maxResults).ToArray();

        object[] results = selectedMatches.Select(element => new
        {
            name = element.Name,
            path = element.Path,
            line = element.Line,
            column = element.Column,
            text_preview = element.TextPreview,
            attributes = includeAttributes
                ? element.Attributes.Select(a => new { name = a.Key, value = a.Value }).ToArray()
                : null,
        }).ToArray<object>();

        object data = new
        {
            file_path = filePath,
            backend = result.Backend,
            language_xml_enabled = XmlParsingSupport.IsLanguageXmlEnabled(),
            strict_well_formed = result.StrictWellFormed,
            duration_ms = result.DurationMs,
            element_name = elementName,
            case_sensitive = caseSensitive,
            include_attributes = includeAttributes,
            max_results = maxResults,
            total_matches = matches.Length,
            returned_matches = selectedMatches.Length,
            truncated = matches.Length > selectedMatches.Length,
            matches = results,
        };

        return Task.FromResult(new CommandExecutionResult(
            Data: data,
            Errors: Array.Empty<CommandError>(),
            Telemetry: XmlParsingSupport.BuildParseTelemetry(result)));
    }
}
