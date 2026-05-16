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
        SyntaxTree updatedTree = CommandLanguageServices.ParseSyntaxTree(
            updatedSource,
            filePath,
            analysis.Language,
            cancellationToken);

        if (!string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken, analysis.Language);
        }

        try
        {
            // Replace the document syntax tree in the project compilation. This preserves project references,
            // NuGet restore state, and language version settings where MSBuildWorkspace was successfully loaded.
            SyntaxTree? compilationTree = analysis.Compilation.SyntaxTrees.FirstOrDefault(tree =>
                string.Equals(Path.GetFullPath(tree.FilePath), filePath, PathComparison));
            compilationTree ??= analysis.SyntaxTree;
            Compilation updatedCompilation = analysis.Compilation.ReplaceSyntaxTree(compilationTree, updatedTree);
            return updatedCompilation
                .GetDiagnostics(cancellationToken)
                .Where(diagnostic => IsDiagnosticForFile(diagnostic, filePath))
                .ToArray();
        }
        catch
        {
            // Fallback to ad-hoc diagnostics if the compilation cannot be safely updated in-place.
            return CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken, analysis.Language);
        }
    }

    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsForUpdatedSourceAsync(
        CommandFileAnalysis analysis,
        string updatedSource,
        CancellationToken cancellationToken)
    {
        string filePath = Path.GetFullPath(analysis.FilePath);
        if (!string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase) ||
            analysis.Document is null)
        {
            return GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
        }

        try
        {
            Solution updatedSolution = analysis.Document.Project.Solution.WithDocumentText(
                analysis.Document.Id,
                SourceText.From(updatedSource));
            Document? updatedDocument = updatedSolution.GetDocument(analysis.Document.Id);
            Compilation? updatedCompilation = updatedDocument is null
                ? null
                : await updatedDocument.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (updatedCompilation is null)
            {
                return GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
            }

            return updatedCompilation
                .GetDiagnostics(cancellationToken)
                .Where(diagnostic => IsDiagnosticForFile(diagnostic, filePath))
                .ToArray();
        }
        catch
        {
            return GetDiagnosticsForUpdatedSource(analysis, updatedSource, cancellationToken);
        }
    }

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
