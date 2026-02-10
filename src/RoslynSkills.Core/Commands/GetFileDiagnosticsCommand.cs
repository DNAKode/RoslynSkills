using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);

        IReadOnlyList<Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { tree }, cancellationToken);
        NormalizedDiagnostic[] payload = CompilationDiagnostics.Normalize(diagnostics);

        object data = new
        {
            file_path = filePath,
            total = payload.Length,
            errors = payload.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
            warnings = payload.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
            diagnostics = payload,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }
}

