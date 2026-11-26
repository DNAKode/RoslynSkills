using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VbSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Immutable per-document index entry tied to a specific Roslyn <see cref="SyntaxTree"/> and <see cref="SourceText"/>.
/// Cached in a <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed by <see cref="SyntaxTree"/> so untouched
/// documents across incremental solution updates are never re-tokenized or re-hashed.
/// </summary>
internal sealed class OverlayDocumentEntry
{
    private string? _textSha256;

    public OverlayDocumentEntry(
        string filePath,
        SyntaxTree syntaxTree,
        SourceText sourceText,
        string fullText,
        ulong[] uniqueTrigrams,
        string[] uniqueTokensLower)
    {
        FilePath = filePath;
        SyntaxTree = syntaxTree;
        SourceText = sourceText;
        FullText = fullText;
        UniqueTrigrams = uniqueTrigrams;
        UniqueTokensLower = uniqueTokensLower;
    }

    public string FilePath { get; }

    public SyntaxTree SyntaxTree { get; }

    public SourceText SourceText { get; }

    public string FullText { get; }

    /// <summary>Sorted unique case-folded 3-char trigrams packed into 48 bits of <see cref="ulong"/>.</summary>
    public ulong[] UniqueTrigrams { get; }

    /// <summary>Unique lowercased word/identifier tokens present in the document.</summary>
    public string[] UniqueTokensLower { get; }

    public int LineCount => SourceText.Lines.Count;

    public string GetTextSha256()
    {
        if (_textSha256 is not null)
        {
            return _textSha256;
        }

        Encoding encoding = SourceText.Encoding ?? EditFileText.Utf8NoBom;
        string computed = EditFileText.ComputeEncodedSha256(FullText, encoding);
        _textSha256 = computed;
        return computed;
    }
}

/// <summary>Syntax-level classification of a text match using Roslyn's cached <see cref="SyntaxTree"/>.</summary>
internal sealed record SyntaxMatchClassification(
    string ContextKind,
    bool IsCode,
    bool IsComment,
    bool IsString,
    bool IsIdentifier,
    bool IsDeclaration,
    bool IsInvocation,
    string? EnclosingSymbol);

