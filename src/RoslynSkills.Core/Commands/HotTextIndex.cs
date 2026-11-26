using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace RoslynSkills.Core.Commands;

/// <summary>One indexed source file: its lines exactly as <see cref="File.ReadAllLines(string)"/> returns them.</summary>
internal sealed record IndexedTextFile(string Path, long WriteTicksUtc, long Length, string[] Lines);

/// <summary>
/// In-memory text of every C#/C# script file under a hot workspace's root, kept current from the workspace's file
/// watcher instead of walking and reading the disk on every query. <c>ctx.search_text</c> over a loaded workspace
/// reads it; the semantic commands use <see cref="IdentifierIndex"/> for candidate-document filtering.
/// </summary>
/// <remarks>
/// The index mirrors what a disk walk would find: files with a <c>.cs</c>/<c>.csx</c> extension that are not
/// generated or build output (<see cref="CommandFileFilters.IsGeneratedPath"/>). It rescans the disk once when the
/// watcher reported something it cannot attribute to a file (error, directory move), and at least every
/// <see cref="HotWorkspaceFreshnessSettings.SafetySweepInterval"/>; between those events a call costs only the
/// files the watcher named.
/// </remarks>
internal sealed class HotTextIndex
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly HashSet<string> SkippedDirectoryNames = new(PathComparer)
    {
        "bin", "obj", ".vs", ".git", "node_modules",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, IndexedTextFile> _files = new(PathComparer);
    private long _seenGeneration = -1;
    private DateTime _lastFullSyncUtc = DateTime.MinValue;

    // Directories whose whole subtree is indexed. A directory is indexed the first time a search names it, so the cost
    // of walking a large repository is paid for the folders actually searched, once, and kept current by the watcher.
    private readonly HashSet<string> _coveredDirectories = new(PathComparer);
    private readonly HashSet<string> _oversizedDirectories = new(PathComparer);
    private const int MaxEntriesPerDirectoryWalk = 60_000;

    public HotTextIndex(string rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? string.Empty : System.IO.Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>Counters for diagnostics and tests.</summary>
    public int FullSyncCount { get; private set; }

    public int IncrementalRefreshCount { get; private set; }

    public static bool IsIndexedPath(string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        return (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".csx", StringComparison.OrdinalIgnoreCase)) &&
               !CommandFileFilters.IsGeneratedPath(path);
    }

    /// <summary>Whether <paramref name="fullPath"/> is the root or lies beneath it.</summary>
    public bool Covers(string fullPath)
    {
        if (RootDirectory.Length == 0)
        {
            return false;
        }

        string normalizedRoot = RootDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            return false;
        }

        return fullPath.Length == normalizedRoot.Length ||
               fullPath[normalizedRoot.Length] == System.IO.Path.DirectorySeparatorChar ||
               fullPath[normalizedRoot.Length] == System.IO.Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Brings the index up to date with the disk, makes sure every directory in <paramref name="scopeDirectories"/> is
    /// indexed, and returns a stable view. A scope that holds more than <see cref="MaxEntriesPerDirectoryWalk"/> entries
    /// is reported in <paramref name="unindexed"/> and must be read from disk by the caller.
    /// </summary>
    public IReadOnlyDictionary<string, IndexedTextFile> Snapshot(
        WorkspaceChangeTracker tracker,
        IReadOnlyList<string> scopeDirectories,
        out IReadOnlySet<string> unindexed)
    {
        lock (_gate)
        {
            string[] dirty = tracker.DrainTextDirtyPaths();
            long generation = tracker.UncertaintyGeneration;
            bool fullSync = _coveredDirectories.Count > 0 &&
                            (!tracker.HasWatcher ||
                             generation != _seenGeneration ||
                             DateTime.UtcNow - _lastFullSyncUtc > HotWorkspaceFreshnessSettings.SafetySweepInterval);
            if (fullSync)
            {
                foreach (string covered in _coveredDirectories.ToArray())
                {
                    WalkDirectoryLocked(covered, replaceExisting: true);
                }

                _oversizedDirectories.Clear();
                _seenGeneration = generation;
                _lastFullSyncUtc = DateTime.UtcNow;
                FullSyncCount++;
            }
            else if (dirty.Length > 0)
            {
                foreach (string path in dirty)
                {
                    RefreshLocked(path);
                }

                IncrementalRefreshCount++;
            }

            HashSet<string> notIndexed = new(PathComparer);
            foreach (string scope in scopeDirectories)
            {
                string normalized = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(scope));
                if (IsUnderCoveredLocked(normalized))
                {
                    continue;
                }

                if (_oversizedDirectories.Contains(normalized))
                {
                    notIndexed.Add(normalized);
                    continue;
                }

                if (_coveredDirectories.Count == 0)
                {
                    _seenGeneration = generation;
                    _lastFullSyncUtc = DateTime.UtcNow;
                }

                if (WalkDirectoryLocked(normalized, replaceExisting: false))
                {
                    foreach (string descendant in _coveredDirectories.Where(dir => IsAtOrBelow(dir, normalized)).ToArray())
                    {
                        _coveredDirectories.Remove(descendant);
                    }

                    _coveredDirectories.Add(normalized);
                    tracker.RegisterIndexedDirectory(normalized);
                }
                else
                {
                    _oversizedDirectories.Add(normalized);
                    notIndexed.Add(normalized);
                }
            }

            unindexed = notIndexed;
            return new Dictionary<string, IndexedTextFile>(_files, PathComparer);
        }
    }

    private bool IsUnderCoveredLocked(string path)
    {
        string directory = System.IO.Path.TrimEndingDirectorySeparator(path);
        foreach (string covered in _coveredDirectories)
        {
            if (IsAtOrBelow(directory, covered))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Orders indexed files beneath <paramref name="directory"/> the way <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// does: a directory's own files by name, then its subdirectories in name order.
    /// </summary>
    public static IEnumerable<IndexedTextFile> EnumerateBeneath(IReadOnlyDictionary<string, IndexedTextFile> snapshot, string directory)
    {
        string root = directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        Dictionary<string, List<IndexedTextFile>> byDirectory = new(PathComparer);
        HashSet<string> directories = new(PathComparer);
        foreach (IndexedTextFile file in snapshot.Values)
        {
            string? dir = System.IO.Path.GetDirectoryName(file.Path);
            if (dir is null || !IsAtOrBelow(dir, root))
            {
                continue;
            }

            if (!byDirectory.TryGetValue(dir, out List<IndexedTextFile>? list))
            {
                byDirectory[dir] = list = new List<IndexedTextFile>();
            }

            list.Add(file);

            string? walk = dir;
            while (walk is not null && !PathComparer.Equals(walk, root) && directories.Add(walk))
            {
                walk = System.IO.Path.GetDirectoryName(walk);
            }
        }

        Dictionary<string, List<string>> children = new(PathComparer);
        foreach (string dir in directories)
        {
            string? parent = System.IO.Path.GetDirectoryName(dir);
            if (parent is null)
            {
                continue;
            }

            if (!children.TryGetValue(parent, out List<string>? list))
            {
                children[parent] = list = new List<string>();
            }

            list.Add(dir);
        }

        Queue<string> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            string current = pending.Dequeue();
            if (byDirectory.TryGetValue(current, out List<IndexedTextFile>? files))
            {
                files.Sort((left, right) => PathComparer.Compare(System.IO.Path.GetFileName(left.Path), System.IO.Path.GetFileName(right.Path)));
                foreach (IndexedTextFile file in files)
                {
                    yield return file;
                }
            }

            if (children.TryGetValue(current, out List<string>? subdirectories))
            {
                subdirectories.Sort((left, right) => PathComparer.Compare(System.IO.Path.GetFileName(left), System.IO.Path.GetFileName(right)));
                foreach (string subdirectory in subdirectories)
                {
                    pending.Enqueue(subdirectory);
                }
            }
        }
    }

    private static bool IsAtOrBelow(string directory, string root)
    {
        if (PathComparer.Equals(directory, root))
        {
            return true;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return directory.Length > root.Length &&
               directory.StartsWith(root, comparison) &&
               (directory[root.Length] == System.IO.Path.DirectorySeparatorChar ||
                directory[root.Length] == System.IO.Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Walks <paramref name="directory"/> and loads or refreshes every indexed source file beneath it. Returns false
    /// (leaving the index untouched) when the subtree is too large to index; with
    /// <paramref name="replaceExisting"/> files under it that no longer exist are dropped.
    /// </summary>
    private bool WalkDirectoryLocked(string directory, bool replaceExisting)
    {
        if (!Directory.Exists(directory))
        {
            if (replaceExisting)
            {
                foreach (string stale in _files.Keys.Where(path => IsAtOrBelow(System.IO.Path.GetDirectoryName(path) ?? string.Empty, directory)).ToArray())
                {
                    _files.Remove(stale);
                }
            }

            return true;
        }

        Dictionary<string, IndexedTextFile> loaded = new(PathComparer);
        HashSet<string> seen = new(PathComparer);
        int budget = MaxEntriesPerDirectoryWalk;
        if (!WalkLocked(new DirectoryInfo(directory), seen, loaded, ref budget))
        {
            return false;
        }

        foreach ((string path, IndexedTextFile file) in loaded)
        {
            _files[path] = file;
        }

        if (replaceExisting)
        {
            foreach (string stale in _files.Keys
                         .Where(path => !seen.Contains(path) && IsAtOrBelow(System.IO.Path.GetDirectoryName(path) ?? string.Empty, directory))
                         .ToArray())
            {
                _files.Remove(stale);
            }
        }

        return true;
    }

    private bool WalkLocked(DirectoryInfo directory, HashSet<string> seen, Dictionary<string, IndexedTextFile> loaded, ref int budget)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        budget -= entries.Length;
        if (budget < 0)
        {
            return false;
        }

        foreach (FileSystemInfo entry in entries)
        {
            if (entry is DirectoryInfo subdirectory)
            {
                if (!SkippedDirectoryNames.Contains(subdirectory.Name) &&
                    (subdirectory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    !WalkLocked(subdirectory, seen, loaded, ref budget))
                {
                    return false;
                }

                continue;
            }

            if (entry is not FileInfo file || !IsIndexedPath(file.FullName))
            {
                continue;
            }

            seen.Add(file.FullName);
            if (_files.TryGetValue(file.FullName, out IndexedTextFile? existing) &&
                existing.WriteTicksUtc == file.LastWriteTimeUtc.Ticks &&
                existing.Length == file.Length)
            {
                continue;
            }

            IndexedTextFile? text = TryLoad(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length);
            if (text is not null)
            {
                loaded[file.FullName] = text;
            }
        }

        return true;
    }

    private void RefreshLocked(string path)
    {
        if (!IsIndexedPath(path) || !IsUnderCoveredLocked(System.IO.Path.GetDirectoryName(path) ?? string.Empty))
        {
            return;
        }

        FileInfo info = new(path);
        if (!info.Exists)
        {
            _files.Remove(path);
            return;
        }

        // A watcher event always re-reads: a write inside the timestamp granularity must not be missed.
        IndexedTextFile? loaded = TryLoad(path, info.LastWriteTimeUtc.Ticks, info.Length);
        if (loaded is not null)
        {
            _files[path] = loaded;
        }
        else
        {
            // Locked mid-write: drop the stale text; the next full sync (or the write's own final event) restores it.
            _files.Remove(path);
            _lastFullSyncUtc = DateTime.MinValue;
        }
    }

    private static IndexedTextFile? TryLoad(string path, long writeTicks, long length)
    {
        try
        {
            return new IndexedTextFile(path, writeTicks, length, File.ReadAllLines(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Per-syntax-tree identifier sets for candidate-document filtering. A tree object is replaced when its document
/// changes, so a <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed by tree is incremental by construction:
/// unchanged documents keep their set, edited ones are re-indexed on first use, removed ones are collected.
/// </summary>
internal static class IdentifierIndex
{
    private static readonly ConditionalWeakTable<SyntaxTree, TreeIdentifiers> Cache = new();

    /// <summary>
    /// False only when <paramref name="tree"/> cannot spell <paramref name="name"/> as an identifier. Trees containing
    /// unicode escapes are always candidates because an escaped identifier may not match its spelled form.
    /// </summary>
    public static bool MightMention(SyntaxTree tree, string name)
    {
        TreeIdentifiers identifiers = GetIdentifiers(tree);
        return identifiers.HasUnicodeEscape || identifiers.Names.Contains(name);
    }

    /// <summary>
    /// False only when <paramref name="tree"/> cannot contain a target-typed <c>new(...)</c> (an object creation that
    /// does not spell its type). A tree with unicode escapes is always a candidate.
    /// </summary>
    public static bool MightHaveImplicitObjectCreation(SyntaxTree tree)
    {
        TreeIdentifiers identifiers = GetIdentifiers(tree);
        return identifiers.HasUnicodeEscape || identifiers.HasNewParen;
    }

    /// <summary>
    /// False only when <paramref name="tree"/> cannot contain a <c>: base(...)</c> / <c>: this(...)</c> constructor
    /// initializer (a constructor call that does not spell the called type).
    /// </summary>
    public static bool MightHaveConstructorInitializer(SyntaxTree tree)
    {
        TreeIdentifiers identifiers = GetIdentifiers(tree);
        return identifiers.HasUnicodeEscape || identifiers.HasConstructorInitializer;
    }

    private static TreeIdentifiers GetIdentifiers(SyntaxTree tree)
        => Cache.GetValue(tree, static t => TreeIdentifiers.Build(t.GetText().ToString()));

    /// <summary>Populates the identifier set for <paramref name="tree"/> ahead of the first query.</summary>
    public static void Warm(SyntaxTree tree)
    {
        _ = GetIdentifiers(tree);
    }

    /// <summary>Whether <paramref name="tree"/> already has a cached identifier set (diagnostics and tests).</summary>
    internal static bool IsCached(SyntaxTree tree) => Cache.TryGetValue(tree, out _);

    private sealed class TreeIdentifiers
    {
        private TreeIdentifiers(HashSet<string> names, bool hasUnicodeEscape, bool hasNewParen, bool hasConstructorInitializer)
        {
            Names = names;
            HasUnicodeEscape = hasUnicodeEscape;
            HasNewParen = hasNewParen;
            HasConstructorInitializer = hasConstructorInitializer;
        }

        public HashSet<string> Names { get; }

        public bool HasUnicodeEscape { get; }

        /// <summary>A <c>new</c> keyword followed by <c>(</c> (or a comment): a possible target-typed creation.</summary>
        public bool HasNewParen { get; }

        /// <summary>A <c>base</c> or <c>this</c> keyword followed by <c>(</c> (or a comment): a possible constructor initializer.</summary>
        public bool HasConstructorInitializer { get; }

        public static TreeIdentifiers Build(string text)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            bool hasEscape = false;
            bool hasNewParen = false;
            bool hasCtorInitializer = false;
            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '\\' && index + 1 < text.Length && (text[index + 1] == 'u' || text[index + 1] == 'U'))
                {
                    hasEscape = true;
                    index += 2;
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    int start = index++;
                    while (index < text.Length && IsIdentifierPart(text[index]))
                    {
                        index++;
                    }

                    int length = index - start;
                    string word = text.Substring(start, length);
                    names.Add(word);
                    if (length == 3 && string.Equals(word, "new", StringComparison.Ordinal))
                    {
                        hasNewParen |= NextSignificantCharStartsCall(text, index);
                    }
                    else if (length == 4 && string.Equals(word, "base", StringComparison.Ordinal) ||
                             length == 4 && string.Equals(word, "this", StringComparison.Ordinal))
                    {
                        hasCtorInitializer |= NextSignificantCharStartsCall(text, index);
                    }

                    continue;
                }

                index++;
            }

            return new TreeIdentifiers(names, hasEscape, hasNewParen, hasCtorInitializer);
        }

        // '(' starts a call; '/' could start a comment before it, so it stays a candidate.
        private static bool NextSignificantCharStartsCall(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return index < text.Length && (text[index] == '(' || text[index] == '/');
        }

        private static bool IsIdentifierStart(char c)
            => c == '_' || char.IsLetter(c) || (c > 127 && char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.LetterNumber);

        private static bool IsIdentifierPart(char c)
            => c == '_' || char.IsLetterOrDigit(c) ||
               (c > 127 && char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
                   or System.Globalization.UnicodeCategory.SpacingCombiningMark
                   or System.Globalization.UnicodeCategory.ConnectorPunctuation
                   or System.Globalization.UnicodeCategory.LetterNumber
                   or System.Globalization.UnicodeCategory.Format);
    }
}
