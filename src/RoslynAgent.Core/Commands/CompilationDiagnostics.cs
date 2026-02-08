using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;

namespace RoslynAgent.Core.Commands;

internal static class CompilationDiagnostics
{
    public static ImmutableArray<Diagnostic> GetDiagnostics(
        IReadOnlyList<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "RoslynAgent.InMemory",
            syntaxTrees: syntaxTrees,
            references: CompilationReferenceBuilder.BuildMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics(cancellationToken);
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
