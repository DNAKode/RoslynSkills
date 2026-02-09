using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

internal static class RoslynSessionStore
{
    private static readonly string StoreDirectory = Path.Combine(Path.GetTempPath(), "roslyn-agent-sessions");
    private static readonly ConcurrentDictionary<string, RoslynSession> Sessions = new(StringComparer.Ordinal);

    public static int Count
    {
        get
        {
            EnsureStoreDirectory();
            return Directory.GetFiles(StoreDirectory, "*.json", SearchOption.TopDirectoryOnly).Length;
        }
    }

    public static string CreateSessionId()
        => $"s-{Guid.NewGuid():N}";

    public static bool TryAdd(RoslynSession session)
    {
        EnsureStoreDirectory();
        string path = GetSessionPath(session.SessionId);
        if (File.Exists(path))
        {
            return false;
        }

        if (!TryPersistSession(path, session))
        {
            return false;
        }

        Sessions[session.SessionId] = session;
        return true;
    }

    public static bool TryGet(string sessionId, out RoslynSession? session)
    {
        if (Sessions.TryGetValue(sessionId, out session))
        {
            return true;
        }

        EnsureStoreDirectory();
        string path = GetSessionPath(sessionId);
        if (!File.Exists(path))
        {
            session = null;
            return false;
        }

        try
        {
            string json = File.ReadAllText(path);
            PersistedSessionState? state = JsonSerializer.Deserialize<PersistedSessionState>(json);
            if (state is null)
            {
                session = null;
                return false;
            }

            session = RoslynSession.FromPersistedState(state);
            Sessions[sessionId] = session;
            return true;
        }
        catch
        {
            session = null;
            return false;
        }
    }

    public static bool Persist(RoslynSession session)
    {
        EnsureStoreDirectory();
        string path = GetSessionPath(session.SessionId);
        if (!TryPersistSession(path, session))
        {
            return false;
        }

        Sessions[session.SessionId] = session;
        return true;
    }

