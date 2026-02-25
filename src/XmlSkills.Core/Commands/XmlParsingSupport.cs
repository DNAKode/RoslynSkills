using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Language.Xml;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

internal enum XmlParserBackend
{
    XDocument,
    LanguageXml,
}

internal sealed record ParsedXmlElement(
    string Name,
    string Path,
    int Depth,
    int Line,
    int Column,
    string Value,
    bool HasChildElements,
    string? TextPreview,
    IReadOnlyList<KeyValuePair<string, string>> Attributes);

internal sealed record ParsedXmlDocument(
    string Backend,
    string? RootName,
    IReadOnlyList<ParsedXmlElement> Elements);

internal sealed record BackendParseResult(
    string Backend,
    bool Success,
    bool StrictWellFormed,
    int DurationMs,
    ParsedXmlDocument? Document,
    CommandError? Error,
    string ParseCacheMode = "none",
    bool ParseCacheHit = false);

internal static class XmlParsingSupport
{
    public static bool TryReadRequiredFilePath(
        JsonElement input,
        List<CommandError> errors,
        out string filePath)
    {
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string rawPath))
        {
            filePath = string.Empty;
            return false;
        }

        filePath = Path.GetFullPath(rawPath);
        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError(
                "file_not_found",
                $"XML file '{filePath}' was not found."));
            return false;
        }

        return true;
    }

    public static bool TryResolveBackend(
        JsonElement input,
        List<CommandError> errors,
        out XmlParserBackend backend)
    {
        backend = XmlParserBackend.XDocument;
        string? raw = null;
        if (input.TryGetProperty("backend", out JsonElement backendProp))
        {
            if (backendProp.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", "Property 'backend' must be a string when provided."));
                return false;
            }

            raw = backendProp.GetString();
        }
        else if (input.TryGetProperty("parser_backend", out JsonElement parserBackendProp))
        {
            if (parserBackendProp.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", "Property 'parser_backend' must be a string when provided."));
                return false;
            }

            raw = parserBackendProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        string normalized = raw.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        switch (normalized)
        {
            case "xdocument":
            case "linq":
            case "linq_to_xml":
                backend = XmlParserBackend.XDocument;
                return true;
            case "language_xml":
            case "microsoft_language_xml":
            case "roslyn_xml":
                backend = XmlParserBackend.LanguageXml;
                return true;
            default:
                errors.Add(new CommandError(
                    "invalid_input",
                    $"Unsupported backend '{raw}'. Expected 'xdocument' or 'language_xml'."));
                return false;
        }
    }

    public static bool EnsureBackendEnabled(
        XmlParserBackend backend,
        List<CommandError> errors)
    {
        if (backend != XmlParserBackend.LanguageXml)
        {
            return true;
        }

        if (IsLanguageXmlEnabled())
        {
            return true;
        }

        errors.Add(new CommandError(
            "backend_not_enabled",
            "Backend 'language_xml' is disabled. Set XMLCLI_ENABLE_LANGUAGE_XML=1 to enable experimental parser backend mode."));
        return false;
    }

    public static bool IsLanguageXmlEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("XMLCLI_ENABLE_LANGUAGE_XML");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            _ => false,
        };
    }

    public static BackendParseResult ParseWithBackend(
        string filePath,
        XmlParserBackend backend)
    {
        return backend switch
        {
            XmlParserBackend.XDocument => ParseWithXDocument(filePath),
            XmlParserBackend.LanguageXml => ParseWithLanguageXml(filePath),
            _ => new BackendParseResult("unknown", false, false, 0, null, new CommandError("unsupported_backend", $"Unsupported backend '{backend}'.")),
        };
    }

    public static object BuildParseTelemetry(BackendParseResult result)
        => BuildParseTelemetry(result.Backend, result.DurationMs, result.ParseCacheMode, result.ParseCacheHit);

    public static object BuildParseTelemetry(
        string backend,
        int parseDurationMs,
        string parseCacheMode = "none",
        bool parseCacheHit = false)
    {
        return new
        {
            backend,
            timing = new
            {
                parse_ms = parseDurationMs,
            },
            cache_context = new
            {
                parse_cache_mode = parseCacheMode,
                parse_cache_hit = parseCacheHit,
            },
        };
    }

    public static (int line, int column)? TryGetLineInfo(XObject node)
    {
        if (node is IXmlLineInfo info && info.HasLineInfo())
        {
            return (info.LineNumber, info.LinePosition);
        }

        return null;
    }

    public static string BuildElementPath(XElement element)
    {
        Stack<string> segments = new();
        XElement? current = element;
        while (current is not null)
        {
            segments.Push(current.Name.LocalName);
            current = current.Parent;
        }

        return "/" + string.Join("/", segments);
    }

    public static string? GetTextPreview(XElement element, int maxChars = 120)
    {
        if (element.HasElements)
        {
            return null;
        }

        return NormalizeTextPreview(element.Value, maxChars);
    }

    public static string? GetTextPreview(string? value, int maxChars = 120)
        => NormalizeTextPreview(value ?? string.Empty, maxChars);

    public static (int line, int column) GetLineColumn(string text, int offset)
    {
        if (offset < 0)
        {
            offset = 0;
        }

        if (offset > text.Length)
        {
            offset = text.Length;
        }

        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        int column = offset - lineStart + 1;
        return (line, column);
    }

    private static BackendParseResult ParseWithXDocument(string filePath)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            XDocument document = XDocument.Load(
                filePath,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            if (document.Root is null)
            {
                sw.Stop();
                return new BackendParseResult(
                    Backend: "xdocument",
                    Success: false,
                    StrictWellFormed: true,
                    DurationMs: (int)sw.ElapsedMilliseconds,
                    Document: null,
                    Error: new CommandError("invalid_xml", $"XML file '{filePath}' has no root element."));
            }

            XElement[] all = document.Root.DescendantsAndSelf().ToArray();
            ParsedXmlElement[] parsed = all.Select(e =>
            {
                (int line, int column)? li = TryGetLineInfo(e);
                return new ParsedXmlElement(
                    Name: e.Name.LocalName,
                    Path: BuildElementPath(e),
                    Depth: e.Ancestors().Count(),
                    Line: li?.line ?? 0,
                    Column: li?.column ?? 0,
                    Value: e.Value,
                    HasChildElements: e.HasElements,
                    TextPreview: GetTextPreview(e),
                    Attributes: e.Attributes().Select(a => new KeyValuePair<string, string>(a.Name.LocalName, a.Value)).ToArray());
            }).ToArray();

            ParsedXmlDocument model = new(
                Backend: "xdocument",
                RootName: document.Root.Name.LocalName,
                Elements: parsed);

            sw.Stop();
            return new BackendParseResult(
                Backend: "xdocument",
                Success: true,
                StrictWellFormed: true,
                DurationMs: (int)sw.ElapsedMilliseconds,
                Document: model,
                Error: null);
        }
        catch (XmlException ex)
        {
            sw.Stop();
            return new BackendParseResult(
                Backend: "xdocument",
                Success: false,
                StrictWellFormed: true,
                DurationMs: (int)sw.ElapsedMilliseconds,
                Document: null,
                Error: new CommandError(
                    "invalid_xml",
                    $"Failed to parse XML file '{filePath}'.",
                    new { ex.LineNumber, ex.LinePosition, ex.Message }));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new BackendParseResult(
                Backend: "xdocument",
                Success: false,
                StrictWellFormed: true,
                DurationMs: (int)sw.ElapsedMilliseconds,
                Document: null,
                Error: new CommandError(
                    "xml_load_failed",
                    $"Could not read XML file '{filePath}'.",
                    new { ex.Message }));
        }
    }

    private static BackendParseResult ParseWithLanguageXml(string filePath)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string text = File.ReadAllText(filePath);
            XmlDocumentSyntax document = Parser.ParseText(text);
            IXmlElement? root = document.Root;

            if (root is null)
            {
                sw.Stop();
                return new BackendParseResult(
                    Backend: "language_xml",
                    Success: false,
                    StrictWellFormed: false,
                    DurationMs: (int)sw.ElapsedMilliseconds,
                    Document: null,
                    Error: new CommandError("parse_failed", $"language_xml parser could not identify a root element for '{filePath}'."));
            }

            List<ParsedXmlElement> elements = new();
            VisitLanguageElement(root, parentPath: "", depth: 0, text: text, results: elements);

            ParsedXmlDocument model = new(
                Backend: "language_xml",
                RootName: root.Name,
                Elements: elements.ToArray());

            sw.Stop();
            return new BackendParseResult(
                Backend: "language_xml",
                Success: true,
                StrictWellFormed: false,
                DurationMs: (int)sw.ElapsedMilliseconds,
                Document: model,
                Error: null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new BackendParseResult(
                Backend: "language_xml",
                Success: false,
                StrictWellFormed: false,
                DurationMs: (int)sw.ElapsedMilliseconds,
                Document: null,
                Error: new CommandError(
                    "parse_failed",
                    $"language_xml backend failed while parsing '{filePath}'.",
                    new { ex.Message }));
        }
    }

    private static void VisitLanguageElement(
        IXmlElement element,
        string parentPath,
        int depth,
        string text,
        List<ParsedXmlElement> results)
    {
        string path = string.IsNullOrEmpty(parentPath)
            ? "/" + element.Name
            : parentPath + "/" + element.Name;

        (int line, int column) = GetLineColumn(text, element.Start);
        IReadOnlyList<KeyValuePair<string, string>> attributes = element.Attributes.ToArray();
        string? textPreview = element.Elements.Any()
            ? null
            : GetTextPreview(element.Value);
        bool hasChildElements = element.Elements.Any();

        results.Add(new ParsedXmlElement(
            Name: element.Name,
            Path: path,
            Depth: depth,
            Line: line,
            Column: column,
            Value: element.Value ?? string.Empty,
            HasChildElements: hasChildElements,
            TextPreview: textPreview,
            Attributes: attributes));

        foreach (IXmlElement child in element.Elements)
        {
            VisitLanguageElement(child, path, depth + 1, text, results);
        }
    }

    private static string? NormalizeTextPreview(string value, int maxChars)
    {
        string normalized = value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..maxChars] + "...";
    }
}
