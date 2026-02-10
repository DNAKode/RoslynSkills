using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSkills.Contracts;
using System.Text;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class CreateFileCommand : IAgentCommand
{
    private static readonly HashSet<string> CSharpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
    };

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.create_file",
        Summary: "Create a new text file in one shot, optionally overwriting and returning C# diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        string normalizedFilePath = Path.GetFullPath(filePath);
        bool overwrite = InputParsing.GetOptionalBool(input, "overwrite", defaultValue: false);
        bool createDirectories = InputParsing.GetOptionalBool(input, "create_directories", defaultValue: true);

        if (File.Exists(normalizedFilePath) && !overwrite)
        {
            errors.Add(new CommandError(
                "file_exists",
                $"File '{normalizedFilePath}' already exists. Set 'overwrite=true' to replace it."));
        }

        string? parentDirectory = Path.GetDirectoryName(normalizedFilePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory) &&
            !Directory.Exists(parentDirectory) &&
            !createDirectories)
        {
            errors.Add(new CommandError(
                "directory_not_found",
                $"Parent directory '{parentDirectory}' does not exist. Set 'create_directories=true' to create it."));
        }

        if (input.TryGetProperty("content", out JsonElement contentProperty) &&
            contentProperty.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError("invalid_input", "Property 'content' must be a string when provided."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> validationErrors = Validate(input).ToList();
        if (validationErrors.Count > 0)
        {
            return new CommandExecutionResult(null, validationErrors);
        }

        string filePath = Path.GetFullPath(input.GetProperty("file_path").GetString()!);
        string content = input.TryGetProperty("content", out JsonElement contentProperty) &&
                         contentProperty.ValueKind == JsonValueKind.String
            ? contentProperty.GetString() ?? string.Empty
            : string.Empty;

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        bool overwrite = InputParsing.GetOptionalBool(input, "overwrite", defaultValue: false);
        bool createDirectories = InputParsing.GetOptionalBool(input, "create_directories", defaultValue: true);
        bool includeDiagnostics = InputParsing.GetOptionalBool(input, "include_diagnostics", defaultValue: true);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 2_000);

        bool existedBefore = File.Exists(filePath);
        string? existingContent = existedBefore
            ? await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false)
            : null;

        bool changed = !existedBefore || !string.Equals(existingContent, content, StringComparison.Ordinal);
        bool wroteFile = false;
        bool createdDirectories = false;

        string? parentDirectory = Path.GetDirectoryName(filePath);
        if (apply && createDirectories && !string.IsNullOrWhiteSpace(parentDirectory) && !Directory.Exists(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
            createdDirectories = true;
        }

        if (apply && (!existedBefore || overwrite) && changed)
        {
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        bool isCSharpFile = IsCSharpFile(filePath);
        object diagnosticsData = new
        {
            evaluated = false,
            reason = isCSharpFile ? "diagnostics_disabled" : "not_csharp_source_file",
            total = 0,
            returned = 0,
            errors = 0,
            warnings = 0,
            diagnostics = Array.Empty<NormalizedDiagnostic>(),
        };

        if (includeDiagnostics && isCSharpFile)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(content, path: filePath, cancellationToken: cancellationToken);
            IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { tree }, cancellationToken);
            NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics)
                .Take(maxDiagnostics)
                .ToArray();
            diagnosticsData = new
            {
                evaluated = true,
                reason = string.Empty,
                total = diagnostics.Count,
                returned = normalized.Length,
                errors = normalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = normalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                diagnostics = normalized,
            };
        }

        object data = new
        {
            file_path = filePath,
            apply_changes = apply,
            overwrite,
            create_directories = createDirectories,
            created_directories = createdDirectories,
            existed_before = existedBefore,
            created = !existedBefore,
            changed,
            wrote_file = wroteFile,
            character_count = content.Length,
            line_count = CountLines(content),
            diagnostics_after_create = diagnosticsData,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        int lines = 1;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private static bool IsCSharpFile(string path)
    {
        string extension = Path.GetExtension(path);
        return CSharpExtensions.Contains(extension);
    }
}
