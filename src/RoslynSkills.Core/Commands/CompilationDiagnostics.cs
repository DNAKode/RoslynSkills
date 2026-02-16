using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace RoslynSkills.Core.Commands;

internal static class CompilationDiagnostics
{
    public static ImmutableArray<Diagnostic> GetDiagnostics(
        IReadOnlyList<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken,
        string? language = null)
    {
        if (syntaxTrees.Count == 0)
        {
            return ImmutableArray<Diagnostic>.Empty;
        }

        string resolvedLanguage = language
            ?? syntaxTrees[0].Options.Language
            ?? LanguageNames.CSharp;
        Compilation compilation = CommandLanguageServices.CreateCompilation(
            assemblyName: "RoslynSkills.InMemory",
            syntaxTrees: syntaxTrees,
            language: resolvedLanguage);

        return compilation.GetDiagnostics(cancellationToken);
    }

    public static ImmutableArray<Diagnostic> GetDiagnostics(
        IReadOnlyList<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken)
    {
        return GetDiagnostics(syntaxTrees, cancellationToken, language: null);
    }

    public static NormalizedDiagnostic[] Normalize(IReadOnlyList<Diagnostic> diagnostics)
    {
        return diagnostics
            .Select(ToPayload)
            .OrderBy(d => d.line)
            .ThenBy(d => d.column)
            .ThenBy(d => d.id, StringComparer.Ordinal)
            .ToArray();
    }

    private static NormalizedDiagnostic ToPayload(Diagnostic diagnostic)
    {
        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        int line = span.StartLinePosition.Line >= 0 ? span.StartLinePosition.Line + 1 : 0;
        int column = span.StartLinePosition.Character >= 0 ? span.StartLinePosition.Character + 1 : 0;

        return new NormalizedDiagnostic(
            id: diagnostic.Id,
            severity: diagnostic.Severity.ToString(),
            message: diagnostic.GetMessage(),
            file_path: string.IsNullOrWhiteSpace(span.Path) ? diagnostic.Location.SourceTree?.FilePath ?? string.Empty : span.Path,
            line: line,
            column: column);
    }
}

internal sealed record NormalizedDiagnostic(
    string id,
    string severity,
    string message,
    string file_path,
    int line,
    int column);