    public static bool TryRemove(string sessionId, out RoslynSession? session)
    {
        Sessions.TryRemove(sessionId, out session);

        EnsureStoreDirectory();
        string path = GetSessionPath(sessionId);
        bool existedOnDisk = File.Exists(path);
        if (existedOnDisk)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                return false;
            }
        }

        return session is not null || existedOnDisk;
    }

    private static bool TryPersistSession(string path, RoslynSession session)
    {
        try
        {
            PersistedSessionState state = session.ToPersistedState();
            string json = JsonSerializer.Serialize(state);
            File.WriteAllText(path, json, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetSessionPath(string sessionId)
        => Path.Combine(StoreDirectory, $"{sessionId}.json");

    private static void EnsureStoreDirectory()
    {
        if (!Directory.Exists(StoreDirectory))
        {
            Directory.CreateDirectory(StoreDirectory);
        }
    }
}

internal sealed class RoslynSession
{
    private readonly object _gate = new();
    private readonly string _originalSource;
    private readonly string _openDiskHash;
    private SourceText _currentSourceText;
    private string _currentSourceHash;
    private SyntaxTree _currentSyntaxTree;
    private CSharpCompilation _compilation;
    private int _generation;

    private RoslynSession(
        string sessionId,
        string filePath,
        string originalSource,
        SourceText currentSourceText,
        SyntaxTree currentSyntaxTree,
        CSharpCompilation compilation,
        string openDiskHash,
        int generation)
    {
        SessionId = sessionId;
        FilePath = filePath;
        _originalSource = originalSource;
        _openDiskHash = openDiskHash;
        _currentSourceText = currentSourceText;
        _currentSourceHash = ComputeContentHash(currentSourceText.ToString());
        _currentSyntaxTree = currentSyntaxTree;
        _compilation = compilation;
        _generation = Math.Max(0, generation);
    }

    public string SessionId { get; }

    public string FilePath { get; }

    public int CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public static async Task<RoslynSession> CreateAsync(string sessionId, string filePath, CancellationToken cancellationToken)
    {
        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        SourceText sourceText = syntaxTree.GetText(cancellationToken);
        CSharpCompilation compilation = CommandFileAnalysis.CreateCompilation(
            assemblyName: $"RoslynAgent.Session.{sessionId}",
            syntaxTrees: new[] { syntaxTree });

        return new RoslynSession(
            sessionId: sessionId,
            filePath: filePath,
            originalSource: source,
            currentSourceText: sourceText,
            currentSyntaxTree: syntaxTree,
            compilation: compilation,
            openDiskHash: ComputeContentHash(source),
            generation: 0);
    }

    public static RoslynSession FromPersistedState(PersistedSessionState state)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(state.current_source, path: state.file_path);
        SourceText sourceText = syntaxTree.GetText();
        CSharpCompilation compilation = CommandFileAnalysis.CreateCompilation(
            assemblyName: $"RoslynAgent.Session.{state.session_id}",
            syntaxTrees: new[] { syntaxTree });

        return new RoslynSession(
            sessionId: state.session_id,
            filePath: state.file_path,
            originalSource: state.original_source,
            currentSourceText: sourceText,
            currentSyntaxTree: syntaxTree,
            compilation: compilation,
            openDiskHash: string.IsNullOrWhiteSpace(state.open_disk_hash)
                ? ComputeContentHash(state.original_source)
                : state.open_disk_hash!,
            generation: state.generation);
    }

    public PersistedSessionState ToPersistedState()
    {
        lock (_gate)
        {
            return new PersistedSessionState(
                session_id: SessionId,
                file_path: FilePath,
                original_source: _originalSource,
                current_source: _currentSourceText.ToString(),
                open_disk_hash: _openDiskHash,
                generation: _generation);
        }
    }

    public SessionSnapshot BuildSnapshot(int maxDiagnostics, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return BuildSnapshotLocked(maxDiagnostics, cancellationToken);
        }
    }

    public SessionUpdateResult SetContent(string newContent, int maxDiagnostics, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            int previousGeneration = _generation;
            SourceText previous = _currentSourceText;
            SourceText updated = SourceText.From(newContent, previous.Encoding ?? Encoding.UTF8);
            if (string.Equals(previous.ToString(), updated.ToString(), StringComparison.Ordinal))
            {
                SessionSnapshot snapshot = BuildSnapshotLocked(maxDiagnostics, cancellationToken);
                return new SessionUpdateResult(
                    changed: false,
                    changed_lines: Array.Empty<int>(),
                    previous_generation: previousGeneration,
                    snapshot: snapshot);
            }

            SyntaxTree updatedTree = _currentSyntaxTree.WithChangedText(updated);
            CSharpCompilation updatedCompilation = _compilation.ReplaceSyntaxTree(_currentSyntaxTree, updatedTree);

            _currentSourceText = updated;
            _currentSourceHash = ComputeContentHash(updated.ToString());
            _currentSyntaxTree = updatedTree;
            _compilation = updatedCompilation;
            _generation++;

            int[] changedLines = ComputeChangedLines(previous, _currentSourceText);
            SessionSnapshot currentSnapshot = BuildSnapshotLocked(maxDiagnostics, cancellationToken);
            return new SessionUpdateResult(
                changed: changedLines.Length > 0,
                changed_lines: changedLines,
                previous_generation: previousGeneration,
                snapshot: currentSnapshot);
        }
    }

    public SessionUpdateResult ApplyTextEdits(IReadOnlyList<SessionTextEdit> edits, int maxDiagnostics, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            int previousGeneration = _generation;
            if (edits.Count == 0)
            {
                SessionSnapshot snapshot = BuildSnapshotLocked(maxDiagnostics, cancellationToken);
                return new SessionUpdateResult(
                    changed: false,
                    changed_lines: Array.Empty<int>(),
                    previous_generation: previousGeneration,
                    snapshot: snapshot);
            }

            SourceText previous = _currentSourceText;
            List<TextChange> textChanges = BuildTextChanges(previous, edits);
            SourceText updated = previous.WithChanges(textChanges);
            if (string.Equals(previous.ToString(), updated.ToString(), StringComparison.Ordinal))
            {
                SessionSnapshot snapshot = BuildSnapshotLocked(maxDiagnostics, cancellationToken);
                return new SessionUpdateResult(
                    changed: false,
                    changed_lines: Array.Empty<int>(),
                    previous_generation: previousGeneration,
                    snapshot: snapshot);
            }

            SyntaxTree updatedTree = _currentSyntaxTree.WithChangedText(updated);
            CSharpCompilation updatedCompilation = _compilation.ReplaceSyntaxTree(_currentSyntaxTree, updatedTree);

            _currentSourceText = updated;
            _currentSourceHash = ComputeContentHash(updated.ToString());
            _currentSyntaxTree = updatedTree;
            _compilation = updatedCompilation;
            _generation++;

            int[] changedLines = ComputeChangedLines(previous, _currentSourceText);
            SessionSnapshot currentSnapshot = BuildSnapshotLocked(maxDiagnostics, cancellationToken);
            return new SessionUpdateResult(
                changed: changedLines.Length > 0,
                changed_lines: changedLines,
                previous_generation: previousGeneration,
                snapshot: currentSnapshot);
        }
    }

    public SessionDiffResult BuildDiff(int maxChanges)
    {
        lock (_gate)
        {
            SourceText original = SourceText.From(_originalSource, _currentSourceText.Encoding ?? Encoding.UTF8);
            int maxLineCount = Math.Max(original.Lines.Count, _currentSourceText.Lines.Count);
            List<SessionDiffLine> changes = new();
            int totalChanged = 0;
            bool truncated = false;

            for (int lineIndex = 0; lineIndex < maxLineCount; lineIndex++)
            {
                string before = lineIndex < original.Lines.Count
                    ? original.Lines[lineIndex].ToString()
                    : string.Empty;
                string after = lineIndex < _currentSourceText.Lines.Count
                    ? _currentSourceText.Lines[lineIndex].ToString()
                    : string.Empty;

                if (string.Equals(before, after, StringComparison.Ordinal))
                {
                    continue;
                }

                totalChanged++;
                if (changes.Count >= maxChanges)
                {
                    truncated = true;
                    continue;
                }

                changes.Add(new SessionDiffLine(
                    line: lineIndex + 1,
                    before: before,
                    after: after));
            }

            return new SessionDiffResult(
                session_id: SessionId,
                file_path: FilePath,
                total_changed_lines: totalChanged,
                returned_changed_lines: changes.Count,
                truncated: truncated,
                changes: changes);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        string content;
        lock (_gate)
        {
            content = _currentSourceText.ToString();
        }

        await File.WriteAllTextAsync(FilePath, content, cancellationToken).ConfigureAwait(false);
    }

    public SessionStatus GetStatus()
    {
        lock (_gate)
        {
            return BuildStatusLocked();
        }
    }

    private SessionSnapshot BuildSnapshotLocked(int maxDiagnostics, CancellationToken cancellationToken)
    {
        IReadOnlyList<Diagnostic> diagnostics = _compilation.GetDiagnostics(cancellationToken);
        NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics)
            .Take(maxDiagnostics)
            .ToArray();

        int errorCount = normalized.Count(d =>
            string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase));
        int warningCount = normalized.Count(d =>
            string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase));

        bool hasChanges = !string.Equals(_originalSource, _currentSourceText.ToString(), StringComparison.Ordinal);
        return new SessionSnapshot(
            session_id: SessionId,
            file_path: FilePath,
            generation: _generation,
            line_count: _currentSourceText.Lines.Count,
            character_count: _currentSourceText.Length,
            has_changes: hasChanges,
            total_diagnostics: diagnostics.Count,
            returned_diagnostics: normalized.Length,
            errors: errorCount,
            warnings: warningCount,
            diagnostics: normalized);
    }

    private SessionStatus BuildStatusLocked()
    {
        bool hasChanges = !string.Equals(_originalSource, _currentSourceText.ToString(), StringComparison.Ordinal);
        bool diskExists = File.Exists(FilePath);
        string? diskHash = null;
        bool diskMatchesOpen = false;
        bool diskMatchesCurrent = false;

        if (diskExists)
        {
            string diskSource = File.ReadAllText(FilePath);
            diskHash = ComputeContentHash(diskSource);
            diskMatchesOpen = string.Equals(diskHash, _openDiskHash, StringComparison.Ordinal);
            diskMatchesCurrent = string.Equals(diskHash, _currentSourceHash, StringComparison.Ordinal);
        }

        string syncState;
        string recommendedAction;

        if (!diskExists)
        {
            syncState = "missing_on_disk";
            recommendedAction = "restore_file_or_close_session_without_commit";
        }
        else if (!diskMatchesOpen && hasChanges && !diskMatchesCurrent)
        {
            syncState = "diverged";
            recommendedAction = "review_session.diff_then_commit_with_require_disk_unchanged_false_or_close_reopen";
        }
        else if (!diskMatchesOpen && !hasChanges)
        {
            syncState = "disk_changed_external";
            recommendedAction = "close_and_reopen_session_before_more_edits";
        }
        else if (diskMatchesCurrent && hasChanges)
        {
            syncState = "committed_not_closed";
            recommendedAction = "close_session_or_continue_incremental_edits";
        }
        else if (hasChanges)
        {
            syncState = "in_memory_changes";
            recommendedAction = "run_session.get_diagnostics_and_session.diff_then_commit_or_close";
        }
        else
        {
            syncState = "in_sync";
            recommendedAction = "safe_to_continue";
        }

        return new SessionStatus(
            session_id: SessionId,
            file_path: FilePath,
            generation: _generation,
            has_changes: hasChanges,
            disk_exists: diskExists,
            disk_matches_open: diskMatchesOpen,
            disk_matches_current: diskMatchesCurrent,
            open_disk_hash: HashPrefix(_openDiskHash),
            current_content_hash: HashPrefix(_currentSourceHash),
            disk_hash: diskHash is null ? null : HashPrefix(diskHash),
            sync_state: syncState,
            recommended_action: recommendedAction);
    }

    private static int[] ComputeChangedLines(SourceText before, SourceText after)
    {
        int lineCount = Math.Max(before.Lines.Count, after.Lines.Count);
        List<int> changedLines = new();

        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            string beforeLine = lineIndex < before.Lines.Count
                ? before.Lines[lineIndex].ToString()
                : string.Empty;
            string afterLine = lineIndex < after.Lines.Count
                ? after.Lines[lineIndex].ToString()
                : string.Empty;

            if (!string.Equals(beforeLine, afterLine, StringComparison.Ordinal))
            {
                changedLines.Add(lineIndex + 1);
            }
        }

        return changedLines.ToArray();
    }

    private static List<TextChange> BuildTextChanges(SourceText sourceText, IReadOnlyList<SessionTextEdit> edits)
    {
        List<(TextSpan span, string text)> resolved = new();
        for (int index = 0; index < edits.Count; index++)
        {
            SessionTextEdit edit = edits[index];

            int start = GetPositionFromLineColumn(sourceText, edit.start_line, edit.start_column, allowLineEnd: true);
            int end = GetPositionFromLineColumn(sourceText, edit.end_line, edit.end_column, allowLineEnd: true);
            if (end < start)
            {
                throw new ArgumentException($"Edit at index {index} has end before start.");
            }

            resolved.Add((new TextSpan(start, end - start), edit.new_text ?? string.Empty));
        }

        (TextSpan span, string text)[] ordered = resolved
            .OrderBy(item => item.span.Start)
            .ThenBy(item => item.span.End)
            .ToArray();

        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i - 1].span.End > ordered[i].span.Start)
            {
                throw new ArgumentException($"Edits overlap at sorted indexes {i - 1} and {i}.");
            }
        }

        return ordered
            .Select(item => new TextChange(item.span, item.text))
            .ToList();
    }

    private static int GetPositionFromLineColumn(SourceText sourceText, int line, int column, bool allowLineEnd)
    {
        if (line < 1 || line > sourceText.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(line), $"Line '{line}' is outside the valid range 1..{sourceText.Lines.Count}.");
        }

        TextLine textLine = sourceText.Lines[line - 1];
        int requestedOffset = Math.Max(0, column - 1);
        int maxOffset = allowLineEnd
            ? textLine.Span.Length
            : Math.Max(0, textLine.Span.Length - 1);
        int clampedOffset = Math.Min(requestedOffset, maxOffset);
        return textLine.Start + clampedOffset;
    }

    private static string ComputeContentHash(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string HashPrefix(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        return hash.Length <= 12 ? hash : hash[..12];
    }
}

