using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSkills.Core.Commands;

internal static class WorkspaceDiagnostics
{
    public static IReadOnlyList<Diagnostic> GetDiagnosticsForCurrentFile(CommandFileAnalysis analysis, CancellationToken cancellationToken)
    {
        if (!string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return analysis.Compilation.GetDiagnostics(cancellationToken);
        }

        string filePath = Path.GetFullPath(analysis.FilePath);
        return analysis.Compilation
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => IsDiagnosticForFile(diagnostic, filePath))
            .ToArray();
    }

    public static IReadOnlyList<Diagnostic> GetDiagnosticsForUpdatedSource(
        CommandFileAnalysis analysis,
        string updatedSource,
        CancellationToken cancellationToken)
    {
        string filePath = Path.GetFullPath(analysis.FilePath);
        if (!string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            SyntaxTree adHocTree = CommandLanguageServices.ParseSyntaxTree(
                updatedSource,
                filePath,
                analysis.Language,
                cancellationToken);
            return CompilationDiagnostics.GetDiagnostics(new[] { adHocTree }, cancellationToken, analysis.Language);
        }

        SyntaxTree? compilationTree = analysis.Compilation.SyntaxTrees.FirstOrDefault(tree =>
            !string.IsNullOrWhiteSpace(tree.FilePath) &&
            string.Equals(Path.GetFullPath(tree.FilePath), filePath, PathComparison));
        compilationTree ??= analysis.SyntaxTree;

        SyntaxTree updatedTree = CommandLanguageServices.ParseSyntaxTree(
            updatedSource,
            filePath,
            analysis.Language,
            cancellationToken,
            compilationTree.Options);

        try
        {
            // Replace the document syntax tree in the project compilation using the tree's own ParseOptions.
            // This preserves project references, NuGet restore state, implicit usings, and language version.
            Compilation updatedCompilation = analysis.Compilation.ReplaceSyntaxTree(compilationTree, updatedTree);
            SemanticModel semanticModel = updatedCompilation.GetSemanticModel(updatedTree);
            return FilterReportedDiagnostics(semanticModel.GetDiagnostics(cancellationToken: cancellationToken));
        }
        catch
        {
            // Fallback to ad-hoc diagnostics only if the compilation cannot be safely updated in-place.
            SyntaxTree adHocTree = CommandLanguageServices.ParseSyntaxTree(
                updatedSource,
                filePath,
                analysis.Language,
                cancellationToken);
            return CompilationDiagnostics.GetDiagnostics(new[] { adHocTree }, cancellationToken, analysis.Language);
        }
    }

    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsForUpdatedSourceAsync(
        CommandFileAnalysis analysis,
        string updatedSource,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase) ||
            analysis.Document is null)
        {
            return GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
        }

        try
        {
            Solution updatedSolution = analysis.Document.Project.Solution.WithDocumentText(
                analysis.Document.Id,
                SourceText.From(updatedSource, analysis.SourceText.Encoding ?? EditFileText.Utf8NoBom));
            return await GetDiagnosticsForUpdatedSolutionAsync(
                    updatedSolution,
                    new[] { analysis.Document.Id },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
        }
    }

    /// <summary>
    /// Computes diagnostics for the affected documents directly from the updated Roslyn <see cref="Solution"/>,
    /// using each document's project compilation and semantic model (never an ad-hoc compilation).
    /// </summary>
    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsForUpdatedSolutionAsync(
        Solution updatedSolution,
        IEnumerable<DocumentId> affectedDocumentIds,
        CancellationToken cancellationToken)
    {
        List<Diagnostic> diagnostics = new();
        HashSet<string> seenFilePaths = new(PathComparer);

        foreach (DocumentId docId in affectedDocumentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document? document = updatedSolution.GetDocument(docId);
            if (document is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(document.FilePath) &&
                !seenFilePaths.Add(Path.GetFullPath(document.FilePath)))
            {
                continue;
            }

            SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null)
            {
                continue;
            }

            foreach (Diagnostic diagnostic in FilterReportedDiagnostics(semanticModel.GetDiagnostics(cancellationToken: cancellationToken)))
            {
                diagnostics.Add(diagnostic);
            }
        }

        return diagnostics;
    }

    private static IReadOnlyList<Diagnostic> FilterReportedDiagnostics(IEnumerable<Diagnostic> diagnostics)
        => diagnostics
            .Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity != DiagnosticSeverity.Hidden)
            .ToArray();

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsDiagnosticForFile(Diagnostic diagnostic, string filePath)
    {
        if (diagnostic.Location == Location.None || !diagnostic.Location.IsInSource)
        {
            return false;
        }

        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        string candidatePath = string.IsNullOrWhiteSpace(span.Path)
            ? diagnostic.Location.SourceTree?.FilePath ?? string.Empty
            : span.Path;

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        return PathsEqual(filePath, candidatePath);
    }

    private static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }
}
