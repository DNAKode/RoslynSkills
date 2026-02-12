using Microsoft.CodeAnalysis;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class GetFileDiagnosticsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "diag.get_file_diagnostics",
        Summary: "Compile a C# file in-memory and return normalized diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError(
                "file_not_found",
                $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError("file_not_found", $"Input file '{filePath}' does not exist."),
                });
        }

        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);
        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(
            filePath,
            cancellationToken,
            workspacePath).ConfigureAwait(false);

        if (requireWorkspace &&
            !string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            string fallbackReason = string.IsNullOrWhiteSpace(analysis.WorkspaceContext.fallback_reason)
                ? "Workspace context could not be resolved."
                : analysis.WorkspaceContext.fallback_reason;
            return new CommandExecutionResult(
                null,
                new[]
                {
                    new CommandError(
                        "workspace_required",
                        $"Command '{Descriptor.Id}' requires workspace context for '{analysis.FilePath}', but mode was '{analysis.WorkspaceContext.mode}'. {fallbackReason} Pass workspace_path (.csproj/.sln/.slnx or containing directory) and retry."),
                });
        }

        IReadOnlyList<Diagnostic> diagnostics = GetDiagnosticsForFile(analysis, cancellationToken);
        NormalizedDiagnostic[] payload = CompilationDiagnostics.Normalize(diagnostics);

        object data = new
        {
            file_path = analysis.FilePath,
            workspace_context = BuildWorkspaceContextPayload(analysis.WorkspaceContext),
            require_workspace = requireWorkspace,
            total = payload.Length,
            errors = payload.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
            warnings = payload.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
            diagnostics = payload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static IReadOnlyList<Diagnostic> GetDiagnosticsForFile(CommandFileAnalysis analysis, CancellationToken cancellationToken)
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

    private static object BuildWorkspaceContextPayload(WorkspaceContextInfo context)
    {
        return new
        {
            mode = context.mode,
            resolution_source = context.resolution_source,
            requested_workspace_path = context.requested_workspace_path,
            resolved_workspace_path = context.resolved_workspace_path,
            project_path = context.project_path,
            fallback_reason = context.fallback_reason,
            attempted_workspace_paths = context.attempted_workspace_paths,
            workspace_diagnostics = context.workspace_diagnostics,
        };
    }
}

