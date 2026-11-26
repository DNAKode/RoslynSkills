using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynSkills.Core.Commands;

public sealed class SearchTextCommand : IAgentCommand
{
    private const int MaxBatchedQueries = 32;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly HashSet<string> SupportedSyntaxFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        "any",
        "code",
        "comments",
        "strings",
        "comments_and_strings",
        "non_code",
        "identifiers",
        "declarations",
        "invocations",
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.search_text",
        Summary: "Search literal/regex patterns via the hot workspace overlay trigram/token index (zero disk reads when warm), with optional Roslyn AST syntax filters (code/comments/strings/identifiers/declarations/invocations) and batched queries.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false,
        RunsOnHotWorkspace: true,
        HotWorkspaceOptional: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();

        if (input.TryGetProperty("queries", out JsonElement queriesProp) &&
            queriesProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (queriesProp.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new CommandError("invalid_input", "Property 'queries' must be an array of query objects."));
                return errors;
            }

            int queryCount = queriesProp.GetArrayLength();
            if (queryCount == 0)
            {
                errors.Add(new CommandError("invalid_input", "Property 'queries' must contain at least one query."));
                return errors;
            }

            if (queryCount > MaxBatchedQueries)
            {
                errors.Add(new CommandError("invalid_input", $"Property 'queries' supports at most {MaxBatchedQueries} queries per call."));
                return errors;
            }

            ValidateSharedOptions(input, errors);
            int idx = 0;
            foreach (JsonElement queryItem in queriesProp.EnumerateArray())
            {
                if (queryItem.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new CommandError("invalid_input", $"Query at index {idx} must be an object."));
                    idx++;
                    continue;
                }

