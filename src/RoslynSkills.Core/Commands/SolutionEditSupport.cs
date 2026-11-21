using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace RoslynSkills.Core.Commands;

internal sealed record ChangedSolutionFile(
    string FilePath,
    DocumentId PrimaryDocumentId,
    FileTextSnapshot OriginalSnapshot,
    string UpdatedText,
    int ReplacementCount,
    int[] ChangedLines);

internal sealed record SolutionEditPlan(
    Solution FinalSolution,
    IReadOnlyList<ChangedSolutionFile> ChangedFiles,
    IReadOnlyList<DocumentId> AffectedDocumentIds);

/// <summary>
/// Shared solution-level helpers for semantic edits: XML documentation parsing for cref resolution,
/// symbol normalization for Roslyn Renamer, span-scoped formatting with document options, and
/// line-ending / encoding preservation across all modified files.
/// </summary>
internal static class SolutionEditSupport
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Ensures C# projects parse XML documentation comments (<see cref="DocumentationMode.Parse"/>)
    /// so <c>&lt;see cref="..."/&gt;</c> references are semantically bound and rewritten by Roslyn's
    /// Renamer and SymbolFinder without enabling CS1591 missing-doc warnings.
    /// </summary>
    public static Solution EnsureDocumentationCommentParsing(Solution solution)
    {
        Solution updated = solution;
        foreach (Project project in solution.Projects)
        {
            if (project.ParseOptions is CSharpParseOptions csOptions &&
                csOptions.DocumentationMode == DocumentationMode.None)
            {
                updated = updated.WithProjectParseOptions(
                    project.Id,
                    csOptions.WithDocumentationMode(DocumentationMode.Parse));
            }
        }

        return updated;
    }

    public static ISymbol ResolveRenamableSymbol(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reducedFrom })
        {
            symbol = reducedFrom;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctor &&
            ctor.ContainingType is not null)
        {
            symbol = ctor.ContainingType;
        }

        return symbol.OriginalDefinition;
    }

    public static async Task<Document> FormatChangedSpansAsync(
        Document oldDocument,
        Document newDocument,
        string lineEnding,
        IReadOnlyList<TextSpan>? explicitSpans,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TextSpan> spans;
        if (explicitSpans is not null && explicitSpans.Count > 0)
        {
            spans = explicitSpans;
        }
        else
        {
            SourceText newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TextChange> docChanges = (await newDocument.GetTextChangesAsync(oldDocument, cancellationToken).ConfigureAwait(false))
                .OrderBy(c => c.Span.Start)
                .ToArray();
            spans = ComputeChangedSpansInNewText(docChanges, newText.Length);
        }

        if (spans.Count == 0)
        {
            return newDocument;
        }

#pragma warning disable CS0618 // Document.GetOptionsAsync returns the document's merged .editorconfig/workspace OptionSet.
        OptionSet documentOptions = await newDocument.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
        OptionSet optionsWithLineEnding = documentOptions.WithChangedOption(
            FormattingOptions.NewLine,
            newDocument.Project.Language,
            lineEnding);

        return await Formatter.FormatAsync(
                newDocument,
                spans,
                optionsWithLineEnding,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<SolutionEditPlan> FinalizeSolutionChangesAsync(
        Solution initialSolution,
        Solution editedSolution,
        IReadOnlyDictionary<DocumentId, IReadOnlyList<TextSpan>>? explicitFormatSpans,
        bool formatChangedSpans,
        CancellationToken cancellationToken)
    {
        SolutionChanges changes = editedSolution.GetChanges(initialSolution);
        List<(DocumentId Id, string FilePath)> changedDocs = new();

        foreach (ProjectChanges projectChanges in changes.GetProjectChanges())
        {
            foreach (DocumentId docId in projectChanges.GetChangedDocuments())
            {
                Document? newDoc = editedSolution.GetDocument(docId);
                if (newDoc is null || string.IsNullOrWhiteSpace(newDoc.FilePath))
                {
                    continue;
                }

                string normalizedPath = Path.GetFullPath(newDoc.FilePath);
                if (!File.Exists(normalizedPath))
                {
                    continue;
                }

                changedDocs.Add((docId, normalizedPath));
            }
        }

        Solution finalSolution = editedSolution;
        List<ChangedSolutionFile> changedFiles = new();
        List<DocumentId> affectedDocumentIds = new();
        HashSet<string> processedPaths = new(PathComparer);

        foreach ((DocumentId docId, string filePath) in changedDocs
                     .OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!processedPaths.Add(filePath))
            {
                continue;
            }

            Document? oldDoc = initialSolution.GetDocument(docId);
            Document? newDoc = finalSolution.GetDocument(docId);
            if (oldDoc is null || newDoc is null)
            {
                continue;
            }

            FileTextSnapshot originalSnapshot = await EditFileText.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);

            Document formattedDoc = newDoc;
            if (formatChangedSpans)
            {
                IReadOnlyList<TextSpan>? docExplicitSpans = null;
                explicitFormatSpans?.TryGetValue(docId, out docExplicitSpans);
                formattedDoc = await FormatChangedSpansAsync(
                        oldDoc,
                        newDoc,
                        originalSnapshot.LineEnding,
                        docExplicitSpans,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            SourceText formattedSourceText = await formattedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
            string updatedText = EditFileText.PreserveLineEndings(originalSnapshot, formattedSourceText.ToString());
            if (string.Equals(originalSnapshot.Text, updatedText, StringComparison.Ordinal))
            {
                continue;
            }

            SourceText oldDocText = await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TextChange> textChanges = (await formattedDoc.GetTextChangesAsync(oldDoc, cancellationToken).ConfigureAwait(false))
                .OrderBy(c => c.Span.Start)
                .ToArray();
            int[] changedLines = ComputeChangedLines(oldDocText, textChanges);
            SourceText finalSourceText = SourceText.From(updatedText, originalSnapshot.Encoding);

            // Sync all linked documents sharing this file path in finalSolution.
            foreach (Document linkedDoc in FindDocumentsByPath(finalSolution, filePath))
            {
                finalSolution = finalSolution.WithDocumentText(linkedDoc.Id, finalSourceText);
            }

            affectedDocumentIds.Add(docId);
            changedFiles.Add(new ChangedSolutionFile(
                FilePath: filePath,
                PrimaryDocumentId: docId,
                OriginalSnapshot: originalSnapshot,
                UpdatedText: updatedText,
                ReplacementCount: Math.Max(1, textChanges.Count),
                ChangedLines: changedLines));
        }

        return new SolutionEditPlan(finalSolution, changedFiles, affectedDocumentIds);
    }

    public static IReadOnlyList<TextSpan> ComputeChangedSpansInNewText(IReadOnlyList<TextChange> changes, int newTextLength)
    {
        if (changes.Count == 0)
        {
            return Array.Empty<TextSpan>();
        }

        List<TextSpan> spans = new(changes.Count);
        int delta = 0;
        foreach (TextChange change in changes)
        {
            int newLength = change.NewText?.Length ?? 0;
            int newStart = Math.Clamp(change.Span.Start + delta, 0, newTextLength);
            int clampedLength = Math.Clamp(newLength, 0, newTextLength - newStart);
            if (clampedLength > 0)
            {
                spans.Add(new TextSpan(newStart, clampedLength));
            }

            delta += newLength - change.Span.Length;
        }

        return spans;
    }

    public static int[] ComputeChangedLines(SourceText before, SourceText after)
        => ComputeChangedLines(before, after.GetTextChanges(before));

    public static int[] ComputeChangedLines(SourceText before, IReadOnlyList<TextChange> changes)
    {
        if (changes.Count == 0)
        {
            return Array.Empty<int>();
        }

        HashSet<int> lines = new();
        foreach (TextChange change in changes)
        {
            TextSpan beforeSpan = change.Span;
            if (beforeSpan.Length == 0)
            {
                int insertionPosition = Math.Max(0, Math.Min(before.Length, beforeSpan.Start));
                int insertionLine = before.Lines.GetLineFromPosition(insertionPosition).LineNumber + 1;
                lines.Add(insertionLine);
                continue;
            }

            int startPosition = Math.Max(0, Math.Min(before.Length, beforeSpan.Start));
            int startLine = before.Lines.GetLineFromPosition(startPosition).LineNumber + 1;
            int endPosition = Math.Max(startPosition, Math.Min(before.Length, beforeSpan.End - 1));
            int endLine = before.Lines.GetLineFromPosition(endPosition).LineNumber + 1;
            for (int line = startLine; line <= endLine; line++)
            {
                lines.Add(line);
            }
        }

        return lines.OrderBy(v => v).ToArray();
    }

    public static IEnumerable<Document> FindDocumentsByPath(Solution solution, string filePath)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                if (!string.IsNullOrWhiteSpace(document.FilePath) &&
                    PathComparer.Equals(Path.GetFullPath(document.FilePath), normalizedPath))
                {
                    yield return document;
                }
            }
        }
    }
}