internal sealed record SessionSnapshot(
    string session_id,
    string file_path,
    int generation,
    int line_count,
    int character_count,
    bool has_changes,
    int total_diagnostics,
    int returned_diagnostics,
    int errors,
    int warnings,
    IReadOnlyList<NormalizedDiagnostic> diagnostics);

internal sealed record SessionUpdateResult(
    bool changed,
    IReadOnlyList<int> changed_lines,
    int previous_generation,
    SessionSnapshot snapshot);

internal sealed record SessionDiffLine(
    int line,
    string before,
    string after);

internal sealed record SessionDiffResult(
    string session_id,
    string file_path,
    int total_changed_lines,
    int returned_changed_lines,
    bool truncated,
    IReadOnlyList<SessionDiffLine> changes);

internal sealed record SessionTextEdit(
    int start_line,
    int start_column,
    int end_line,
    int end_column,
    string new_text);

internal sealed record PersistedSessionState(
    string session_id,
    string file_path,
    string original_source,
    string current_source,
    string? open_disk_hash = null,
    int generation = 0);

internal sealed record SessionStatus(
    string session_id,
    string file_path,
    int generation,
    bool has_changes,
    bool disk_exists,
    bool disk_matches_open,
    bool disk_matches_current,
    string open_disk_hash,
    string current_content_hash,
    string? disk_hash,
    string sync_state,
    string recommended_action);