                ValidateSingleQuerySpec(queryItem, fallbackScope: input, errors, prefix: $"Query[{idx}]: ");
                idx++;
            }

            return errors;
        }

        ValidateSingleQuerySpec(input, fallbackScope: null, errors, prefix: string.Empty);
        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        IReadOnlyList<CommandError> validationErrors = Validate(input);
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        if (input.TryGetProperty("queries", out JsonElement queriesProp) && queriesProp.ValueKind == JsonValueKind.Array)
        {
            List<object> batchResults = new();
            List<CommandError> batchErrors = new();
            int totalMatchesAll = 0;
            int idx = 0;
            string? dominantSource = null;

            foreach (JsonElement queryItem in queriesProp.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? label = GetOptionalTrimmedString(queryItem, "label");
                CommandExecutionResult itemResult = await ExecuteSingleQueryAsync(queryItem, fallbackDefaults: input, cancellationToken).ConfigureAwait(false);
                if (!itemResult.Ok)
                {
                    batchErrors.AddRange(itemResult.Errors);
                    batchResults.Add(new
                    {
                        index = idx,
                        label,
                        ok = false,
                        errors = itemResult.Errors,
                    });
                }
                else
                {
                    JsonElement itemData = JsonSerializer.SerializeToElement(itemResult.Data);
                    int itemMatches = itemData.TryGetProperty("total_matches", out JsonElement tm) && tm.TryGetInt32(out int c) ? c : 0;
                    totalMatchesAll += itemMatches;
                    if (dominantSource is null &&
                        itemData.TryGetProperty("analysis_scope", out JsonElement scopeObj) &&
                        scopeObj.TryGetProperty("text_source", out JsonElement ts))
                    {
                        dominantSource = ts.GetString();
                    }

                    batchResults.Add(new
                    {
                        index = idx,
                        label,
                        ok = true,
                        result = itemResult.Data,
                    });
                }

                idx++;
            }

            object batchPayload = new
            {
                batched = true,
                query_count = batchResults.Count,
                total_matches = totalMatchesAll,
                text_source = dominantSource ?? "disk",
                results = batchResults,
            };

            return new CommandExecutionResult(batchPayload, batchErrors);
        }

        return await ExecuteSingleQueryAsync(input, fallbackDefaults: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> ExecuteSingleQueryAsync(
        JsonElement queryInput,
        JsonElement? fallbackDefaults,
        CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!TryGetPatterns(queryInput, errors, out string[] patterns))
        {
            return new CommandExecutionResult(null, errors);
        }

        string mode = GetMode(queryInput, fallbackDefaults);
        string syntaxFilter = GetSyntaxFilter(queryInput, fallbackDefaults);
        bool wholeWord = GetBoolWithFallback(queryInput, fallbackDefaults, "whole_word", defaultValue: false);
        bool caseSensitive = GetBoolWithFallback(queryInput, fallbackDefaults, "case_sensitive", defaultValue: false);
        bool includeGenerated = GetBoolWithFallback(queryInput, fallbackDefaults, "include_generated", defaultValue: false);
        bool brief = GetBoolWithFallback(queryInput, fallbackDefaults, "brief", defaultValue: true);
        bool hasSyntaxFilter = !string.Equals(syntaxFilter, "any", StringComparison.OrdinalIgnoreCase);
        bool includeSyntaxContext = GetBoolWithFallback(queryInput, fallbackDefaults, "include_syntax_context", defaultValue: hasSyntaxFilter);

        int maxResults = GetIntWithFallback(queryInput, fallbackDefaults, "max_results", defaultValue: 200, minValue: 1, maxValue: 10_000);
        int maxFiles = GetIntWithFallback(queryInput, fallbackDefaults, "max_files", defaultValue: 1_000, minValue: 1, maxValue: 100_000);
        int contextLines = GetIntWithFallback(queryInput, fallbackDefaults, "context_lines", defaultValue: brief ? 0 : 1, minValue: 0, maxValue: 10);
        int previewMaxChars = GetIntWithFallback(queryInput, fallbackDefaults, "preview_max_chars", defaultValue: 180, minValue: 40, maxValue: 4_000);
        int maxReturnedMatches = GetIntWithFallback(queryInput, fallbackDefaults, "max_returned_matches", defaultValue: 8, minValue: 1, maxValue: 1_000);

        string? filePath = GetOptionalTrimmedString(queryInput, "file_path") ?? GetOptionalTrimmedString(fallbackDefaults, "file_path");
        string[] roots = GetOptionalTrimmedStringArray(queryInput, "roots");
        if (roots.Length == 0 && fallbackDefaults.HasValue)
        {
            roots = GetOptionalTrimmedStringArray(fallbackDefaults.Value, "roots");
        }

        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(queryInput)
            ?? (fallbackDefaults.HasValue ? WorkspaceInput.GetOptionalWorkspacePath(fallbackDefaults.Value) : null);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(queryInput)
            ?? (fallbackDefaults.HasValue ? WorkspaceInput.GetOptionalWorkspaceHandle(fallbackDefaults.Value) : null);

        string[] includeGlobs = GetOptionalTrimmedStringArray(queryInput, "include_globs");
        if (includeGlobs.Length == 0 && fallbackDefaults.HasValue)
        {
            includeGlobs = GetOptionalTrimmedStringArray(fallbackDefaults.Value, "include_globs");
        }

        if (includeGlobs.Length == 0)
        {
            includeGlobs = ["**/*.cs", "**/*.csx"];
        }

        string[] excludeGlobs = GetOptionalTrimmedStringArray(queryInput, "exclude_globs");
        if (excludeGlobs.Length == 0 && fallbackDefaults.HasValue)
        {
            excludeGlobs = GetOptionalTrimmedStringArray(fallbackDefaults.Value, "exclude_globs");
        }

        HostedWorkspace? hosted = TryGetHostedWorkspaceByHandle(workspaceHandle);
        if (filePath is null && roots.Length == 0 && workspacePath is null && hosted is not null)
        {
            workspacePath = hosted.Workspace.ResolvedWorkspacePath;
        }

        List<SearchScope> scopes = ResolveScopes(filePath, roots, workspacePath);
        if (scopes.Count == 0)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_input", "No valid search scope was resolved.") });
        }

        if (filePath is not null && !File.Exists(filePath) && (hosted is null || !hosted.Workspace.TryGetTreeByPath(filePath, out _)))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        bool isRegex = string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase);
        Regex[] regexes = Array.Empty<Regex>();
        if (isRegex)
        {
            RegexOptions options = GetRegexOptions(caseSensitive);
            regexes = patterns.Select(pattern => new Regex(pattern, options, TimeSpan.FromSeconds(2))).ToArray();
        }

        List<GlobPattern> includePatterns = includeGlobs.Select(GlobPattern.Create).Where(p => p is not null).Select(p => p!).ToList();
        List<GlobPattern> excludePatterns = excludeGlobs.Select(GlobPattern.Create).Where(p => p is not null).Select(p => p!).ToList();

        HashSet<string> seenFiles = new(PathComparer);
        List<SearchMatch> matches = new();
        int filesScanned = 0;
        int filesWithMatches = 0;
        bool truncated = false;
        bool fileLimitReached = false;
        int overlayIndexedDocuments = 0;
        int overlayPrunedFiles = 0;
        int overlayHits = 0;
        int hotTextHits = 0;
        int diskHits = 0;

        bool canUseOverlay = !includeGenerated &&
                             hosted is not null &&
                             UsesOnlyWorkspaceSourceGlobs(includeGlobs) &&
                             hosted.Workspace.SyntaxTreesByPath.Count > 0 &&
                             scopes.All(s => hosted.OverlayIndex.Covers(s.path) || hosted.Workspace.TryGetTreeByPath(s.path, out _));

        if (canUseOverlay && hosted is not null)
        {
            // Zero-disk fast path: use WorkspaceOverlayIndex trigram + token posting lists over cached SourceText/SyntaxTree.
            IReadOnlyList<OverlayDocumentEntry> candidates;
            if (scopes.Count == 1 && scopes[0].is_file)
            {
                if (hosted.OverlayIndex.TryGetDocument(hosted.Workspace, scopes[0].path, out OverlayDocumentEntry? singleDoc, cancellationToken) &&
                    singleDoc is not null)
                {
                    candidates = new[] { singleDoc };
                    overlayIndexedDocuments = hosted.OverlayIndex.IndexedDocumentCount;
                    overlayPrunedFiles = Math.Max(0, overlayIndexedDocuments - 1);
                }
                else
                {
                    candidates = Array.Empty<OverlayDocumentEntry>();
                    canUseOverlay = false;
                }
            }
            else
            {
                candidates = hosted.OverlayIndex.QueryCandidates(
                    hosted.Workspace,
                    patterns,
                    isRegex,
                    wholeWord,
                    syntaxFilter,
                    out overlayIndexedDocuments,
                    out overlayPrunedFiles,
                    cancellationToken);
            }

            if (canUseOverlay)
            {
                foreach (OverlayDocumentEntry doc in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SearchScope? matchingScope = FindContainingScope(scopes, doc.FilePath);
                    if (matchingScope is null)
                    {
                        continue;
                    }

                    if (!seenFiles.Add(doc.FilePath))
                    {
                        continue;
                    }

                    if (filesScanned >= maxFiles)
                    {
                        fileLimitReached = true;
                        break;
                    }

                    string relativePath = BuildRelativePath(matchingScope, doc.FilePath);
                    string fileName = Path.GetFileName(doc.FilePath);
                    if (!MatchesPatterns(relativePath, fileName, includePatterns, excludePatterns))
                    {
                        continue;
                    }

                    filesScanned++;
                    overlayHits++;
                    int beforeCount = matches.Count;

                    SearchOverlayDocument(
                        doc,
                        patterns,
                        regexes,
                        isRegex,
                        wholeWord,
                        caseSensitive,
                        syntaxFilter,
                        includeSyntaxContext,
                        contextLines,
                        previewMaxChars,
                        maxResults,
                        matches,
                        ref truncated,
                        cancellationToken);

                    if (matches.Count > beforeCount)
                    {
                        filesWithMatches++;
                    }

                    if (truncated)
                    {
                        break;
                    }
                }
            }
        }

        if (!canUseOverlay)
        {
            HotTextIndex? textIndex = null;
            IReadOnlyDictionary<string, IndexedTextFile>? textSnapshot = null;
            IReadOnlySet<string> unindexedScopes = new HashSet<string>(PathComparer);
            if (!includeGenerated &&
                UsesOnlyCSharpGlobs(includeGlobs) &&
                hosted is not null)
            {
                textIndex = hosted.TextIndex;
                string[] directoryScopes = scopes
                    .Where(scope => !scope.is_file && Directory.Exists(scope.path) && textIndex.Covers(scope.path))
                    .Select(scope => scope.path)
                    .ToArray();
                textSnapshot = textIndex.Snapshot(hosted.ChangeTracker, directoryScopes, out unindexedScopes);
            }

            foreach ((SearchScope scope, string candidatePath, IndexedTextFile? indexedFile) in EnumerateCandidateFiles(scopes, textIndex, textSnapshot, unindexedScopes))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fullPath = Path.GetFullPath(candidatePath);
                if (!seenFiles.Add(fullPath))
                {
                    continue;
                }

                if (filesScanned >= maxFiles)
                {
                    fileLimitReached = true;
                    break;
                }

                if (!includeGenerated && CommandFileFilters.IsGeneratedPath(fullPath))
                {
                    continue;
                }

                string relativePath = BuildRelativePath(scope, fullPath);
                string fileName = Path.GetFileName(fullPath);
                if (!MatchesPatterns(relativePath, fileName, includePatterns, excludePatterns))
                {
                    continue;
                }

                // Prefer cached overlay entry for individual files when available even in mixed scopes.
                if (hosted is not null &&
                    hosted.OverlayIndex.TryGetDocument(hosted.Workspace, fullPath, out OverlayDocumentEntry? overlayDoc, cancellationToken) &&
                    overlayDoc is not null)
                {
                    filesScanned++;
                    overlayHits++;
                    int beforeCount = matches.Count;
                    SearchOverlayDocument(
                        overlayDoc,
                        patterns,
                        regexes,
                        isRegex,
                        wholeWord,
                        caseSensitive,
                        syntaxFilter,
                        includeSyntaxContext,
                        contextLines,
                        previewMaxChars,
                        maxResults,
                        matches,
                        ref truncated,
                        cancellationToken);
                    if (matches.Count > beforeCount)
                    {
                        filesWithMatches++;
                    }

                    if (truncated)
                    {
                        break;
                    }

                    continue;
                }

                string[] lines;
                if (indexedFile is not null)
                {
                    lines = indexedFile.Lines;
                    hotTextHits++;
                }
                else
                {
                    try
                    {
                        lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                        diskHits++;
                    }
                    catch (Exception ex)
                    {
                        return new CommandExecutionResult(
                            null,
                            new[] { new CommandError("io_error", $"Failed to read '{fullPath}': {ex.Message}") });
                    }
                }

                filesScanned++;
                int before = matches.Count;
                SearchLinesWithOptionalSyntaxFilter(
                    fullPath,
                    lines,
                    patterns,
                    regexes,
                    isRegex,
                    wholeWord,
                    caseSensitive,
                    syntaxFilter,
                    includeSyntaxContext,
                    contextLines,
                    previewMaxChars,
                    maxResults,
                    matches,
                    ref truncated,
                    cancellationToken);

                if (matches.Count > before)
                {
                    filesWithMatches++;
                }

                if (truncated)
                {
                    break;
                }
            }
        }

        string textSource = canUseOverlay
            ? "workspace_overlay_index"
            : overlayHits > 0 && diskHits == 0 && hotTextHits == 0
                ? "workspace_overlay_index"
                : hotTextHits > 0 && diskHits == 0
                    ? "hot_text_index"
                    : (overlayHits > 0 || hotTextHits > 0) && diskHits > 0
                        ? "mixed"
                        : "disk";

        object? resultGuidance = BuildResultGuidance(
            matches.Count,
            truncated,
            fileLimitReached,
            maxResults,
            contextLines,
            previewMaxChars,
            filePath,
            roots);

        bool compactBroadMatches = brief && resultGuidance is not null && matches.Count > maxReturnedMatches;
        SearchMatch[] returnedMatches = compactBroadMatches
            ? matches.Take(maxReturnedMatches).ToArray()
            : matches.ToArray();

        object matchPayload = brief
            ? returnedMatches.Select(match => includeSyntaxContext
                ? (object)new
                {
                    match.file_path,
                    match.line,
                    match.column,
                    match.pattern,
                    match.syntax_context,
                    match.enclosing_symbol,
                    match.preview,
                }
                : new
                {
                    match.file_path,
                    match.line,
                    match.column,
                    match.pattern,
                    match.preview,
                }).ToArray()
            : returnedMatches;

        object data = new
        {
            analysis_scope = new
            {
                analysis_mode = canUseOverlay ? "overlay_index" : "directory_scan",
                text_source = textSource,
                indexed_documents = overlayIndexedDocuments,
                index_pruned_files = overlayPrunedFiles,
                workspace_kind = scopes.All(scope => scope.is_file) ? "file" : "directory",
                resolved_scope_count = scopes.Count,
                scopes = scopes.Select(scope => new
                {
                    path = scope.path,
                    is_file = scope.is_file,
                }).ToArray(),
            },
            query = new
            {
                patterns,
                mode,
                syntax_filter = syntaxFilter,
                whole_word = wholeWord,
                include_syntax_context = includeSyntaxContext,
                case_sensitive = caseSensitive,
                file_path = filePath,
                roots,
                workspace_path = workspacePath,
                include_globs = includeGlobs,
                exclude_globs = excludeGlobs,
                include_generated = includeGenerated,
                max_results = maxResults,
                max_files = maxFiles,
                context_lines = contextLines,
                preview_max_chars = previewMaxChars,
                max_returned_matches = maxReturnedMatches,
                brief,
            },
            files_scanned = filesScanned,
            files_with_matches = filesWithMatches,
            total_matches = matches.Count,
            returned_matches = returnedMatches.Length,
            omitted_matches = Math.Max(0, matches.Count - returnedMatches.Length),
            truncated,
            file_limit_reached = fileLimitReached,
            result_guidance = resultGuidance,
            matches = matchPayload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static void SearchOverlayDocument(
        OverlayDocumentEntry doc,
        string[] patterns,
        Regex[] regexes,
        bool isRegex,
        bool wholeWord,
        bool caseSensitive,
        string syntaxFilter,
        bool includeSyntaxContext,
        int contextLines,
        int previewMaxChars,
        int maxResults,
        List<SearchMatch> matches,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        SourceText sourceText = doc.SourceText;
        string fullText = doc.FullText;
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool needSyntax = includeSyntaxContext || !string.Equals(syntaxFilter, "any", StringComparison.OrdinalIgnoreCase);

        if (!isRegex)
        {
            // Scan the full text directly using Span/IndexOf (far faster than allocating per-line strings!)
            // and map each hit to 1-based line/column via Roslyn's O(log L) SourceText.Lines table.
            for (int p = 0; p < patterns.Length; p++)
            {
                string pattern = patterns[p];
                int searchFrom = 0;
                while (searchFrom <= fullText.Length - pattern.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int foundAt = fullText.IndexOf(pattern, searchFrom, comparison);
                    if (foundAt < 0)
                    {
                        break;
                    }

                    searchFrom = foundAt + Math.Max(1, pattern.Length);
                    if (wholeWord && !IsWholeWordBoundary(fullText, foundAt, pattern.Length))
                    {
                        continue;
                    }

                    SyntaxMatchClassification? classification = null;
                    if (needSyntax)
                    {
                        classification = WorkspaceOverlayIndex.ClassifySyntaxMatch(doc.SyntaxTree, foundAt, pattern.Length, cancellationToken);
                        if (!MatchesSyntaxFilter(classification, syntaxFilter))
                        {
                            continue;
                        }
                    }

                    LinePosition pos = sourceText.Lines.GetLinePosition(foundAt);
                    string preview = BuildPreviewFromSourceText(sourceText, pos.Line, contextLines, previewMaxChars);
                    matches.Add(new SearchMatch(
                        file_path: doc.FilePath,
                        line: pos.Line + 1,
                        column: pos.Character + 1,
                        pattern: pattern,
                        match_text: pattern,
                        preview: preview,
                        syntax_context: classification?.ContextKind,
                        enclosing_symbol: classification?.EnclosingSymbol));

                    if (matches.Count >= maxResults)
                    {
                        truncated = true;
                        return;
                    }
                }
            }
        }
        else
        {
            // Regex searches remain line-scoped for exact compatibility with existing line-anchored regexes (^ / $).
            TextLineCollection textLines = sourceText.Lines;
            for (int lineIndex = 0; lineIndex < textLines.Count; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TextLine textLine = textLines[lineIndex];
                string line = textLine.ToString();

                for (int p = 0; p < regexes.Length; p++)
                {
                    Regex regex = regexes[p];
                    string pattern = patterns[p];
                    foreach (Match match in regex.Matches(line))
                    {
                        if (!match.Success || match.Value.Length == 0)
                        {
                            continue;
                        }

                        int absOffset = textLine.Start + match.Index;
                        SyntaxMatchClassification? classification = null;
                        if (needSyntax)
                        {
                            classification = WorkspaceOverlayIndex.ClassifySyntaxMatch(doc.SyntaxTree, absOffset, match.Length, cancellationToken);
                            if (!MatchesSyntaxFilter(classification, syntaxFilter))
                            {
                                continue;
                            }
                        }

                        matches.Add(new SearchMatch(
                            file_path: doc.FilePath,
                            line: lineIndex + 1,
                            column: match.Index + 1,
                            pattern: pattern,
                            match_text: TruncateText(match.Value, maxChars: 120),
                            preview: BuildPreviewFromSourceText(sourceText, lineIndex, contextLines, previewMaxChars),
                            syntax_context: classification?.ContextKind,
                            enclosing_symbol: classification?.EnclosingSymbol));

                        if (matches.Count >= maxResults)
                        {
                            truncated = true;
                            return;
                        }
                    }
                }
            }
        }
    }

    private static void SearchLinesWithOptionalSyntaxFilter(
        string fullPath,
        string[] lines,
        string[] patterns,
        Regex[] regexes,
        bool isRegex,
        bool wholeWord,
        bool caseSensitive,
        string syntaxFilter,
        bool includeSyntaxContext,
        int contextLines,
        int previewMaxChars,
        int maxResults,
        List<SearchMatch> matches,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool needSyntax = (includeSyntaxContext || !string.Equals(syntaxFilter, "any", StringComparison.OrdinalIgnoreCase)) &&
                          CommandLanguageServices.IsSupportedSourceFile(fullPath);

        SyntaxTree? lazyTree = null;
        SourceText? lazySourceText = null;

        SyntaxMatchClassification? ClassifyDiskMatch(int lineIndex, int columnIndex, int length)
        {
            if (!needSyntax)
            {
                return null;
            }

            if (lazyTree is null)
            {
                string joined = string.Join('\n', lines);
                lazyTree = CommandLanguageServices.ParseSyntaxTree(joined, fullPath, cancellationToken);
                lazySourceText = lazyTree.GetText(cancellationToken);
            }

            int absOffset = lazySourceText!.Lines[lineIndex].Start + columnIndex;
            return WorkspaceOverlayIndex.ClassifySyntaxMatch(lazyTree, absOffset, length, cancellationToken);
        }

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines[lineIndex];

            if (!isRegex)
            {
                for (int patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
                {
                    string pattern = patterns[patternIndex];
                    int searchFrom = 0;
                    while (true)
                    {
                        int foundAt = line.IndexOf(pattern, searchFrom, comparison);
                        if (foundAt < 0)
                        {
                            break;
                        }

                        searchFrom = foundAt + Math.Max(1, pattern.Length);
                        if (wholeWord && !IsWholeWordBoundary(line, foundAt, pattern.Length))
                        {
                            if (searchFrom > line.Length)
                            {
                                break;
                            }

                            continue;
                        }

                        SyntaxMatchClassification? classification = ClassifyDiskMatch(lineIndex, foundAt, pattern.Length);
                        if (classification is not null && !MatchesSyntaxFilter(classification, syntaxFilter))
                        {
                            if (searchFrom > line.Length)
                            {
                                break;
                            }

                            continue;
                        }

                        matches.Add(new SearchMatch(
                            file_path: fullPath,
                            line: lineIndex + 1,
                            column: foundAt + 1,
                            pattern: pattern,
                            match_text: pattern,
                            preview: BuildPreview(lines, lineIndex, contextLines, previewMaxChars),
                            syntax_context: classification?.ContextKind,
                            enclosing_symbol: classification?.EnclosingSymbol));

                        if (matches.Count >= maxResults)
                        {
                            truncated = true;
                            return;
                        }

                        if (searchFrom > line.Length)
                        {
                            break;
                        }
                    }
                }
            }
            else
            {
                for (int patternIndex = 0; patternIndex < regexes.Length; patternIndex++)
                {
                    Regex regex = regexes[patternIndex];
                    string pattern = patterns[patternIndex];

                    foreach (Match match in regex.Matches(line))
                    {
                        if (!match.Success || match.Value.Length == 0)
                        {
                            continue;
                        }

                        SyntaxMatchClassification? classification = ClassifyDiskMatch(lineIndex, match.Index, match.Length);
                        if (classification is not null && !MatchesSyntaxFilter(classification, syntaxFilter))
                        {
                            continue;
                        }

                        matches.Add(new SearchMatch(
                            file_path: fullPath,
                            line: lineIndex + 1,
                            column: match.Index + 1,
                            pattern: pattern,
                            match_text: TruncateText(match.Value, maxChars: 120),
                            preview: BuildPreview(lines, lineIndex, contextLines, previewMaxChars),
                            syntax_context: classification?.ContextKind,
                            enclosing_symbol: classification?.EnclosingSymbol));

                        if (matches.Count >= maxResults)
                        {
                            truncated = true;
                            return;
                        }
                    }
                }
            }
        }
    }

    private static bool MatchesSyntaxFilter(SyntaxMatchClassification classification, string filter)
        => filter.ToLowerInvariant() switch
        {
            "any" => true,
            "code" => classification.IsCode,
            "comments" => classification.IsComment,
            "strings" => classification.IsString,
            "comments_and_strings" or "non_code" => classification.IsComment || classification.IsString,
            "identifiers" => classification.IsIdentifier,
            "declarations" => classification.IsDeclaration,
            "invocations" => classification.IsInvocation,
            _ => true,
        };

    private static bool IsWholeWordBoundary(string text, int start, int length)
    {
        bool leftOk = start == 0 || !IsWordChar(text[start - 1]);
        int end = start + length;
        bool rightOk = end >= text.Length || !IsWordChar(text[end]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static SearchScope? FindContainingScope(IReadOnlyList<SearchScope> scopes, string fullPath)
    {
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (SearchScope scope in scopes)
        {
            if (scope.is_file)
            {
                if (PathComparer.Equals(scope.path, fullPath))
                {
                    return scope;
                }

                continue;
            }

            string normalizedRoot = scope.path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.StartsWith(normalizedRoot, cmp) &&
                (fullPath.Length == normalizedRoot.Length ||
                 fullPath[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
                 fullPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar))
            {
                return scope;
            }
        }

        return null;
    }

    private static string BuildPreviewFromSourceText(SourceText sourceText, int lineIndex, int contextLines, int maxChars)
    {
        int startLine = Math.Max(0, lineIndex - contextLines);
        int endLine = Math.Min(sourceText.Lines.Count - 1, lineIndex + contextLines);
        List<string> snippetLines = new(endLine - startLine + 1);
        for (int i = startLine; i <= endLine; i++)
        {
            snippetLines.Add($"{i + 1,4}: {sourceText.Lines[i]}");
        }

        string preview = string.Join(Environment.NewLine, snippetLines);
        return TruncateText(preview, maxChars);
    }

    private static void ValidateSharedOptions(JsonElement input, List<CommandError> errors)
    {
        ValidateOptionalString(input, "file_path", errors);
        ValidateOptionalStringArray(input, "roots", errors);
        ValidateOptionalStringArray(input, "include_globs", errors);
        ValidateOptionalStringArray(input, "exclude_globs", errors);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "case_sensitive", errors);
        InputParsing.ValidateOptionalBool(input, "whole_word", errors);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);
        InputParsing.ValidateOptionalBool(input, "include_syntax_context", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);

        string syntaxFilter = GetSyntaxFilter(input, fallback: null);
        if (!SupportedSyntaxFilters.Contains(syntaxFilter))
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property 'syntax_filter' value '{syntaxFilter}' is unsupported. Supported values: {string.Join(", ", SupportedSyntaxFilters.OrderBy(v => v, StringComparer.Ordinal))}."));
        }
    }

    private static void ValidateSingleQuerySpec(JsonElement input, JsonElement? fallbackScope, List<CommandError> errors, string prefix)
    {
        string mode = GetMode(input, fallbackScope);
        if (!IsValidMode(mode))
        {
            errors.Add(new CommandError("invalid_input", $"{prefix}Property 'mode' must be 'literal' or 'regex'."));
            return;
        }

        string syntaxFilter = GetSyntaxFilter(input, fallbackScope);
        if (!SupportedSyntaxFilters.Contains(syntaxFilter))
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"{prefix}Property 'syntax_filter' value '{syntaxFilter}' is unsupported. Supported values: {string.Join(", ", SupportedSyntaxFilters.OrderBy(v => v, StringComparer.Ordinal))}."));
            return;
        }

        if (!TryGetPatterns(input, errors, out string[] patterns))
        {
            return;
        }

        ValidateOptionalString(input, "file_path", errors);
        ValidateOptionalStringArray(input, "roots", errors);
        ValidateOptionalStringArray(input, "include_globs", errors);
        ValidateOptionalStringArray(input, "exclude_globs", errors);
        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);

        InputParsing.ValidateOptionalBool(input, "case_sensitive", errors);
        InputParsing.ValidateOptionalBool(input, "whole_word", errors);
        InputParsing.ValidateOptionalBool(input, "include_generated", errors);
        InputParsing.ValidateOptionalBool(input, "include_syntax_context", errors);
        InputParsing.ValidateOptionalBool(input, "brief", errors);

        bool hasScope = HasNonEmptyString(input, "file_path")
            || HasNonEmptyArray(input, "roots")
            || HasNonEmptyString(input, "workspace_path")
            || HasNonEmptyString(input, "workspace_handle")
            || (fallbackScope.HasValue &&
                (HasNonEmptyString(fallbackScope.Value, "file_path")
                 || HasNonEmptyArray(fallbackScope.Value, "roots")
                 || HasNonEmptyString(fallbackScope.Value, "workspace_path")
                 || HasNonEmptyString(fallbackScope.Value, "workspace_handle")));

        if (!hasScope)
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"{prefix}At least one scope is required: provide 'file_path', 'roots', 'workspace_path', or 'workspace_handle'."));
        }

        if (HasNonEmptyString(input, "file_path") &&
            InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath) &&
            !File.Exists(filePath))
        {
            string? handle = WorkspaceInput.GetOptionalWorkspaceHandle(input)
                ?? (fallbackScope.HasValue ? WorkspaceInput.GetOptionalWorkspaceHandle(fallbackScope.Value) : null);
            HostedWorkspace? hosted = TryGetHostedWorkspaceByHandle(handle);
            if (hosted is null || !hosted.Workspace.TryGetTreeByPath(filePath, out _))
            {
                errors.Add(new CommandError("file_not_found", $"{prefix}Input file '{filePath}' does not exist."));
            }
        }

        if (input.TryGetProperty("roots", out JsonElement rootsProperty) &&
            rootsProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement rootProperty in rootsProperty.EnumerateArray())
            {
                if (rootProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string root = (rootProperty.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (!File.Exists(root) && !Directory.Exists(root))
                {
                    errors.Add(new CommandError("path_not_found", $"{prefix}Search root '{root}' does not exist."));
                }
            }
        }

        if (string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
        {
            bool caseSensitive = GetBoolWithFallback(input, fallbackScope, "case_sensitive", defaultValue: false);
            RegexOptions options = GetRegexOptions(caseSensitive);
            foreach (string pattern in patterns)
            {
                try
                {
                    _ = new Regex(pattern, options, TimeSpan.FromSeconds(2));
                }
                catch (ArgumentException ex)
                {
                    errors.Add(new CommandError("invalid_regex", $"{prefix}Pattern '{pattern}' is not a valid regex: {ex.Message}"));
                }
            }
        }
    }

    private static object? BuildResultGuidance(
        int matchCount,
        bool truncated,
        bool fileLimitReached,
        int maxResults,
        int contextLines,
        int previewMaxChars,
        string? filePath,
        IReadOnlyList<string> roots)
    {
        bool largeResultSet = matchCount >= 20;
        bool highRequestedLimit = maxResults > 40;
        bool highPayloadShape = largeResultSet && (contextLines > 0 || previewMaxChars > 180 || highRequestedLimit);
        if (!truncated && !fileLimitReached && !highPayloadShape)
        {
            return null;
        }

        List<string> reasons = new();
        if (truncated)
        {
            reasons.Add("result limit reached before search completed");
        }

        if (fileLimitReached)
        {
            reasons.Add("file scan limit reached before search completed");
        }

        if (highPayloadShape)
        {
            reasons.Add("broad search returned many preview-bearing matches");
        }

        string scopeHint = filePath is not null
            ? "--file-path <same-file>"
            : roots.Count > 0
                ? "--root <narrower-dir>"
                : "--workspace-path <solution-or-project>";

        return new
        {
            severity = truncated || fileLimitReached ? "warning" : "info",
            reasons = reasons.ToArray(),
            recommended_next_step = "Narrow before repeating broad search: add --syntax-filter code|declarations|invocations, --whole-word true, or switch to ctx.symbol_context, ctx.member_source, or ctx.read_window.",
            suggested_commands = new[]
            {
                $"roscli ctx.search_text <pattern> {scopeHint} --syntax-filter code --whole-word true --max-results 20 --context-lines 0",
                "roscli ctx.symbol_context --symbol <Type.Member> --brief true",
                "roscli ctx.file_outline <file-path> --member-name-contains <term> --max-members 20",
                "roscli ctx.member_source <file-path> --member-name <name> --focus-text <literal> --context-lines-before 3 --context-lines-after 8",
            },
            payload_policy = "Brief broad results return a capped matches payload; total_matches remains authoritative. Add --max-returned-matches <n> only when you deliberately need more preview rows.",
        };
    }

    private static bool TryGetPatterns(JsonElement input, List<CommandError> errors, out string[] patterns)
    {
        List<string> values = new();

        if (input.TryGetProperty("pattern", out JsonElement patternProperty))
        {
            if (patternProperty.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", "Property 'pattern' must be a string when provided."));
            }
            else
            {
                string value = (patternProperty.GetString() ?? string.Empty).Trim();
                if (value.Length > 0)
                {
                    values.Add(value);
                }
            }
        }

        if (input.TryGetProperty("patterns", out JsonElement patternsProperty))
        {
            if (patternsProperty.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new CommandError("invalid_input", "Property 'patterns' must be an array of strings."));
            }
            else
            {
                foreach (JsonElement patternElement in patternsProperty.EnumerateArray())
                {
                    if (patternElement.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(new CommandError("invalid_input", "Property 'patterns' must contain only strings."));
                        continue;
                    }

                    string value = (patternElement.GetString() ?? string.Empty).Trim();
                    if (value.Length > 0)
                    {
                        values.Add(value);
                    }
                }
            }
        }

        values = values
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToList();

        if (values.Count == 0)
        {
            errors.Add(new CommandError("invalid_input", "Provide at least one non-empty search pattern via 'pattern', 'patterns', or 'queries'."));
            patterns = Array.Empty<string>();
            return false;
        }

        patterns = values.ToArray();
        return true;
    }

    private static string GetMode(JsonElement input, JsonElement? fallback)
        => GetOptionalTrimmedString(input, "mode")
           ?? GetOptionalTrimmedString(fallback, "mode")
           ?? "literal";

    private static string GetSyntaxFilter(JsonElement input, JsonElement? fallback)
        => GetOptionalTrimmedString(input, "syntax_filter")
           ?? GetOptionalTrimmedString(input, "ast_filter")
           ?? GetOptionalTrimmedString(fallback, "syntax_filter")
           ?? GetOptionalTrimmedString(fallback, "ast_filter")
           ?? "any";

    private static bool GetBoolWithFallback(JsonElement primary, JsonElement? fallback, string propertyName, bool defaultValue)
    {
        if (primary.ValueKind == JsonValueKind.Object && primary.TryGetProperty(propertyName, out _))
        {
            return InputParsing.GetOptionalBool(primary, propertyName, defaultValue);
        }

        if (fallback.HasValue && fallback.Value.ValueKind == JsonValueKind.Object && fallback.Value.TryGetProperty(propertyName, out _))
        {
            return InputParsing.GetOptionalBool(fallback.Value, propertyName, defaultValue);
        }

        return defaultValue;
    }

    private static int GetIntWithFallback(JsonElement primary, JsonElement? fallback, string propertyName, int defaultValue, int minValue, int maxValue)
    {
        if (primary.ValueKind == JsonValueKind.Object && primary.TryGetProperty(propertyName, out _))
        {
            return InputParsing.GetOptionalInt(primary, propertyName, defaultValue, minValue, maxValue);
        }

        if (fallback.HasValue && fallback.Value.ValueKind == JsonValueKind.Object && fallback.Value.TryGetProperty(propertyName, out _))
        {
            return InputParsing.GetOptionalInt(fallback.Value, propertyName, defaultValue, minValue, maxValue);
        }

        return defaultValue;
    }

    private static bool IsValidMode(string mode)
        => string.Equals(mode, "literal", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase);

    private static RegexOptions GetRegexOptions(bool caseSensitive)
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return options;
    }

    private static string? GetOptionalTrimmedString(JsonElement? input, string propertyName)
        => input.HasValue ? GetOptionalTrimmedString(input.Value, propertyName) : null;

    private static string? GetOptionalTrimmedString(JsonElement input, string propertyName)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string value = (property.GetString() ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string[] GetOptionalTrimmedStringArray(JsonElement input, string propertyName)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        List<string> values = new();
        foreach (JsonElement element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string value = (element.GetString() ?? string.Empty).Trim();
            if (value.Length > 0)
            {
                values.Add(value);
            }
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasNonEmptyString(JsonElement input, string propertyName)
        => !string.IsNullOrWhiteSpace(GetOptionalTrimmedString(input, propertyName));

    private static bool HasNonEmptyArray(JsonElement input, string propertyName)
        => GetOptionalTrimmedStringArray(input, propertyName).Length > 0;

    private static void ValidateOptionalString(JsonElement input, string propertyName, List<CommandError> errors)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property))
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must be a string when provided."));
            return;
        }

        string value = (property.GetString() ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must not be empty when provided."));
        }
    }

    private static void ValidateOptionalStringArray(JsonElement input, string propertyName, List<CommandError> errors)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property))
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must be an array of strings when provided."));
            return;
        }

        foreach (JsonElement element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must contain only strings."));
                return;
            }

            string value = (element.GetString() ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                errors.Add(new CommandError("invalid_input", $"Property '{propertyName}' must not contain empty values."));
                return;
            }
        }
    }

    private static List<SearchScope> ResolveScopes(string? filePath, IReadOnlyList<string> roots, string? workspacePath)
    {
        List<SearchScope> scopes = new();
        HashSet<string> seen = new(PathComparer);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string fullPath = Path.GetFullPath(filePath);
            if (seen.Add(fullPath))
            {
                scopes.Add(new SearchScope(fullPath, is_file: true));
            }
        }

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(root);
            bool isFile = File.Exists(fullPath);
            bool isDirectory = Directory.Exists(fullPath);
            if (!isFile && !isDirectory)
            {
                continue;
            }

            if (seen.Add(fullPath))
            {
                scopes.Add(new SearchScope(fullPath, is_file: isFile));
            }
        }

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            string fullPath = Path.GetFullPath(workspacePath);
            if (File.Exists(fullPath))
            {
                string extension = Path.GetExtension(fullPath);
                string scopePath = string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(extension, ".csx", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase)
                    ? fullPath
                    : Path.GetDirectoryName(fullPath) ?? fullPath;
                bool isFile = File.Exists(scopePath);
                if (seen.Add(scopePath))
                {
                    scopes.Add(new SearchScope(scopePath, is_file: isFile));
                }
            }
            else if (Directory.Exists(fullPath))
            {
                if (seen.Add(fullPath))
                {
                    scopes.Add(new SearchScope(fullPath, is_file: false));
                }
            }
        }

        return scopes;
    }

    private static bool UsesOnlyCSharpGlobs(IReadOnlyList<string> includeGlobs)
        => includeGlobs.Count > 0 &&
           includeGlobs.All(glob =>
               glob.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
               glob.EndsWith(".csx", StringComparison.OrdinalIgnoreCase));

    private static bool UsesOnlyWorkspaceSourceGlobs(IReadOnlyList<string> includeGlobs)
        => includeGlobs.Count > 0 &&
           includeGlobs.All(glob =>
               glob.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
               glob.EndsWith(".csx", StringComparison.OrdinalIgnoreCase) ||
               glob.EndsWith(".vb", StringComparison.OrdinalIgnoreCase));

    private static HostedWorkspace? TryGetHostedWorkspaceByHandle(string? handle)
        => !string.IsNullOrWhiteSpace(handle) &&
           WorkspaceHostStoreProvider.Current.TryGet(handle, out HostedWorkspace? hosted)
            ? hosted
            : null;

    private static IEnumerable<(SearchScope Scope, string CandidatePath, IndexedTextFile? Indexed)> EnumerateCandidateFiles(
        IReadOnlyList<SearchScope> scopes,
        HotTextIndex? textIndex,
        IReadOnlyDictionary<string, IndexedTextFile>? textSnapshot,
        IReadOnlySet<string> unindexedScopes)
    {
        foreach (SearchScope scope in scopes)
        {
            bool covered = textIndex is not null && textSnapshot is not null && textIndex.Covers(scope.path) &&
                           (scope.is_file || !unindexedScopes.Contains(Path.TrimEndingDirectorySeparator(scope.path)));
            if (scope.is_file)
            {
                if (File.Exists(scope.path))
                {
                    IndexedTextFile? indexed = null;
                    if (covered)
                    {
                        textSnapshot!.TryGetValue(scope.path, out indexed);
                    }

                    yield return (scope, scope.path, indexed);
                }

                continue;
            }

            if (covered)
            {
                foreach (IndexedTextFile file in HotTextIndex.EnumerateBeneath(textSnapshot!, scope.path))
                {
                    yield return (scope, file.Path, file);
                }

                continue;
            }

            if (!Directory.Exists(scope.path))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(scope.path, "*", SearchOption.AllDirectories))
            {
                yield return (scope, file, null);
            }
        }
    }

    private static string BuildRelativePath(SearchScope scope, string fullPath)
    {
        if (scope.is_file)
        {
            return Path.GetFileName(fullPath).Replace('\\', '/');
        }

        try
        {
            string relative = Path.GetRelativePath(scope.path, fullPath);
            return relative.Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    private static bool MatchesPatterns(
        string relativePath,
        string fileName,
        IReadOnlyList<GlobPattern> includePatterns,
        IReadOnlyList<GlobPattern> excludePatterns)
    {
        bool include = includePatterns.Count == 0 || includePatterns.Any(pattern => pattern.IsMatch(relativePath, fileName));
        if (!include)
        {
            return false;
        }

        bool excluded = excludePatterns.Any(pattern => pattern.IsMatch(relativePath, fileName));
        return !excluded;
    }

    private static string BuildPreview(string[] lines, int lineIndex, int contextLines, int maxChars)
    {
        int startLine = Math.Max(0, lineIndex - contextLines);
        int endLine = Math.Min(lines.Length - 1, lineIndex + contextLines);
        List<string> snippetLines = new();
        for (int i = startLine; i <= endLine; i++)
        {
            snippetLines.Add($"{i + 1,4}: {lines[i]}");
        }

        string preview = string.Join(Environment.NewLine, snippetLines);
        return TruncateText(preview, maxChars);
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars <= 3)
        {
            return text[..maxChars];
        }

        return text[..(maxChars - 3)] + "...";
    }

    private sealed record SearchScope(string path, bool is_file);

    private sealed record SearchMatch(
        string file_path,
        int line,
        int column,
        string pattern,
        string match_text,
        string preview,
        string? syntax_context = null,
        string? enclosing_symbol = null);

    private sealed class GlobPattern
    {
        private readonly Regex _regex;
        private readonly bool _matchOnFileNameOnly;

        private GlobPattern(Regex regex, bool matchOnFileNameOnly)
        {
            _regex = regex;
            _matchOnFileNameOnly = matchOnFileNameOnly;
        }

        public static GlobPattern? Create(string pattern)
        {
            string normalized = (pattern ?? string.Empty).Trim().Replace('\\', '/');
            if (normalized.Length == 0)
            {
                return null;
            }

            bool fileNameOnly = !normalized.Contains('/', StringComparison.Ordinal);
            string regexPattern = "^" + ConvertGlobToRegex(normalized) + "$";
            RegexOptions options = RegexOptions.CultureInvariant;
            if (OperatingSystem.IsWindows())
            {
                options |= RegexOptions.IgnoreCase;
            }

            return new GlobPattern(new Regex(regexPattern, options), fileNameOnly);
        }

        public bool IsMatch(string relativePath, string fileName)
        {
            string candidate = _matchOnFileNameOnly ? fileName : relativePath;
            return _regex.IsMatch(candidate);
        }

        private static string ConvertGlobToRegex(string pattern)
        {
            int index = 0;
            StringBuilder builder = new(pattern.Length * 2);
            while (index < pattern.Length)
            {
                char c = pattern[index];
                if (c == '*')
                {
                    bool isDoubleStar = index + 1 < pattern.Length && pattern[index + 1] == '*';
                    if (isDoubleStar)
                    {
                        bool followedBySlash = index + 2 < pattern.Length && pattern[index + 2] == '/';
                        if (followedBySlash)
                        {
                            builder.Append("(?:.*/)?");
                            index += 3;
                        }
                        else
                        {
                            builder.Append(".*");
                            index += 2;
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                        index++;
                    }

                    continue;
                }

                if (c == '?')
                {
                    builder.Append("[^/]");
                    index++;
                    continue;
                }

                if (c == '/')
                {
                    builder.Append('/');
                    index++;
                    continue;
                }

                builder.Append(Regex.Escape(c.ToString()));
                index++;
            }

            return builder.ToString();
        }
    }
}
