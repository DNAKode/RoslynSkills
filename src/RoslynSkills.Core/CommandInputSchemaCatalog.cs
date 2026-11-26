using System.Text.Json.Nodes;
using RoslynSkills.Contracts;

namespace RoslynSkills.Core;

/// <summary>
/// Single descriptor-driven input-schema source of truth shared across Core, CLI (<c>describe-command</c>,
/// schema-driven shorthand coercion/path normalization), and MCP (<c>--mode full</c> and <c>--mode slim</c>).
/// </summary>
public static class CommandInputSchemaCatalog
{
    private static readonly IReadOnlyDictionary<string, CommandInputSchema> Schemas = BuildCatalog();

    public static CommandInputSchema? GetSchema(string commandId)
        => Schemas.TryGetValue(commandId, out CommandInputSchema? schema) ? schema : null;

    public static IReadOnlyDictionary<string, CommandInputSchema> GetAllSchemas() => Schemas;

    public static CommandParameterDescriptor? FindParameter(CommandInputSchema? schema, string normalizedName)
    {
        if (schema is null || string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        foreach (CommandParameterDescriptor param in schema.Parameters)
        {
            if (string.Equals(param.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return param;
            }

            if (param.Aliases is not null)
            {
                foreach (string alias in param.Aliases)
                {
                    if (string.Equals(alias, normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return param;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Projects a <see cref="CommandInputSchema"/> into a standard JSON Schema <see cref="JsonObject"/> for MCP
    /// <c>tools/list</c> (both <c>--mode full</c> and compact <c>--mode slim</c>) and CLI <c>describe-command</c>.
    /// </summary>
    public static JsonObject ToJsonSchemaObject(
        CommandInputSchema schema,
        string? description = null,
        bool includeDescriptions = true,
        IReadOnlySet<string>? allowedParameters = null,
        IReadOnlyDictionary<string, string>? descriptionOverrides = null,
        IReadOnlySet<string>? requiredOverrides = null,
        bool? additionalPropertiesOverride = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? allowedItemProperties = null)
    {
        JsonObject root = new()
        {
            ["type"] = "object",
        };

        bool additionalProperties = additionalPropertiesOverride ?? schema.AdditionalProperties;
        if (additionalPropertiesOverride.HasValue || includeDescriptions)
        {
            root["additionalProperties"] = additionalProperties;
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            root["description"] = description;
        }

        JsonObject properties = new();
        JsonArray required = new();

        foreach (CommandParameterDescriptor param in schema.Parameters)
        {
            if (allowedParameters is not null && !allowedParameters.Contains(param.Name))
            {
                continue;
            }

            string? paramDesc = null;
            if (descriptionOverrides is not null && descriptionOverrides.TryGetValue(param.Name, out string? overrideDesc))
            {
                paramDesc = overrideDesc;
            }
            else if (includeDescriptions && !string.IsNullOrWhiteSpace(param.Description))
            {
                paramDesc = param.Description;
            }

            IReadOnlySet<string>? nestedFilter = allowedItemProperties is not null &&
                                                 allowedItemProperties.TryGetValue(param.Name, out IReadOnlySet<string>? foundFilter)
                ? foundFilter
                : null;
            properties[param.Name] = BuildParameterNode(param, paramDesc, includeDescriptions, nestedFilter);

            bool isRequired = requiredOverrides is not null
                ? requiredOverrides.Contains(param.Name)
                : param.Required;
            if (isRequired)
            {
                required.Add(param.Name);
            }
        }

        if (properties.Count > 0)
        {
            root["properties"] = properties;
        }

        if (required.Count > 0)
        {
            root["required"] = required;
        }

        return root;
    }

    private static JsonObject BuildParameterNode(
        CommandParameterDescriptor param,
        string? description,
        bool includeNestedDescriptions,
        IReadOnlySet<string>? allowedItemProperties = null)
    {
        JsonObject node = new()
        {
            ["type"] = param.Type,
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            node["description"] = description;
        }

        if (param.EnumValues is { Count: > 0 })
        {
            JsonArray arr = new();
            foreach (string v in param.EnumValues)
            {
                arr.Add(v);
            }

            node["enum"] = arr;
        }

        if (param.Minimum.HasValue && includeNestedDescriptions)
        {
            node["minimum"] = param.Minimum.Value;
        }

        if (param.Maximum.HasValue && includeNestedDescriptions)
        {
            node["maximum"] = param.Maximum.Value;
        }

        if (string.Equals(param.Type, "array", StringComparison.Ordinal))
        {
            JsonObject items = new()
            {
                ["type"] = param.ItemType ?? "string",
            };

            if (string.Equals(param.ItemType, "object", StringComparison.Ordinal) && param.ItemProperties is { Count: > 0 })
            {
                JsonObject itemProps = new();
                JsonArray itemReq = new();
                foreach (CommandParameterDescriptor child in param.ItemProperties)
                {
                    if (allowedItemProperties is not null && !allowedItemProperties.Contains(child.Name))
                    {
                        continue;
                    }

                    string? childDesc = includeNestedDescriptions ? child.Description : null;
                    itemProps[child.Name] = BuildParameterNode(child, childDesc, includeNestedDescriptions);
                    if (child.Required)
                    {
                        itemReq.Add(child.Name);
                    }
                }

                items["properties"] = itemProps;
                if (itemReq.Count > 0)
                {
                    items["required"] = itemReq;
                }
            }

            node["items"] = items;
        }

        return node;
    }

    private static CommandParameterDescriptor Str(string name, string desc, bool req = false, bool isPath = false, string[]? enumValues = null, string[]? aliases = null)
        => new(name, "string", desc, Required: req, EnumValues: enumValues, IsPath: isPath, Aliases: aliases);

    private static CommandParameterDescriptor Int(string name, string desc, bool req = false, int? min = null, int? max = null)
        => new(name, "integer", desc, Required: req, Minimum: min, Maximum: max);

    private static CommandParameterDescriptor Num(string name, string desc, bool req = false, int? min = null, int? max = null)
        => new(name, "number", desc, Required: req, Minimum: min, Maximum: max);

    private static CommandParameterDescriptor Bool(string name, string desc, bool req = false)
        => new(name, "boolean", desc, Required: req);

    private static CommandParameterDescriptor StrArr(string name, string desc, bool req = false, bool isPath = false, bool commaList = false, string[]? aliases = null)
        => new(name, "array", desc, Required: req, ItemType: "string", IsPath: isPath, AcceptCommaList: commaList, Aliases: aliases);

    private static CommandParameterDescriptor ObjArr(string name, string desc, CommandParameterDescriptor[] itemProps, bool req = false)
        => new(name, "array", desc, Required: req, ItemType: "object", ItemProperties: itemProps);

    private static CommandParameterDescriptor Obj(string name, string desc, bool req = false)
        => new(name, "object", desc, Required: req);

    private static CommandParameterDescriptor[] WorkspaceScopeParams() =>
    [
        Str("workspace_path", "Optional .sln/.slnx/.csproj/.vbproj or directory workspace path.", isPath: true),
        Str("workspace_handle", "Optional hot workspace handle returned by workspace.preload."),
        Bool("require_workspace", "When true, fail closed if workspace resolution falls back to ad_hoc."),
    ];

    private static CommandParameterDescriptor[] FileAnchorParams(bool required) =>
    [
        Str("file_path", "Path to a C# or VB source file (.cs/.csx/.vb).", req: required, isPath: true),
        Int("line", "1-based line number for symbol anchor.", req: required, min: 1),
        Int("column", "1-based column number for symbol anchor.", req: required, min: 1),
    ];

    private static CommandParameterDescriptor[] SymbolOrFileAnchorParams() =>
    [
        Str("symbol", "Optional Type.Member symbol name (with optional parameter types) resolved across the loaded workspace.", aliases: ["symbol_name"]),
        Str("file_path", "Path to a C# or VB source file (.cs/.csx/.vb). Required unless 'symbol' is provided.", isPath: true),
        Int("line", "1-based line number for symbol anchor.", min: 1),
        Int("column", "1-based column number for symbol anchor.", min: 1),
    ];

    private static IReadOnlyDictionary<string, CommandInputSchema> BuildCatalog()
    {
        Dictionary<string, CommandInputSchema> map = new(StringComparer.OrdinalIgnoreCase);

        void Register(string id, params CommandParameterDescriptor[] parameters)
            => map[id] = new CommandInputSchema(parameters, AdditionalProperties: true);

        static CommandParameterDescriptor[] Concat(params IEnumerable<CommandParameterDescriptor>[] groups)
            => groups.SelectMany(g => g).ToArray();

        // System
        Register("system.ping");

        // Navigation
        Register("nav.find_symbol", Concat(
        [
            Str("file_path", "Path to a C# or VB source file (.cs/.csx/.vb).", req: true, isPath: true),
            Str("symbol_name", "Symbol name to search for.", req: true),
            Bool("brief", "Return compact result payload."),
            Int("max_results", "Maximum matches to return.", min: 1),
            Int("context_lines", "Context lines around each match.", min: 0),
            Bool("declarations_only", "When true, return only declaration matches."),
            Bool("first_declaration", "When true, return first declaration match."),
            Bool("snippet_single_line", "Render context snippets as a single line."),
            Int("max_snippet_chars", "Maximum snippet character count (0 means no limit).", min: 0),
        ], WorkspaceScopeParams()));

        Register("nav.find_symbol_batch", Concat(
        [
            ObjArr("queries", "Array of file-scoped symbol queries.",
            [
                Str("file_path", "Path to a C# or VB source file.", req: true, isPath: true),
                Str("symbol_name", "Symbol name to search for.", req: true),
                Str("label", "Optional correlation label."),
            ], req: true),
            Bool("continue_on_error", "Continue remaining queries after an error."),
            Bool("brief", "Default brief mode."),
            Int("max_results", "Default max results per query.", min: 1),
            Int("context_lines", "Default context lines.", min: 0),
            Bool("declarations_only", "Default declaration-only mode."),
            Bool("first_declaration", "Default first-declaration mode."),
            Bool("snippet_single_line", "Default single-line snippet mode."),
            Int("max_snippet_chars", "Default max snippet characters.", min: 0),
        ], WorkspaceScopeParams()));

        Register("nav.go_to_definition", Concat(SymbolOrFileAnchorParams(),
        [
            Bool("brief", "Return compact definition locations without source snippets."),
            Int("context_lines", "Number of context lines around each source definition.", min: 0),
        ], WorkspaceScopeParams()));

        Register("nav.find_references", Concat(SymbolOrFileAnchorParams(),
        [
            StrArr("symbols", "Optional batch of Type.Member symbol queries resolved in one pass.", commaList: true),
            Bool("include_declaration", "Include symbol declaration locations in matches."),
            Bool("include_generated", "Include generated source files."),
            Int("max_results", "Maximum reference matches to return per symbol.", min: 1),
            Int("context_lines", "Number of context lines around each match.", min: 0),
        ], WorkspaceScopeParams()));

        Register("nav.find_invocations", Concat(SymbolOrFileAnchorParams(),
        [
            StrArr("symbols", "Optional batch of Type.Member symbol queries resolved in one compilation pass.", commaList: true),
            Bool("brief", "Return compact match payload."),
            Int("max_results", "Maximum invocation matches to return per symbol.", min: 1),
            Int("context_lines", "Context lines around each match.", min: 0),
            Bool("include_object_creations", "Include object creation calls for constructor or type targets."),
            Bool("include_generated", "Include generated source files."),
        ], WorkspaceScopeParams()));

        Register("nav.find_implementations", Concat(SymbolOrFileAnchorParams(),
        [
            Int("max_results", "Maximum implementation matches to return.", min: 1),
        ], WorkspaceScopeParams()));

        Register("nav.find_overrides", Concat(SymbolOrFileAnchorParams(),
        [
            Int("max_results", "Maximum override matches to return.", min: 1),
        ], WorkspaceScopeParams()));

        Register("nav.call_hierarchy", Concat(SymbolOrFileAnchorParams(),
        [
            Str("direction", "Hierarchy direction: incoming, outgoing, or both.", enumValues: ["incoming", "outgoing", "both"]),
            Int("max_depth", "Maximum recursive depth (default 1, range 1..8).", min: 1, max: 8),
            Int("max_nodes", "Maximum nodes returned.", min: 1),
            Int("max_edges", "Maximum edges returned.", min: 1),
            Int("context_lines", "Context lines around call sites.", min: 0),
            Bool("brief", "Return compact node/edge payload."),
            Bool("include_object_creations", "Include object creation edges."),
            Bool("include_external", "Include external metadata symbols."),
            Bool("include_generated", "Include generated source files."),
        ], WorkspaceScopeParams()));

        Register("nav.call_path", Concat(
        [
            Str("source_symbol", "Optional source method symbol (Type.Method) instead of source_file_path/line/column.", aliases: ["source_symbol_name"]),
            Str("target_symbol", "Optional target method symbol (Type.Method) instead of target_file_path/line/column.", aliases: ["target_symbol_name"]),
            Str("source_file_path", "Path to source file containing source method anchor.", isPath: true),
            Int("source_line", "1-based line number for source method anchor.", min: 1),
            Int("source_column", "1-based column number for source method anchor.", min: 1),
            Str("target_file_path", "Path to source file containing target method anchor.", isPath: true),
            Int("target_line", "1-based line number for target method anchor.", min: 1),
            Int("target_column", "1-based column number for target method anchor.", min: 1),
            Int("max_depth", "Maximum BFS depth for path search.", min: 1),
            Int("max_nodes", "Maximum visited nodes during path search.", min: 1),
            Int("max_graph_edges", "Maximum edges collected for graph construction.", min: 1),
            Int("context_lines", "Context lines around call sites.", min: 0),
            Bool("brief", "Return compact path payload."),
            Bool("include_object_creations", "Include object creation edges."),
            Bool("include_external", "Include external metadata symbols."),
            Bool("include_generated", "Include generated source files."),
        ], WorkspaceScopeParams()));

        // Context / Mindspace
        CommandParameterDescriptor[] searchQueryItemProps =
        [
            Str("label", "Optional query correlation label."),
            Str("pattern", "Single literal or regex pattern."),
            StrArr("patterns", "Multiple literal or regex patterns."),
            Str("mode", "Search mode: literal or regex.", enumValues: ["literal", "regex"]),
            Str("syntax_filter", "Optional AST context filter.", enumValues: ["any", "code", "comments", "strings", "comments_and_strings", "identifiers", "declarations", "invocations"]),
            Bool("whole_word", "Match whole identifier/word boundaries in literal mode."),
            Bool("case_sensitive", "Case-sensitive matching."),
            Str("file_path", "Optional single-file scope.", isPath: true),
            StrArr("roots", "Optional directory/file search roots.", isPath: true),
            StrArr("include_globs", "Include glob filters.", commaList: true),
            StrArr("exclude_globs", "Exclude glob filters.", commaList: true),
            Int("max_results", "Maximum matches to return.", min: 1),
            Int("context_lines", "Context lines around each match.", min: 0),
        ];

        Register("ctx.search_text", Concat(
        [
            Str("pattern", "Single search pattern (literal or regex)."),
            StrArr("patterns", "Multiple search patterns."),
            ObjArr("queries", "Optional batched search queries executed in one round trip.", searchQueryItemProps),
            Str("mode", "Search mode: literal or regex.", enumValues: ["literal", "regex"]),
            Str("syntax_filter", "Optional Roslyn AST context filter (code excludes comments/strings).", enumValues: ["any", "code", "comments", "strings", "comments_and_strings", "identifiers", "declarations", "invocations"], aliases: ["ast_filter"]),
            Bool("whole_word", "Match whole word boundaries in literal mode."),
            Bool("case_sensitive", "Case-sensitive matching."),
            Bool("include_syntax_context", "Attach syntax_context and enclosing_symbol to matches."),
            Str("file_path", "Optional single-file search scope.", isPath: true),
            StrArr("roots", "Directory/file search roots.", isPath: true),
            StrArr("include_globs", "Include glob filters.", commaList: true, aliases: ["file_glob", "glob"]),
            StrArr("exclude_globs", "Exclude glob filters.", commaList: true),
            Bool("include_generated", "Include generated files."),
            Int("max_results", "Maximum matches to return.", min: 1),
            Int("max_files", "Maximum files to scan.", min: 1),
            Int("context_lines", "Context lines around each match.", min: 0),
            Int("preview_max_chars", "Maximum characters per match preview.", min: 40),
            Int("max_returned_matches", "Cap returned rows in brief broad results.", min: 1),
            Bool("brief", "Return compact match payload."),
        ], WorkspaceScopeParams()));

        CommandParameterDescriptor[] readWindowItemProps =
        [
            Str("label", "Optional window label."),
            Str("file_path", "Path to file to read.", isPath: true),
            Int("start_line", "1-based inclusive start line.", min: 1),
            Int("end_line", "1-based inclusive end line.", min: 1),
            Int("line_count", "Number of lines to return from start_line (default 100).", min: 1),
            Int("anchor_line", "Optional 1-based anchor line centered with context_before/context_after.", min: 1),
            Int("context_before", "Lines before anchor_line.", min: 0),
            Int("context_after", "Lines after anchor_line.", min: 0),
        ];

        Register("ctx.read_window",
        [
            Str("file_path", "Path to file to read (or default file for batched windows).", isPath: true),
            Int("start_line", "1-based inclusive start line (default 1).", min: 1),
            Int("end_line", "1-based inclusive end line.", min: 1),
            Int("line_count", "Number of lines to return when end_line is omitted (default 100).", min: 1),
            Int("anchor_line", "Optional 1-based anchor line centered with context_before/context_after.", min: 1),
            Int("context_before", "Lines before anchor_line (default 15).", min: 0),
            Int("context_after", "Lines after anchor_line (default 30).", min: 0),
            ObjArr("windows", "Optional batch of line windows across one or more files in one round trip.", readWindowItemProps),
            Bool("include_line_numbers", "Prefix lines with 1-based line numbers (default true)."),
            Bool("include_content_hash", "Include file SHA-256 hash for edit.transaction preconditions (default true)."),
            Bool("include_enclosing_outline", "Include enclosing syntax symbol name (default true)."),
            Int("max_chars", "Maximum characters per window.", min: 200),
            Bool("brief", "Compact output."),
            Str("workspace_handle", "Optional hot workspace handle for zero-disk SourceText lookup."),
        ]);

        Register("ctx.symbol_context", Concat(SymbolOrFileAnchorParams(),
        [
            Bool("brief", "Return compact mindspace payload (default true)."),
            Bool("include_definition", "Include zero-disk declaration source window (default true)."),
            Bool("include_references", "Include solution-wide semantic references (default true)."),
            Bool("include_callers", "Include 1-hop incoming callers including interface-dispatched callers (default true)."),
            Bool("include_callees", "Include 1-hop outgoing in-source callees."),
            Bool("include_implementations", "Include implementing types/members and overrides (default true)."),
            Bool("include_diagnostics", "Include compiler diagnostics on declaring file (default true)."),
            Bool("include_line_numbers", "Prefix definition source with line numbers (default true)."),
            Int("max_definition_lines", "Maximum definition source lines returned.", min: 1, max: 500),
            Int("definition_context_before", "Context lines before declaration.", min: 0),
            Int("definition_context_after", "Context lines after declaration.", min: 0),
            Int("max_references", "Maximum references returned.", min: 0, max: 200),
            Int("max_callers", "Maximum incoming callers returned.", min: 0, max: 200),
            Int("max_callees", "Maximum outgoing callees returned.", min: 0, max: 200),
            Int("max_implementations", "Maximum implementations returned.", min: 0, max: 200),
            Int("max_diagnostics", "Maximum declaring-file diagnostics returned.", min: 0, max: 200),
            Int("context_lines", "Reference snippet context lines.", min: 0),
        ], WorkspaceScopeParams()));

        Register("ctx.file_outline",
        [
            Str("file_path", "Path to a C# or VB source file (.cs/.csx/.vb).", req: true, isPath: true),
            Bool("include_usings", "Include using/import directives."),
            Bool("include_members", "Include member outlines for each type."),
            Int("max_types", "Maximum type declarations returned.", min: 1),
            Int("max_members", "Maximum member declarations returned.", min: 1),
            Str("type_name_contains", "Case-insensitive type-name substring filter."),
            Str("member_name_contains", "Case-insensitive member-name/signature substring filter."),
            Str("workspace_handle", "Optional hot workspace handle for cached SyntaxTree lookup."),
        ]);

        Register("ctx.member_source", Concat(
        [
            Str("file_path", "Path to a C# or VB source file (.cs/.csx/.vb).", req: true, isPath: true),
            Int("line", "1-based line number for member anchor.", min: 1),
            Int("column", "1-based column number for member anchor.", min: 1),
            Str("member_name", "Optional exact member name anchor."),
            Str("mode", "Extraction mode: member or body.", enumValues: ["member", "body"]),
            Bool("brief", "Compact output; omits source text by default."),
            Bool("include_source_text", "Include extracted source snippet."),
            Bool("include_edit_target_text", "Include exact edit target text."),
            Bool("include_line_numbers", "Prefix snippet lines with line numbers."),
            Bool("include_trivia", "Include leading/trailing trivia when computing spans."),
            Str("focus_text", "Optional literal text to center the returned source window."),
            Int("context_lines_before", "Additional lines before extracted span.", min: 0),
            Int("context_lines_after", "Additional lines after extracted span.", min: 0),
            Int("max_chars", "Maximum characters returned for source text.", min: 200),
        ], WorkspaceScopeParams()));

        Register("ctx.symbol_envelope", Concat(FileAnchorParams(required: true),
        [
            Int("context_lines", "Context lines around anchor.", min: 0),
        ], WorkspaceScopeParams()));

        Register("ctx.changed_files",
        [
            Str("repo_root", "Optional repository root path.", isPath: true, aliases: ["root"]),
            Bool("include_untracked", "Include untracked files."),
            Bool("include_staged", "Include staged files."),
            Bool("include_unstaged", "Include unstaged files."),
            Bool("cs_only", "Filter to C#/VB source files."),
            Int("max_files", "Maximum files returned.", min: 1),
            Bool("brief", "Return compact output."),
        ]);

        Register("ctx.call_chain_slice", Concat(SymbolOrFileAnchorParams(),
        [
            Str("direction", "Traversal direction: incoming, outgoing, or both.", enumValues: ["incoming", "outgoing", "both"]),
            Int("max_depth", "Maximum traversal depth.", min: 1, max: 8),
            Int("max_nodes", "Maximum nodes returned.", min: 1),
            Int("max_edges", "Maximum edges returned.", min: 1),
            Int("context_lines", "Context lines around call sites.", min: 0),
            Bool("brief", "Return compact payload."),
            Bool("include_object_creations", "Include object creation edges."),
            Bool("include_external", "Include external metadata symbols."),
            Bool("include_generated", "Include generated source files."),
        ], WorkspaceScopeParams()));

        Register("ctx.dependency_slice", Concat(
        [
            Str("workspace_path", "Workspace path (.sln/.slnx/.csproj/dir) used as analysis root.", isPath: true),
            Str("workspace_handle", "Optional hot workspace handle."),
            Str("root_namespace", "Optional namespace prefix filter."),
            Bool("include_external", "Include external namespace dependencies."),
            Bool("include_generated", "Include generated source files."),
            Int("max_files", "Maximum files analyzed.", min: 1),
            Int("max_edges", "Maximum dependency edges returned.", min: 1),
            Bool("brief", "Return compact payload."),
        ]));

        Register("query.batch",
        [
            ObjArr("queries", "Ordered array of read-only queries to execute in one request.",
            [
                Str("command_id", "Supported read-only RoslynSkills command ID.", req: true),
                Obj("input", "Command input object.", req: true),
            ], req: true),
            Bool("continue_on_error", "Continue remaining queries after an error (default true)."),
            Str("workspace_handle", "Optional hot workspace handle inherited by sub-queries."),
        ]);

        // Analysis
        Register("analyze.control_flow_graph", Concat(FileAnchorParams(required: true),
        [
            Bool("brief", "Return compact CFG payload."),
            Int("max_blocks", "Maximum CFG blocks returned.", min: 1),
            Int("max_edges", "Maximum CFG edges returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("analyze.dataflow_slice", Concat(FileAnchorParams(required: true),
        [
            Bool("brief", "Return compact symbol sets."),
            Int("max_symbols", "Maximum symbols returned per set.", min: 1),
        ], WorkspaceScopeParams()));

        Register("analyze.unused_private_symbols",
        [
            Str("workspace_path", "Workspace path (.sln/.slnx/.csproj/.vbproj/file/directory).", req: true, isPath: true),
            Str("workspace_handle", "Optional hot workspace handle."),
            Bool("include_generated", "Include generated source files."),
            Int("max_files", "Maximum files analyzed.", min: 1),
            Int("max_symbols", "Maximum unused-symbol results returned.", min: 1),
            Bool("brief", "Return compact result payload."),
        ]);

        Register("analyze.dependency_violations",
        [
            Str("workspace_path", "Workspace path (.sln/.slnx/.csproj/.vbproj/file/directory).", req: true, isPath: true),
            Str("workspace_handle", "Optional hot workspace handle."),
            StrArr("layers", "Ordered namespace layer prefixes (minimum two).", req: true),
            Str("direction", "Layer direction rule: toward_end or toward_start.", enumValues: ["toward_end", "toward_start"]),
            Bool("ignore_same_namespace", "Ignore dependencies within the same namespace."),
            Bool("include_generated", "Include generated source files."),
            Int("max_files", "Maximum files analyzed.", min: 1),
            Int("max_violations", "Maximum violation results returned.", min: 1),
            Bool("brief", "Return compact result payload."),
        ]);

        Register("analyze.impact_slice", Concat(SymbolOrFileAnchorParams(),
        [
            Bool("include_references", "Include reference locations for the anchor symbol."),
            Bool("include_callers", "Include caller sites for method anchors."),
            Bool("include_callees", "Include callee sites from anchored method body."),
            Bool("include_overrides", "Include override relationships."),
            Bool("include_implementations", "Include interface/type implementation matches."),
            Int("max_references", "Maximum reference matches returned.", min: 1),
            Int("max_callers", "Maximum caller matches returned.", min: 1),
            Int("max_callees", "Maximum callee matches returned.", min: 1),
            Int("max_related", "Maximum override/implementation matches returned.", min: 1),
            Bool("brief", "Return compact result payload."),
        ], WorkspaceScopeParams()));

        Register("analyze.override_coverage",
        [
            Str("workspace_path", "Workspace path (.sln/.slnx/.csproj/.vbproj/file/directory).", req: true, isPath: true),
            Str("workspace_handle", "Optional hot workspace handle."),
            Num("coverage_threshold", "Minimum override coverage ratio (0..1).", min: 0, max: 1),
            Int("min_derived_types", "Minimum derived-type count.", min: 1),
            Bool("include_generated", "Include generated source files."),
            Int("max_files", "Maximum files analyzed.", min: 1),
            Int("max_members", "Maximum finding entries returned.", min: 1),
            Bool("brief", "Return compact result payload."),
        ]);

        Register("analyze.async_risk_scan",
        [
            Str("workspace_path", "Workspace path (.sln/.slnx/.csproj/.vbproj/file/directory).", req: true, isPath: true),
            Str("workspace_handle", "Optional hot workspace handle."),
            StrArr("severity_filter", "Optional severities to include (warning, info).", commaList: true),
            Bool("include_generated", "Include generated source files."),
            Int("max_files", "Maximum files analyzed.", min: 1),
            Int("max_findings", "Maximum findings returned.", min: 1),
            Bool("brief", "Return compact result payload."),
        ]);

        // Diagnostics
        Register("diag.get_file_diagnostics", Concat(
        [
            Str("file_path", "Path to a C# or VB source file.", req: true, isPath: true),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
            StrArr("severity_filter", "Filter by severity (error, warning, info).", commaList: true),
            StrArr("diagnostic_ids", "Filter to specific diagnostic IDs.", commaList: true),
            Bool("brief", "Return compact diagnostics payload."),
        ], WorkspaceScopeParams()));

        Register("diag.get_after_edit", Concat(
        [
            Str("file_path", "Path to a C# or VB source file.", req: true, isPath: true),
            Str("proposed_content", "Optional proposed full file content to diagnose in memory."),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        CommandParameterDescriptor[] snapshotParams =
        [
            Str("directory_path", "Optional directory path scoping projects/files to diagnose.", isPath: true),
            Str("workspace_path", "Optional .sln/.slnx/.csproj/.vbproj workspace path.", isPath: true),
            Str("workspace_handle", "Optional hot workspace handle."),
            StrArr("file_paths", "Optional explicit file list to scope diagnostics.", isPath: true),
            Str("mode", "Output profile: compact, raw, or guided.", enumValues: ["compact", "raw", "guided"]),
            Bool("brief", "Return compact summary payload."),
            Bool("include_diagnostics", "Include individual diagnostic entries."),
            StrArr("severity_filter", "Filter severities (error, warning, info).", commaList: true),
            StrArr("diagnostic_ids", "Filter to specific diagnostic IDs.", commaList: true),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
            Int("max_diagnostics_per_file", "Maximum diagnostics per file.", min: 1),
            Int("max_files", "Maximum files evaluated.", min: 1),
            Int("max_files_in_output", "Maximum files listed in output.", min: 1),
            Int("max_top_diagnostics", "Maximum top diagnostic buckets.", min: 1),
            Bool("recursive", "Recurse subdirectories."),
            Bool("include_generated", "Include generated files."),
            Bool("require_workspace", "Fail closed if workspace resolution falls back to ad_hoc."),
        ];

        Register("diag.get_workspace_snapshot", snapshotParams);
        Register("diag.get_solution_snapshot", snapshotParams);

        Register("diag.diff", Concat(
        [
            Str("file_path", "Path to a C# or VB source file.", req: true, isPath: true),
            Str("proposed_content", "Proposed file content to compare diagnostics against current file.", req: true),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        // Edits
        Register("edit.rename_symbol", Concat(SymbolOrFileAnchorParams(),
        [
            Str("new_name", "New valid C# identifier name.", req: true),
            Bool("apply", "When false, run as dry-run without writing files (default true)."),
            Int("max_diagnostics", "Maximum post-edit diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.change_signature", Concat(SymbolOrFileAnchorParams(),
        [
            Str("parameters", "New C# parameter list (e.g. 'int count, string label').", req: true),
            Str("return_type", "Optional new return type."),
            Str("new_name", "Optional new method identifier name."),
            Bool("apply", "When false, run as dry-run without writing files (default true)."),
            Int("max_diagnostics", "Maximum post-edit diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.add_member", Concat(
        [
            Str("file_path", "Path to target C# source file.", req: true, isPath: true),
            Str("type_name", "Target type declaration name.", req: true),
            Str("member_declaration", "Full C# member declaration code to add.", req: true),
            Bool("apply", "When false, run as dry-run (default true)."),
            Int("max_diagnostics", "Maximum post-edit diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.replace_member_body", Concat(FileAnchorParams(required: true),
        [
            Str("new_body", "Replacement C# block or expression body.", req: true),
            Bool("apply", "When false, run as dry-run (default true)."),
            Int("max_diagnostics", "Maximum post-edit diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.update_usings", Concat(
        [
            Str("file_path", "Path to target C# source file.", req: true, isPath: true),
            StrArr("add_usings", "Namespace using directives to add.", commaList: true),
            StrArr("remove_usings", "Namespace using directives to remove.", commaList: true),
            Bool("sort", "Sort using directives."),
            Bool("apply", "When false, run as dry-run (default true)."),
            Int("max_diagnostics", "Maximum post-edit diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.apply_code_fix", Concat(
        [
            Str("file_path", "Path to a C# source file.", req: true, isPath: true),
            Str("diagnostic_id", "Optional Roslyn diagnostic id (e.g. CS0103, CS0246, CS0535)."),
            Int("line", "Optional 1-based line number of the diagnostic.", min: 1),
            Int("column", "Optional 1-based column number of the diagnostic.", min: 1),
            Str("fix_title", "Optional exact Roslyn code-fix title to apply."),
            Bool("apply", "When false, inspect offered fixes without writing files."),
            Int("max_diagnostics", "Maximum post-edit diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.create_file", Concat(
        [
            Str("file_path", "Path of the file to create.", req: true, isPath: true),
            Str("content", "Initial file content; defaults to empty file."),
            Bool("overwrite", "Overwrite if file already exists."),
            Bool("create_directories", "Create missing parent directories."),
            Bool("apply", "When false, run as dry-run."),
            Bool("include_diagnostics", "Evaluate C# diagnostics for .cs/.csx content."),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.replace_text", Concat(
        [
            Str("file_path", "Path to target file.", req: true, isPath: true),
            Str("old_text", "Exact existing text to replace.", req: true),
            Str("new_text", "Replacement text.", req: true),
            Bool("replace_all", "Replace all occurrences instead of requiring a unique match."),
            Bool("apply", "When false, run as dry-run (default true)."),
            Bool("include_diagnostics", "Evaluate diagnostics after edit."),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.replace_in_member", Concat(
        [
            Str("file_path", "Path to target C# file.", req: true, isPath: true),
            Str("member_name", "Exact target member name (or provide line/column)."),
            Int("line", "Optional 1-based line number for member anchor.", min: 1),
            Int("column", "Optional 1-based column number for member anchor.", min: 1),
            Str("mode", "Scope within member: member or body.", enumValues: ["member", "body"]),
            Str("old_text", "Exact existing text within member to replace.", req: true),
            Str("new_text", "Replacement text.", req: true),
            Bool("replace_all", "Replace all occurrences inside the member."),
            Int("preview_chars", "Maximum diff preview characters.", min: 40),
            Bool("apply", "When false, run as dry-run (default true)."),
            Bool("include_diagnostics", "Evaluate diagnostics after edit."),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        Register("edit.insert_text", Concat(
        [
            Str("file_path", "Path to target file.", req: true, isPath: true),
            Str("anchor_text", "Exact unique anchor text.", req: true),
            Str("insert_text", "Text to insert before or after anchor.", req: true),
            Str("position", "Insert position relative to anchor: before or after.", enumValues: ["before", "after"]),
            Bool("apply", "When false, run as dry-run (default true)."),
            Bool("include_diagnostics", "Evaluate diagnostics after edit."),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ], WorkspaceScopeParams()));

        CommandParameterDescriptor[] txOpProps =
        [
            Str("kind", "Operation kind: replace_text, insert_text, replace_span, set_content.", req: true, enumValues: ["replace_text", "insert_text", "replace_span", "set_content"], aliases: ["operation", "type"]),
            Str("file_path", "Target file path (overrides top-level file_path).", isPath: true),
            Str("expected_sha256", "Optional expected SHA-256 hex digest precondition for the file before editing.", aliases: ["expected_content_hash"]),
            Str("old_text", "Exact text to match (replace_text)."),
            Str("new_text", "Replacement text (replace_text, replace_span)."),
            Str("expected_text", "Expected span content guard (replace_span)."),
            Str("anchor_text", "Exact anchor text (insert_text)."),
            Str("insert_text", "Text to insert (insert_text)."),
            Str("position", "Insert position: before or after (insert_text).", enumValues: ["before", "after"]),
            Int("start_line", "1-based start line (replace_span).", min: 1),
            Int("start_column", "1-based start column (replace_span).", min: 1),
            Int("end_line", "1-based end line (replace_span).", min: 1),
            Int("end_column", "1-based end column (replace_span).", min: 1),
            Int("span_start", "0-based character offset start (replace_span).", min: 0),
            Int("span_length", "Character span length (replace_span).", min: 0),
            Int("span_end", "0-based exclusive character span end (replace_span).", min: 0),
            Str("new_content", "Full replacement file content (set_content)."),
            Bool("replace_all", "Replace all occurrences (replace_text)."),
        ];

        CommandParameterDescriptor[] txParams = Concat(
        [
            ObjArr("operations", "Ordered list of file edit operations executed atomically.", txOpProps, req: true),
            Str("file_path", "Optional default file path inherited by operations.", isPath: true),
            Str("expected_sha256", "Optional expected SHA-256 hex digest precondition for top-level file_path.", aliases: ["expected_content_hash"]),
            Obj("expected_file_hashes", "Optional map of file_path -> expected SHA-256 hex digest preconditions."),
            Bool("apply", "When false, validate and diagnose in memory without writing disk files (default true)."),
            Bool("atomic", "When true (default), write zero files if any operation or precondition fails."),
            Bool("continue_on_error", "Continue validating later operations after an error."),
            Bool("include_diagnostics", "Evaluate post-edit solution diagnostics (default true)."),
            Int("max_diagnostics", "Maximum post-edit diagnostics returned.", min: 1),
        ], WorkspaceScopeParams());

        Register("edit.transaction", txParams);
        Register("edit.batch_exact", txParams);

        Register("edit.claim",
        [
            Str("operation", "Claim action: status, claim, or release.", req: true, enumValues: ["status", "claim", "release"]),
            StrArr("paths", "File paths to claim or release.", isPath: true),
            Str("claim_id", "Claim identifier to release."),
            Str("repo_root", "Optional repository root.", isPath: true),
            Str("owner", "Claim owner label."),
            Str("reason", "Reason for claim."),
            Int("ttl_minutes", "Claim TTL in minutes.", min: 1, max: 1440),
            Bool("force", "Force claim/release override."),
        ]);

        // Repair
        Register("repair.propose_from_diagnostics", Concat(
        [
            Str("file_path", "Path to a C# source file.", req: true, isPath: true),
            Int("max_proposals", "Maximum repair proposals returned.", min: 1),
            Int("max_diagnostics", "Maximum diagnostics inspected.", min: 1),
        ], WorkspaceScopeParams()));

        Register("repair.apply_plan", Concat(
        [
            ObjArr("steps", "Sequence of repair steps ({ operation_id, input }).",
            [
                Str("operation_id", "Target edit command ID.", req: true),
                Obj("input", "Step input payload.", req: true),
            ]),
            Obj("plan", "Alternative container object with 'steps' array."),
            Str("file_path", "Optional default file path inherited by steps.", isPath: true),
            Bool("stop_on_error", "Stop executing remaining steps after first error (default true)."),
        ], WorkspaceScopeParams()));

        // Workspace lifecycle
        CommandParameterDescriptor[] preloadParams =
        [
            Str("workspace_path", "Path to .sln, .slnx, .csproj, or .vbproj.", req: true, isPath: true),
            Str("mode", "Refresh mode: balanced or strict.", enumValues: ["balanced", "strict"]),
            Bool("include_generated", "Include generated source files."),
            Bool("require_solution", "Require a .sln/.slnx solution rather than single project."),
            Bool("wait_warmup", "Wait for background compilation/index warmup to complete before returning."),
            Int("wait_warmup_timeout_ms", "Timeout in ms when wait_warmup=true (default 180000).", min: 100, max: 600_000),
            Int("max_files", "Maximum files to load.", min: 1),
        ];

        Register("workspace.preload", preloadParams);
        Register("workspace.use", preloadParams);

        Register("workspace.status",
        [
            Str("workspace_handle", "Loaded workspace handle.", req: true),
            Bool("wait_warmup", "Wait for background warmup to finish before reporting status."),
            Int("wait_warmup_timeout_ms", "Timeout in ms when wait_warmup=true.", min: 100, max: 600_000),
        ]);

        Register("workspace.refresh",
        [
            Str("workspace_handle", "Loaded workspace handle.", req: true),
            Str("mode", "Refresh mode: auto, incremental, or reload.", enumValues: ["auto", "incremental", "reload"]),
        ]);

        Register("workspace.close",
        [
            Str("workspace_handle", "Loaded workspace handle to close.", req: true),
        ]);

        Register("workspace.list");

        // Sessions
        Register("session.open",
        [
            Str("file_path", "Path to a C# source file (.cs/.csx).", req: true, isPath: true),
            Str("session_id", "Optional session identifier."),
            Int("max_diagnostics", "Maximum diagnostics returned in snapshot.", min: 1),
        ]);

        Register("session.set_content",
        [
            Str("session_id", "Active session identifier.", req: true),
            Str("new_content", "Full replacement content for the session buffer.", req: true),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ]);

        CommandParameterDescriptor[] sessionEditItemProps =
        [
            Int("start_line", "1-based start line.", req: true, min: 1),
            Int("start_column", "1-based start column.", req: true, min: 1),
            Int("end_line", "1-based end line.", req: true, min: 1),
            Int("end_column", "1-based end column.", req: true, min: 1),
            Str("new_text", "Replacement text.", req: true),
        ];

        Register("session.apply_text_edits",
        [
            Str("session_id", "Active session identifier.", req: true),
            ObjArr("edits", "Line/column range text edits to apply.", sessionEditItemProps, req: true),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ]);

        Register("session.apply_and_commit",
        [
            Str("session_id", "Active session identifier.", req: true),
            ObjArr("edits", "Line/column range text edits to apply before committing.", sessionEditItemProps, req: true),
            Bool("keep_open", "Keep session open after commit."),
            Bool("require_disk_unchanged", "Fail if disk file changed since session open."),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ]);

        Register("session.get_diagnostics",
        [
            Str("session_id", "Active session identifier.", req: true),
            Int("max_diagnostics", "Maximum diagnostics returned.", min: 1),
        ]);

        Register("session.status",
        [
            Str("session_id", "Active session identifier.", req: true),
            Bool("include_content", "Include current buffer content."),
        ]);

        Register("session.diff",
        [
            Str("session_id", "Active session identifier.", req: true),
            Int("max_changes", "Maximum changed line entries returned.", min: 1),
        ]);

        Register("session.commit",
        [
            Str("session_id", "Active session identifier.", req: true),
            Bool("keep_open", "Keep session open after commit."),
            Bool("require_disk_unchanged", "Fail if disk file changed since session open."),
        ]);

        Register("session.close",
        [
            Str("session_id", "Active session identifier.", req: true),
        ]);

        return map;
    }
}
