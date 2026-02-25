using System.Text.Json;
using System.Xml.Linq;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

public sealed class ReplaceElementTextCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "xml.replace_element_text",
        Summary: "Replace text of matching leaf XML elements with dry-run-by-default behavior.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        _ = XmlParsingSupport.TryReadRequiredFilePath(input, errors, out _);
        _ = InputParsing.TryGetRequiredString(input, "element_name", errors, out _);
        _ = InputParsing.TryGetRequiredString(input, "new_text", errors, out _);
        InputParsing.ValidateOptionalBool(input, "case_sensitive", errors);
        InputParsing.ValidateOptionalBool(input, "apply", errors);
        InputParsing.ValidateOptionalInt(input, "max_replacements", errors, 1, 10000);
        if (XmlParsingSupport.TryResolveBackend(input, errors, out XmlParserBackend backend))
        {
            _ = XmlParsingSupport.EnsureBackendEnabled(backend, errors);
            bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: false);
            if (backend == XmlParserBackend.LanguageXml && apply)
            {
                errors.Add(new CommandError(
                    "unsupported_write_mode",
                    "backend='language_xml' supports simulation only. Set apply=false (default) or use backend='xdocument' for writes."));
            }
        }

        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!XmlParsingSupport.TryReadRequiredFilePath(input, errors, out string filePath) ||
            !InputParsing.TryGetRequiredString(input, "element_name", errors, out string elementName) ||
            !InputParsing.TryGetRequiredString(input, "new_text", errors, out string newText) ||
            !XmlParsingSupport.TryResolveBackend(input, errors, out XmlParserBackend backend) ||
            !XmlParsingSupport.EnsureBackendEnabled(backend, errors))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        bool caseSensitive = InputParsing.GetOptionalBool(input, "case_sensitive", defaultValue: false);
        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: false);
        int maxReplacements = InputParsing.GetOptionalInt(input, "max_replacements", defaultValue: 1000, minValue: 1, maxValue: 10000);

        if (backend == XmlParserBackend.LanguageXml)
        {
            if (apply)
            {
                errors.Add(new CommandError(
                    "unsupported_write_mode",
                    "backend='language_xml' supports simulation only. Set apply=false (default) or use backend='xdocument' for writes."));
                return Task.FromResult(new CommandExecutionResult(null, errors));
            }

            return Task.FromResult(RunLanguageXmlSimulation(
                filePath,
                elementName,
                newText,
                caseSensitive,
                maxReplacements));
        }

        return Task.FromResult(RunXDocumentEdit(
            filePath,
            elementName,
            newText,
            caseSensitive,
            apply,
            maxReplacements));
    }

    private static CommandExecutionResult RunLanguageXmlSimulation(
        string filePath,
        string elementName,
        string newText,
        bool caseSensitive,
        int maxReplacements)
    {
        BackendParseResult parse = XmlParsingSupport.ParseWithBackend(filePath, XmlParserBackend.LanguageXml);
        if (!parse.Success || parse.Document is null)
        {
            return new CommandExecutionResult(
                Data: null,
                Errors: new[] { parse.Error ?? new CommandError("parse_failed", $"Failed to parse '{filePath}'.") },
                Telemetry: XmlParsingSupport.BuildParseTelemetry(parse));
        }

        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        ParsedXmlElement[] matches = parse.Document.Elements
            .Where(e => string.Equals(e.Name, elementName, comparison))
            .ToArray();

        int skippedNonLeaf = 0;
        int changedCount = 0;
        int unchangedLeafCount = 0;
        List<object> changePreview = new();

        foreach (ParsedXmlElement match in matches)
        {
            if (match.HasChildElements)
            {
                skippedNonLeaf++;
                continue;
            }

            if (changedCount >= maxReplacements)
            {
                break;
            }

            string before = match.Value;
            if (string.Equals(before, newText, StringComparison.Ordinal))
            {
                unchangedLeafCount++;
                continue;
            }

            changedCount++;
            if (changePreview.Count < 25)
            {
                changePreview.Add(new
                {
                    path = match.Path,
                    line = match.Line,
                    column = match.Column,
                    before,
                    after = newText,
                });
            }
        }

        bool changed = changedCount > 0;
        object data = new
        {
            file_path = filePath,
            backend = "language_xml",
            simulated = true,
            language_xml_enabled = XmlParsingSupport.IsLanguageXmlEnabled(),
            element_name = elementName,
            apply = false,
            case_sensitive = caseSensitive,
            max_replacements = maxReplacements,
            total_matches = matches.Length,
            skipped_non_leaf_count = skippedNonLeaf,
            unchanged_leaf_count = unchangedLeafCount,
            replaced_count = changedCount,
            changed,
            persisted = false,
            dry_run = true,
            truncated_by_max_replacements = changedCount >= maxReplacements,
            changes_preview = changePreview.ToArray(),
            notes = new[]
            {
                "backend='language_xml' currently performs simulation only and does not write files.",
            },
        };

        return new CommandExecutionResult(
            Data: data,
            Errors: Array.Empty<CommandError>(),
            Telemetry: XmlParsingSupport.BuildParseTelemetry(parse));
    }

    private static CommandExecutionResult RunXDocumentEdit(
        string filePath,
        string elementName,
        string newText,
        bool caseSensitive,
        bool apply,
        int maxReplacements)
    {
        System.Diagnostics.Stopwatch parseTimer = System.Diagnostics.Stopwatch.StartNew();
        XDocument document;
        try
        {
            document = XDocument.Load(
                filePath,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            parseTimer.Stop();
        }
        catch (System.Xml.XmlException ex)
        {
            parseTimer.Stop();
            return new CommandExecutionResult(
                Data: null,
                Errors: new[]
                {
                    new CommandError(
                        "invalid_xml",
                        $"Failed to parse XML file '{filePath}'.",
                        new { ex.LineNumber, ex.LinePosition, ex.Message }),
                },
                Telemetry: XmlParsingSupport.BuildParseTelemetry("xdocument", (int)parseTimer.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            parseTimer.Stop();
            return new CommandExecutionResult(
                Data: null,
                Errors: new[]
                {
                    new CommandError(
                        "xml_load_failed",
                        $"Could not read XML file '{filePath}'.",
                        new { ex.Message }),
                },
                Telemetry: XmlParsingSupport.BuildParseTelemetry("xdocument", (int)parseTimer.ElapsedMilliseconds));
        }

        if (document.Root is null)
        {
            return new CommandExecutionResult(
                Data: null,
                Errors: new[]
                {
                    new CommandError("invalid_xml", $"XML file '{filePath}' has no root element."),
                },
                Telemetry: XmlParsingSupport.BuildParseTelemetry("xdocument", (int)parseTimer.ElapsedMilliseconds));
        }

        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        XElement[] matches = document.Root
            .DescendantsAndSelf()
            .Where(e => string.Equals(e.Name.LocalName, elementName, comparison))
            .ToArray();

        int skippedNonLeaf = 0;
        int changedCount = 0;
        int unchangedLeafCount = 0;
        List<object> changePreview = new();

        foreach (XElement match in matches)
        {
            if (match.HasElements)
            {
                skippedNonLeaf++;
                continue;
            }

            if (changedCount >= maxReplacements)
            {
                break;
            }

            string before = match.Value;
            if (string.Equals(before, newText, StringComparison.Ordinal))
            {
                unchangedLeafCount++;
                continue;
            }

            match.Value = newText;
            changedCount++;

            if (changePreview.Count < 25)
            {
                (int line, int column)? lineInfo = XmlParsingSupport.TryGetLineInfo(match);
                changePreview.Add(new
                {
                    path = XmlParsingSupport.BuildElementPath(match),
                    line = lineInfo?.line ?? 0,
                    column = lineInfo?.column ?? 0,
                    before,
                    after = newText,
                });
            }
        }

        bool changed = changedCount > 0;
        if (apply && changed)
        {
            document.Save(filePath, SaveOptions.DisableFormatting);
        }

        object data = new
        {
            file_path = filePath,
            backend = "xdocument",
            simulated = false,
            element_name = elementName,
            apply,
            case_sensitive = caseSensitive,
            max_replacements = maxReplacements,
            total_matches = matches.Length,
            skipped_non_leaf_count = skippedNonLeaf,
            unchanged_leaf_count = unchangedLeafCount,
            replaced_count = changedCount,
            changed,
            persisted = apply && changed,
            dry_run = !apply,
            truncated_by_max_replacements = changedCount >= maxReplacements,
            changes_preview = changePreview.ToArray(),
        };

        return new CommandExecutionResult(
            Data: data,
            Errors: Array.Empty<CommandError>(),
            Telemetry: XmlParsingSupport.BuildParseTelemetry("xdocument", (int)parseTimer.ElapsedMilliseconds));
    }
}