/// <summary>
/// Live in-memory overlay trigram + token + line-offset index layered directly over a hot workspace's cached Roslyn
/// <see cref="SourceText"/> and <see cref="SyntaxTree"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// Zero disk reads when warm: all text, line offsets (<see cref="SourceText.Lines"/>), and syntax classification
/// come directly from the Roslyn workspace already resident in <c>WorkspaceHost</c>.
/// </para>
/// <para>
/// Incremental invalidation: unchanged documents keep their <see cref="SyntaxTree"/> object reference across Roslyn
/// immutable <see cref="Solution"/> snapshots, so incremental sync removes postings only for modified or deleted
/// documents and indexes only changed/new trees.
/// </para>
/// <para>
/// No duplication of Roslyn semantics: this index accelerates candidate selection, syntax-aware text filtering, and
/// zero-disk window reads; semantic symbol binding always delegates to Roslyn's <see cref="Compilation"/> and
/// <see cref="SemanticModel"/>.
/// </para>
/// </remarks>
internal sealed class WorkspaceOverlayIndex
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly ConditionalWeakTable<SyntaxTree, OverlayDocumentEntry> EntryCache = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _pathToDocId = new(PathComparer);
    private readonly List<OverlayDocumentEntry?> _docs = new();
    private readonly Stack<int> _freeDocIds = new();
    private readonly Dictionary<ulong, HashSet<int>> _trigramPostings = new();
    private readonly Dictionary<string, HashSet<int>> _tokenPostings = new(StringComparer.Ordinal);

    private StaticAnalysisWorkspace? _syncedWorkspace;
    private int _activeDocumentCount;
    private int _incrementalUpdateCount;
    private int _fullRebuildCount;
    private DateTimeOffset? _lastSyncUtc;

    public WorkspaceOverlayIndex(string rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? string.Empty
            : Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public int IndexedDocumentCount
    {
        get
        {
            lock (_gate)
            {
                return _activeDocumentCount;
            }
        }
    }

    public int IncrementalUpdateCount
    {
        get
        {
            lock (_gate)
            {
                return _incrementalUpdateCount;
            }
        }
    }

    public int FullRebuildCount
    {
        get
        {
            lock (_gate)
            {
                return _fullRebuildCount;
            }
        }
    }

    public bool Covers(string fullPath)
    {
        if (RootDirectory.Length == 0)
        {
            return false;
        }

        string normalizedRoot = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullPath.StartsWith(normalizedRoot, PathComparison))
        {
            return false;
        }

        return fullPath.Length == normalizedRoot.Length ||
               fullPath[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
               fullPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Pre-builds per-tree trigram/token entries in parallel and synchronizes the inverted posting tables.
    /// Safe to invoke from background warmup or on demand before the first query.
    /// </summary>
    public int Warm(StaticAnalysisWorkspace workspace, CancellationToken cancellationToken)
    {
        KeyValuePair<string, SyntaxTree>[] entries = workspace.SyntaxTreesByPath.ToArray();
        if (entries.Length > 1)
        {
            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                CancellationToken = cancellationToken,
            };
            Parallel.ForEach(entries, options, pair =>
            {
                SourceText text = workspace.SourceTextsByTree.TryGetValue(pair.Value, out SourceText? cached)
                    ? cached
                    : pair.Value.GetText(cancellationToken);
                _ = GetOrBuildEntry(pair.Key, pair.Value, text);
            });
        }

        SyncWithWorkspace(workspace, cancellationToken);
        return entries.Length;
    }

    /// <summary>
    /// Synchronizes the overlay index with <paramref name="workspace"/>. O(1) when already synced to the same
    /// <see cref="StaticAnalysisWorkspace"/> instance; incremental O(changed documents) after document edits.
    /// </summary>
    public void SyncWithWorkspace(StaticAnalysisWorkspace workspace, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_syncedWorkspace, workspace))
            {
                return;
            }

            bool isInitial = _syncedWorkspace is null;
            IReadOnlyDictionary<string, SyntaxTree> currentTrees = workspace.SyntaxTreesByPath;

            // 1. Remove documents that disappeared from the workspace.
            if (!isInitial)
            {
                List<string>? removed = null;
                foreach (string existingPath in _pathToDocId.Keys)
                {
                    if (!currentTrees.ContainsKey(existingPath))
                    {
                        removed ??= new List<string>();
                        removed.Add(existingPath);
                    }
                }

                if (removed is not null)
                {
                    foreach (string path in removed)
                    {
                        RemoveDocumentLocked(path);
                    }
                }
            }

            // 2. Add new documents or replace documents whose SyntaxTree reference changed.
            int changedCount = 0;
            foreach ((string fullPath, SyntaxTree tree) in currentTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_pathToDocId.TryGetValue(fullPath, out int existingDocId) &&
                    _docs[existingDocId] is { } existing &&
                    ReferenceEquals(existing.SyntaxTree, tree))
                {
                    continue;
                }

                SourceText text = workspace.SourceTextsByTree.TryGetValue(tree, out SourceText? cached)
                    ? cached
                    : tree.GetText(cancellationToken);
                OverlayDocumentEntry entry = GetOrBuildEntry(fullPath, tree, text);

                if (_pathToDocId.ContainsKey(fullPath))
                {
                    RemoveDocumentLocked(fullPath);
                }

                AddDocumentLocked(entry);
                changedCount++;
            }

            _syncedWorkspace = workspace;
            _lastSyncUtc = DateTimeOffset.UtcNow;
            if (isInitial)
            {
                _fullRebuildCount++;
            }
            else if (changedCount > 0)
            {
                _incrementalUpdateCount++;
            }
        }
    }

    /// <summary>Zero-disk lookup of a workspace document's cached entry (including <see cref="SourceText"/> and <see cref="SyntaxTree"/>).</summary>
    public bool TryGetDocument(StaticAnalysisWorkspace workspace, string filePath, out OverlayDocumentEntry? entry, CancellationToken cancellationToken = default)
    {
        SyncWithWorkspace(workspace, cancellationToken);
        string fullPath = Path.GetFullPath(filePath);
        lock (_gate)
        {
            if (_pathToDocId.TryGetValue(fullPath, out int docId) && _docs[docId] is { } found)
            {
                entry = found;
                return true;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Returns candidate <see cref="OverlayDocumentEntry"/> items that may match at least one of
    /// <paramref name="patterns"/>, narrowed via trigram and/or token posting intersection.
    /// </summary>
    public IReadOnlyList<OverlayDocumentEntry> QueryCandidates(
        StaticAnalysisWorkspace workspace,
        IReadOnlyList<string> patterns,
        bool isRegex,
        bool wholeWord,
        string syntaxFilter,
        out int totalIndexedDocuments,
        out int prunedDocuments,
        CancellationToken cancellationToken = default)
    {
        SyncWithWorkspace(workspace, cancellationToken);

        lock (_gate)
        {
            totalIndexedDocuments = _activeDocumentCount;
            if (_activeDocumentCount == 0)
            {
                prunedDocuments = 0;
                return Array.Empty<OverlayDocumentEntry>();
            }

            HashSet<int>? unionCandidates = new();
            bool requiresFullScan = false;
            bool preferTokenFilter = wholeWord ||
                                     string.Equals(syntaxFilter, "identifiers", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(syntaxFilter, "declarations", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(syntaxFilter, "invocations", StringComparison.OrdinalIgnoreCase);

            foreach (string pattern in patterns)
            {
                HashSet<int>? patternCandidates = FindCandidatesForPatternLocked(pattern, isRegex, preferTokenFilter);
                if (patternCandidates is null)
                {
                    requiresFullScan = true;
                    break;
                }

                unionCandidates.UnionWith(patternCandidates);
            }

            List<OverlayDocumentEntry> results;
            if (requiresFullScan)
            {
                results = new List<OverlayDocumentEntry>(_activeDocumentCount);
                for (int i = 0; i < _docs.Count; i++)
                {
                    if (_docs[i] is { } doc)
                    {
                        results.Add(doc);
                    }
                }
            }
            else
            {
                results = new List<OverlayDocumentEntry>(unionCandidates.Count);
                foreach (int docId in unionCandidates)
                {
                    if (docId >= 0 && docId < _docs.Count && _docs[docId] is { } doc)
                    {
                        results.Add(doc);
                    }
                }
            }

            results.Sort(ComparePathsBreadthFirst);
            prunedDocuments = Math.Max(0, totalIndexedDocuments - results.Count);
            return results;
        }
    }

    private static int ComparePathsBreadthFirst(OverlayDocumentEntry a, OverlayDocumentEntry b)
    {
        string dirA = Path.GetDirectoryName(a.FilePath) ?? string.Empty;
        string dirB = Path.GetDirectoryName(b.FilePath) ?? string.Empty;
        int depthA = CountDirectorySeparators(dirA);
        int depthB = CountDirectorySeparators(dirB);
        int depthCmp = depthA.CompareTo(depthB);
        if (depthCmp != 0)
        {
            return depthCmp;
        }

        int dirCmp = PathComparer.Compare(dirA, dirB);
        if (dirCmp != 0)
        {
            return dirCmp;
        }

        return PathComparer.Compare(Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath));
    }

    private static int CountDirectorySeparators(string path)
    {
        int count = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == Path.DirectorySeparatorChar || path[i] == Path.AltDirectorySeparatorChar)
            {
                count++;
            }
        }

        return count;
    }

    public object GetStatsPayload()
    {
        lock (_gate)
        {
            return new
            {
                indexed_documents = _activeDocumentCount,
                trigram_keys = _trigramPostings.Count,
                token_keys = _tokenPostings.Count,
                full_rebuilds = _fullRebuildCount,
                incremental_updates = _incrementalUpdateCount,
                last_sync_utc = _lastSyncUtc,
            };
        }
    }

    private HashSet<int>? FindCandidatesForPatternLocked(string pattern, bool isRegex, bool preferTokenFilter)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        string? literalAnchor = isRegex ? ExtractLongestLiteralAnchor(pattern) : pattern;
        if (string.IsNullOrEmpty(literalAnchor))
        {
            return null;
        }

        List<HashSet<int>> postingLists = new();

        // If searching for an identifier/whole-word token, exact token lookup is O(1) and handles 1-2 char names too.
        if (preferTokenFilter && !isRegex && IsPureIdentifier(literalAnchor))
        {
            string lowerToken = literalAnchor.ToLowerInvariant();
            if (!_tokenPostings.TryGetValue(lowerToken, out HashSet<int>? tokenDocs) || tokenDocs.Count == 0)
            {
                return new HashSet<int>();
            }

            postingLists.Add(tokenDocs);
        }

        if (literalAnchor.Length >= 3)
        {
            string lower = literalAnchor.ToLowerInvariant();
            for (int i = 0; i <= lower.Length - 3; i++)
            {
                ulong tri = PackTrigram(lower[i], lower[i + 1], lower[i + 2]);
                if (!_trigramPostings.TryGetValue(tri, out HashSet<int>? triDocs) || triDocs.Count == 0)
                {
                    return new HashSet<int>();
                }

                postingLists.Add(triDocs);
            }
        }

        if (postingLists.Count == 0)
        {
            return null;
        }

        // Intersect smallest posting list first to minimize work.
        postingLists.Sort((a, b) => a.Count.CompareTo(b.Count));
        HashSet<int> result = new(postingLists[0]);
        for (int i = 1; i < postingLists.Count && result.Count > 0; i++)
        {
            result.IntersectWith(postingLists[i]);
        }

        return result;
    }

    private void AddDocumentLocked(OverlayDocumentEntry entry)
    {
        int docId;
        if (_freeDocIds.Count > 0)
        {
            docId = _freeDocIds.Pop();
            _docs[docId] = entry;
        }
        else
        {
            docId = _docs.Count;
            _docs.Add(entry);
        }

        _pathToDocId[entry.FilePath] = docId;
        _activeDocumentCount++;

        foreach (ulong tri in entry.UniqueTrigrams)
        {
            if (!_trigramPostings.TryGetValue(tri, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                _trigramPostings[tri] = set;
            }

            set.Add(docId);
        }

        foreach (string token in entry.UniqueTokensLower)
        {
            if (!_tokenPostings.TryGetValue(token, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                _tokenPostings[token] = set;
            }

            set.Add(docId);
        }
    }

    private void RemoveDocumentLocked(string fullPath)
    {
        if (!_pathToDocId.TryGetValue(fullPath, out int docId))
        {
            return;
        }

        _pathToDocId.Remove(fullPath);
        OverlayDocumentEntry? old = _docs[docId];
        _docs[docId] = null;
        _freeDocIds.Push(docId);
        _activeDocumentCount--;

        if (old is null)
        {
            return;
        }

        foreach (ulong tri in old.UniqueTrigrams)
        {
            if (_trigramPostings.TryGetValue(tri, out HashSet<int>? set))
            {
                set.Remove(docId);
                if (set.Count == 0)
                {
                    _trigramPostings.Remove(tri);
                }
            }
        }

        foreach (string token in old.UniqueTokensLower)
        {
            if (_tokenPostings.TryGetValue(token, out HashSet<int>? set))
            {
                set.Remove(docId);
                if (set.Count == 0)
                {
                    _tokenPostings.Remove(token);
                }
            }
        }
    }

    internal static OverlayDocumentEntry GetOrBuildEntry(string fullPath, SyntaxTree tree, SourceText sourceText)
    {
        return EntryCache.GetValue(tree, t => BuildEntry(fullPath, t, sourceText));
    }

    private static OverlayDocumentEntry BuildEntry(string fullPath, SyntaxTree tree, SourceText sourceText)
    {
        string fullText = sourceText.ToString();
        HashSet<ulong> trigrams = new();
        HashSet<string> tokens = new(StringComparer.Ordinal);

        int len = fullText.Length;
        char c0 = '\0';
        char c1 = '\0';
        int lineRun = 0;

        for (int i = 0; i < len; i++)
        {
            char raw = fullText[i];
            if (raw == '\r' || raw == '\n')
            {
                lineRun = 0;
                continue;
            }

            char lower = char.ToLowerInvariant(raw);
            if (lineRun >= 2)
            {
                trigrams.Add(PackTrigram(c0, c1, lower));
            }

            c0 = c1;
            c1 = lower;
            lineRun++;
        }

        int index = 0;
        while (index < len)
        {
            char ch = fullText[index];
            if (IsWordStart(ch))
            {
                int start = index++;
                while (index < len && IsWordPart(fullText[index]))
                {
                    index++;
                }

                string word = fullText.Substring(start, index - start).ToLowerInvariant();
                tokens.Add(word);
                continue;
            }

            index++;
        }

        return new OverlayDocumentEntry(
            fullPath,
            tree,
            sourceText,
            fullText,
            trigrams.ToArray(),
            tokens.ToArray());
    }

    /// <summary>
    /// Classifies a text match span inside <paramref name="syntaxTree"/> using Roslyn's AST/trivia (zero semantic
    /// binding cost) so <c>ctx.search_text</c> can filter code vs comments/strings and declarations/invocations/identifiers.
    /// </summary>
    internal static SyntaxMatchClassification ClassifySyntaxMatch(
        SyntaxTree syntaxTree,
        int spanStart,
        int spanLength,
        CancellationToken cancellationToken)
    {
        SyntaxNode root = syntaxTree.GetRoot(cancellationToken);
        int clampedStart = Math.Clamp(spanStart, 0, Math.Max(0, root.FullSpan.End - 1));

        // 1. Check trivia first (comments, XML doc comments, directives).
        SyntaxTrivia trivia = root.FindTrivia(clampedStart, findInsideTrivia: true);
        if (trivia.Span.Contains(clampedStart) && IsCommentTrivia(trivia))
        {
            bool isDoc = IsDocCommentTrivia(trivia);
            return new SyntaxMatchClassification(
                ContextKind: isDoc ? "doc_comment" : "comment",
                IsCode: false,
                IsComment: true,
                IsString: false,
                IsIdentifier: false,
                IsDeclaration: false,
                IsInvocation: false,
                EnclosingSymbol: FindEnclosingSyntaxSymbolName(trivia.Token.Parent));
        }

        SyntaxToken token = root.FindToken(clampedStart, findInsideTrivia: true);
        string? enclosingSymbol = FindEnclosingSyntaxSymbolName(token.Parent);

        // Inside structured XML doc comment tokens (e.g. XmlTextLiteralToken).
        if (IsInsideXmlDocComment(token))
        {
            return new SyntaxMatchClassification(
                ContextKind: "doc_comment",
                IsCode: false,
                IsComment: true,
                IsString: false,
                IsIdentifier: false,
                IsDeclaration: false,
                IsInvocation: false,
                EnclosingSymbol: enclosingSymbol);
        }

        // String or char literal tokens.
        if (IsStringOrCharToken(token))
        {
            return new SyntaxMatchClassification(
                ContextKind: "string",
                IsCode: false,
                IsComment: false,
                IsString: true,
                IsIdentifier: false,
                IsDeclaration: false,
                IsInvocation: false,
                EnclosingSymbol: enclosingSymbol);
        }

        bool isIdentifier = token.IsKind(SyntaxKind.IdentifierToken) ||
                            token.RawKind == (int)VbSyntaxKind.IdentifierToken;
        if (!isIdentifier)
        {
            return new SyntaxMatchClassification(
                ContextKind: "code",
                IsCode: true,
                IsComment: false,
                IsString: false,
                IsIdentifier: false,
                IsDeclaration: false,
                IsInvocation: false,
                EnclosingSymbol: enclosingSymbol);
        }

        bool isDeclaration = IsDeclarationIdentifierToken(token);
        bool isInvocation = !isDeclaration && IsInvocationIdentifierToken(token);

        string kind = isDeclaration
            ? "declaration"
            : isInvocation
                ? "invocation"
                : "identifier";

        return new SyntaxMatchClassification(
            ContextKind: kind,
            IsCode: true,
            IsComment: false,
            IsString: false,
            IsIdentifier: true,
            IsDeclaration: isDeclaration,
            IsInvocation: isInvocation,
            EnclosingSymbol: enclosingSymbol);
    }

    internal static string? FindEnclosingSyntaxSymbolName(SyntaxNode? node)
    {
        if (node is null)
        {
            return null;
        }

        Stack<string> parts = new();
        for (SyntaxNode? cur = node; cur is not null; cur = cur.Parent)
        {
            string? name = cur switch
            {
                CSharpSyntax.MethodDeclarationSyntax m => m.Identifier.ValueText,
                CSharpSyntax.ConstructorDeclarationSyntax c => c.Identifier.ValueText,
                CSharpSyntax.LocalFunctionStatementSyntax lf => lf.Identifier.ValueText,
                CSharpSyntax.PropertyDeclarationSyntax p => p.Identifier.ValueText,
                CSharpSyntax.IndexerDeclarationSyntax => "this[]",
                CSharpSyntax.EventDeclarationSyntax e => e.Identifier.ValueText,
                CSharpSyntax.BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
                CSharpSyntax.DelegateDeclarationSyntax d => d.Identifier.ValueText,
                CSharpSyntax.BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                VbSyntax.MethodStatementSyntax vm => vm.Identifier.ValueText,
                VbSyntax.PropertyStatementSyntax vp => vp.Identifier.ValueText,
                VbSyntax.TypeStatementSyntax vt => vt.Identifier.ValueText,
                VbSyntax.NamespaceStatementSyntax vns => vns.Name.ToString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(name))
            {
                parts.Push(name);
            }
        }

        return parts.Count == 0 ? null : string.Join(".", parts);
    }

    private static bool IsCommentTrivia(SyntaxTrivia trivia)
    {
        if (string.Equals(trivia.Language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return trivia.RawKind is (int)VbSyntaxKind.CommentTrivia
                or (int)VbSyntaxKind.DocumentationCommentTrivia;
        }

        return trivia.Kind() is SyntaxKind.SingleLineCommentTrivia
            or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia
            or SyntaxKind.MultiLineDocumentationCommentTrivia
            or SyntaxKind.DocumentationCommentExteriorTrivia;
    }

    private static bool IsDocCommentTrivia(SyntaxTrivia trivia)
    {
        if (string.Equals(trivia.Language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return trivia.RawKind == (int)VbSyntaxKind.DocumentationCommentTrivia;
        }

        return trivia.Kind() is SyntaxKind.SingleLineDocumentationCommentTrivia
            or SyntaxKind.MultiLineDocumentationCommentTrivia
            or SyntaxKind.DocumentationCommentExteriorTrivia;
    }

    private static bool IsInsideXmlDocComment(SyntaxToken token)
    {
        if (token.Parent is null)
        {
            return false;
        }

        return token.Parent.AncestorsAndSelf().Any(static n =>
            n is CSharpSyntax.DocumentationCommentTriviaSyntax
                or CSharpSyntax.XmlNodeSyntax
                or VbSyntax.DocumentationCommentTriviaSyntax);
    }

    private static bool IsStringOrCharToken(SyntaxToken token)
    {
        if (string.Equals(token.Language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return token.RawKind is (int)VbSyntaxKind.StringLiteralToken
                or (int)VbSyntaxKind.CharacterLiteralToken
                or (int)VbSyntaxKind.InterpolatedStringTextToken;
        }

        return token.Kind() is SyntaxKind.StringLiteralToken
            or SyntaxKind.CharacterLiteralToken
            or SyntaxKind.InterpolatedStringTextToken
            or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken
            or SyntaxKind.Utf8StringLiteralToken
            or SyntaxKind.Utf8SingleLineRawStringLiteralToken
            or SyntaxKind.Utf8MultiLineRawStringLiteralToken;
    }

    private static bool IsDeclarationIdentifierToken(SyntaxToken token)
    {
        SyntaxNode? parent = token.Parent;
        if (parent is null)
        {
            return false;
        }

        return parent switch
        {
            CSharpSyntax.BaseTypeDeclarationSyntax t => t.Identifier == token,
            CSharpSyntax.DelegateDeclarationSyntax d => d.Identifier == token,
            CSharpSyntax.MethodDeclarationSyntax m => m.Identifier == token,
            CSharpSyntax.ConstructorDeclarationSyntax c => c.Identifier == token,
            CSharpSyntax.DestructorDeclarationSyntax d => d.Identifier == token,
            CSharpSyntax.PropertyDeclarationSyntax p => p.Identifier == token,
            CSharpSyntax.EventDeclarationSyntax e => e.Identifier == token,
            CSharpSyntax.VariableDeclaratorSyntax v => v.Identifier == token,
            CSharpSyntax.ParameterSyntax p => p.Identifier == token,
            CSharpSyntax.TypeParameterSyntax tp => tp.Identifier == token,
            CSharpSyntax.LocalFunctionStatementSyntax lf => lf.Identifier == token,
            CSharpSyntax.EnumMemberDeclarationSyntax em => em.Identifier == token,
            CSharpSyntax.ForEachStatementSyntax fe => fe.Identifier == token,
            CSharpSyntax.SingleVariableDesignationSyntax sv => sv.Identifier == token,
            CSharpSyntax.CatchDeclarationSyntax cd => cd.Identifier == token,
            VbSyntax.TypeStatementSyntax vt => vt.Identifier == token,
            VbSyntax.MethodStatementSyntax vm => vm.Identifier == token,
            VbSyntax.PropertyStatementSyntax vp => vp.Identifier == token,
            VbSyntax.ModifiedIdentifierSyntax vmi => vmi.Identifier == token,
            _ => false,
        };
    }

    private static bool IsInvocationIdentifierToken(SyntaxToken token)
    {
        SyntaxNode? parent = token.Parent;
        if (parent is null)
        {
            return false;
        }

        if (parent is CSharpSyntax.SimpleNameSyntax nameNode)
        {
            SyntaxNode? owner = nameNode.Parent;
            if (owner is CSharpSyntax.MemberAccessExpressionSyntax memberAccess && memberAccess.Name == nameNode)
            {
                owner = memberAccess.Parent;
            }
            else if (owner is CSharpSyntax.MemberBindingExpressionSyntax memberBinding && memberBinding.Name == nameNode)
            {
                owner = memberBinding.Parent;
            }
            else if (owner is CSharpSyntax.QualifiedNameSyntax qualifiedName && qualifiedName.Right == nameNode)
            {
                owner = qualifiedName.Parent;
            }

            if (owner is CSharpSyntax.InvocationExpressionSyntax invocation &&
                invocation.Expression.Span.Contains(nameNode.Span))
            {
                return true;
            }

            if (owner is CSharpSyntax.ObjectCreationExpressionSyntax creation &&
                creation.Type.Span.Contains(nameNode.Span))
            {
                return true;
            }

            if (owner is CSharpSyntax.AttributeSyntax attribute &&
                attribute.Name.Span.Contains(nameNode.Span))
            {
                return true;
            }
        }

        if (parent is VbSyntax.IdentifierNameSyntax vbName)
        {
            SyntaxNode? owner = vbName.Parent;
            if (owner is VbSyntax.MemberAccessExpressionSyntax vbMember && vbMember.Name == vbName)
            {
                owner = vbMember.Parent;
            }

            if (owner is VbSyntax.InvocationExpressionSyntax or VbSyntax.ObjectCreationExpressionSyntax)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the longest guaranteed contiguous literal substring from a regex pattern (only when the pattern has no
    /// top-level alternation `|` or optional quantifier immediately following the run) so trigram filtering stays exact
    /// with zero false negatives.
    /// </summary>
    internal static string? ExtractLongestLiteralAnchor(string regexPattern)
    {
        if (string.IsNullOrEmpty(regexPattern))
        {
            return null;
        }

        // Bail out conservatively if top-level alternation exists (an alternation means no single literal is required).
        int parenDepth = 0;
        bool inCharClass = false;
        for (int i = 0; i < regexPattern.Length; i++)
        {
            char c = regexPattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (inCharClass)
            {
                if (c == ']')
                {
                    inCharClass = false;
                }

                continue;
            }

            if (c == '[')
            {
                inCharClass = true;
            }
            else if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                parenDepth = Math.Max(0, parenDepth - 1);
            }
            else if (c == '|' && parenDepth == 0)
            {
                return null;
            }
        }

        StringBuilder current = new();
        string best = string.Empty;
        parenDepth = 0;
        inCharClass = false;

        void Flush()
        {
            if (current.Length > best.Length)
            {
                best = current.ToString();
            }

            current.Clear();
        }

        for (int i = 0; i < regexPattern.Length; i++)
        {
            char c = regexPattern[i];
            if (inCharClass)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == ']')
                {
                    inCharClass = false;
                }

                continue;
            }

            if (c == '[')
            {
                Flush();
                inCharClass = true;
                continue;
            }

            if (c == '(')
            {
                Flush();
                parenDepth++;
                continue;
            }

            if (c == ')')
            {
                Flush();
                parenDepth = Math.Max(0, parenDepth - 1);
                continue;
            }

            if (parenDepth > 0)
            {
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= regexPattern.Length)
                {
                    Flush();
                    break;
                }

                char escaped = regexPattern[++i];
                // Regex shorthand classes/anchors are not literal characters.
                if (escaped is 'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'b' or 'B' or 'A' or 'Z' or 'z' or 'G' or 'p' or 'P' or 'k' or 'x' or 'u' or 'c' ||
                    (escaped >= '0' && escaped <= '9'))
                {
                    Flush();
                    continue;
                }

                char literalChar = escaped switch
                {
                    't' => '\t',
                    'r' => '\r',
                    'n' => '\n',
                    _ => escaped,
                };
                if (IsFollowedByOptionalQuantifier(regexPattern, i))
                {
                    Flush();
                }
                else
                {
                    current.Append(literalChar);
                }

                continue;
            }

            if (c is '.' or '^' or '$' or '*' or '+' or '?' or '{' or '}')
            {
                Flush();
                continue;
            }

            if (IsFollowedByOptionalQuantifier(regexPattern, i))
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return best.Length >= 3 ? best : null;
    }

    private static bool IsFollowedByOptionalQuantifier(string pattern, int index)
    {
        if (index + 1 >= pattern.Length)
        {
            return false;
        }

        char next = pattern[index + 1];
        if (next is '?' or '*')
        {
            return true;
        }

        if (next == '{' && index + 2 < pattern.Length && pattern[index + 2] == '0')
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackTrigram(char c0, char c1, char c2)
        => ((ulong)c0 << 32) | ((ulong)c1 << 16) | c2;

    private static bool IsWordStart(char c)
        => c == '_' || char.IsLetter(c);

    private static bool IsWordPart(char c)
        => c == '_' || char.IsLetterOrDigit(c);

    private static bool IsPureIdentifier(string text)
    {
        if (text.Length == 0 || !IsWordStart(text[0]))
        {
            return false;
        }

        for (int i = 1; i < text.Length; i++)
        {
            if (!IsWordPart(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
