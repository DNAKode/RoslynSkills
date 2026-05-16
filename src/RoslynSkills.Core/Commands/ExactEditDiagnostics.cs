using Microsoft.CodeAnalysis;

namespace RoslynSkills.Core.Commands;

internal static class ExactEditDiagnostics
{
    private static readonly HashSet<string> CSharpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
    };

    public static async Task<object> BuildAsync(
        string filePath,
        string content,
        bool includeDiagnostics,
        int maxDiagnostics,
        string? workspacePath,
        string? workspaceHandle,
        CancellationToken cancellationToken)
    {
        bool isCSharpFile = CSharpExtensions.Contains(Path.GetExtension(filePath));
        if (!includeDiagnostics || !isCSharpFile)
        {
            return new
            {
                evaluated = false,
                reason = isCSharpFile ? "diagnostics_disabled" : "not_csharp_source_file",
                mode = "none",
                workspace_context = (object?)null,
                total = 0,
                returned = 0,
                errors = 0,
                warnings = 0,
                diagnostics = Array.Empty<NormalizedDiagnostic>(),
            };
        }

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(
                filePath,
                cancellationToken,
                workspacePath,
                workspaceHandle)
            .ConfigureAwait(false);

        IReadOnlyList<Diagnostic> diagnostics = await WorkspaceDiagnostics.GetDiagnosticsForUpdatedSourceAsync(
                analysis,
                content,
                cancellationToken)
            .ConfigureAwait(false);
        NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics)
            .Take(maxDiagnostics)
            .ToArray();

        return new
        {
            evaluated = true,
            reason = string.Empty,
            mode = string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase)
                ? "workspace_updated_source"
                : "file_updated_source",
            workspace_context = WorkspaceContextPayload.Build(analysis.WorkspaceContext),
            total = diagnostics.Count,
            returned = normalized.Length,
            errors = normalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
            warnings = normalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
            diagnostics = normalized,
        };
    }
}
