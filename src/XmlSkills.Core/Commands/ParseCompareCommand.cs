using System.Text.Json;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

public sealed class ParseCompareCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "xml.parse_compare",
        Summary: "Compare strict XDocument parsing against experimental language_xml parsing on the same file.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        Maturity: CommandMaturity.Experimental,
        Traits: new[] { CommandTrait.DerivedAnalysis });

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        _ = XmlParsingSupport.TryReadRequiredFilePath(input, errors, out _);
        InputParsing.ValidateOptionalBool(input, "require_language_xml", errors);
        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!XmlParsingSupport.TryReadRequiredFilePath(input, errors, out string filePath))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        bool requireLanguageXml = InputParsing.GetOptionalBool(input, "require_language_xml", defaultValue: false);
        bool languageXmlEnabled = XmlParsingSupport.IsLanguageXmlEnabled();
        if (requireLanguageXml && !languageXmlEnabled)
        {
            errors.Add(new CommandError(
                "backend_not_enabled",
                "require_language_xml=true but backend is disabled. Set XMLCLI_ENABLE_LANGUAGE_XML=1."));
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        BackendParseResult xdocument = XmlParsingSupport.ParseWithBackend(filePath, XmlParserBackend.XDocument);
        BackendParseResult? languageXml = null;
        if (languageXmlEnabled)
        {
            languageXml = XmlParsingSupport.ParseWithBackend(filePath, XmlParserBackend.LanguageXml);
        }

        object data = new
        {
            file_path = filePath,
            language_xml_enabled = languageXmlEnabled,
            strict_backend = ToBackendObject(xdocument),
            tolerant_backend = languageXml is null ? null : ToBackendObject(languageXml),
            comparison = new
            {
                strict_parse_succeeded = xdocument.Success,
                tolerant_parse_succeeded = languageXml?.Success,
                divergence_detected = languageXml is not null && xdocument.Success != languageXml.Success,
                strict_root = xdocument.Document?.RootName,
                tolerant_root = languageXml?.Document?.RootName,
                strict_elements = xdocument.Document?.Elements.Count ?? 0,
                tolerant_elements = languageXml?.Document?.Elements.Count ?? 0,
            },
        };

        object telemetry = new
        {
            timing = new
            {
                strict_parse_ms = xdocument.DurationMs,
                tolerant_parse_ms = languageXml?.DurationMs,
                total_parse_ms = xdocument.DurationMs + (languageXml?.DurationMs ?? 0),
            },
            cache_context = new
            {
                strict = new
                {
                    parse_cache_mode = xdocument.ParseCacheMode,
                    parse_cache_hit = xdocument.ParseCacheHit,
                },
                tolerant = languageXml is null
                    ? null
                    : new
                    {
                        parse_cache_mode = languageXml.ParseCacheMode,
                        parse_cache_hit = languageXml.ParseCacheHit,
                    },
            },
            backend = new
            {
                strict = xdocument.Backend,
                tolerant = languageXml?.Backend,
            },
        };

        return Task.FromResult(new CommandExecutionResult(
            Data: data,
            Errors: Array.Empty<CommandError>(),
            Telemetry: telemetry));
    }

    private static object ToBackendObject(BackendParseResult result)
    {
        return new
        {
            backend = result.Backend,
            success = result.Success,
            strict_well_formed = result.StrictWellFormed,
            duration_ms = result.DurationMs,
            root_name = result.Document?.RootName,
            element_count = result.Document?.Elements.Count ?? 0,
            attribute_count = result.Document?.Elements.Sum(e => e.Attributes.Count) ?? 0,
            error = result.Error,
        };
    }
}
