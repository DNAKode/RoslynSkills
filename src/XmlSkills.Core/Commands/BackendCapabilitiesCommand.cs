using System.Text.Json;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

public sealed class BackendCapabilitiesCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "xml.backend_capabilities",
        Summary: "Report available XML parser backends, guarantees, and feature-flag status.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.ValidateOptionalBool(input, "include_experimental", errors);
        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        bool includeExperimental = InputParsing.GetOptionalBool(input, "include_experimental", defaultValue: true);
        bool languageXmlEnabled = XmlParsingSupport.IsLanguageXmlEnabled();

        List<object> backends = new()
        {
            new
            {
                id = "xdocument",
                status = "enabled",
                mode = "strict",
                supports_read = true,
                supports_write = true,
                notes = "Default backend. Enforces well-formed XML parsing.",
            },
        };

        if (includeExperimental)
        {
            backends.Add(new
            {
                id = "language_xml",
                status = languageXmlEnabled ? "enabled" : "disabled",
                mode = "tolerant",
                supports_read = true,
                supports_write = false,
                feature_flag = "XMLCLI_ENABLE_LANGUAGE_XML=1",
                notes = "Experimental backend based on Microsoft.Language.Xml; useful for tolerant parsing and comparison studies.",
            });
        }

        object data = new
        {
            include_experimental = includeExperimental,
            language_xml_enabled = languageXmlEnabled,
            backends = backends.ToArray(),
            write_behavior = new
            {
                xml_replace_element_text = "xdocument only",
            },
            recommendation = new
            {
                default_backend = "xdocument",
                comparison_command = "xml.parse_compare",
            },
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
