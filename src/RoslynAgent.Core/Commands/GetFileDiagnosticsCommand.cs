using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynAgent.Contracts;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

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

        IEnumerable<MetadataReference> references = BuildMetadataReferences();
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "RoslynAgent.InMemory",
            syntaxTrees: new[] { tree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics(cancellationToken);
        DiagnosticPayload[] payload = diagnostics
            .Select(ToPayload)
            .OrderBy(d => d.line)
            .ThenBy(d => d.column)
            .ThenBy(d => d.id, StringComparer.Ordinal)
            .ToArray();

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

    private static IEnumerable<MetadataReference> BuildMetadataReferences()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            string? location = assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            paths.Add(location);
        }

        return paths.Select(path => MetadataReference.CreateFromFile(path));
    }

    private static DiagnosticPayload ToPayload(Diagnostic diagnostic)
    {
        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        int line = span.StartLinePosition.Line >= 0 ? span.StartLinePosition.Line + 1 : 0;
        int column = span.StartLinePosition.Character >= 0 ? span.StartLinePosition.Character + 1 : 0;

        return new DiagnosticPayload(
            id: diagnostic.Id,
            severity: diagnostic.Severity.ToString(),
            message: diagnostic.GetMessage(),
            file_path: string.IsNullOrWhiteSpace(span.Path) ? diagnostic.Location.SourceTree?.FilePath ?? string.Empty : span.Path,
            line: line,
            column: column);
    }

    private sealed record DiagnosticPayload(
        string id,
        string severity,
        string message,
        string file_path,
        int line,
        int column);
}
