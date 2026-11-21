using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
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
        Summary: "Create a new text file in one shot, joining the hot workspace project and returning diagnostics.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true,
        RunsOnHotWorkspace: true);

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

        WorkspaceInput.ValidateOptionalWorkspacePath(input, errors);
        WorkspaceInput.ValidateOptionalWorkspaceHandle(input, errors);
        InputParsing.ValidateOptionalBool(input, "require_workspace", errors);

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
        string? workspacePath = WorkspaceInput.GetOptionalWorkspacePath(input);
        string? workspaceHandle = WorkspaceInput.GetOptionalWorkspaceHandle(input);
        bool requireWorkspace = InputParsing.GetOptionalBool(input, "require_workspace", defaultValue: false);

        bool existedBefore = File.Exists(filePath);
        FileTextSnapshot? existingSnapshot = existedBefore
            ? await EditFileText.ReadAsync(filePath, cancellationToken).ConfigureAwait(false)
            : null;
        Encoding targetEncoding = existingSnapshot?.Encoding ?? EditFileText.Utf8NoBom;

        bool changed = !existedBefore || !string.Equals(existingSnapshot?.Text, content, StringComparison.Ordinal);
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
            await EditFileText.WriteAsync(filePath, content, targetEncoding, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        object hotWorkspaceRefresh = await HotWorkspaceEditRefresh.RefreshAfterWriteAsync(
                new[] { filePath },
                wroteFile,
                allowAddDocuments: true,
                cancellationToken)
            .ConfigureAwait(false);

        bool isCSharpFile = IsCSharpFile(filePath);
        object diagnosticsData = new
        {
            evaluated = false,
            reason = isCSharpFile ? "diagnostics_disabled" : "not_csharp_source_file",
            mode = "none",
            total = 0,
            returned = 0,
            errors = 0,
            warnings = 0,
            diagnostics = Array.Empty<NormalizedDiagnostic>(),
        };

        if (includeDiagnostics && isCSharpFile)
        {
            (IReadOnlyList<Diagnostic>? solutionDiagnostics, string? diagnosticMode) =
                await TryEvaluateSolutionDiagnosticsAsync(
                        filePath,
                        content,
                        targetEncoding,
                        workspacePath,
                        workspaceHandle,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (requireWorkspace && solutionDiagnostics is null)
            {
                return new CommandExecutionResult(
                    null,
                    new[]
                    {
                        new CommandError(
                            "workspace_required",
                            $"Command '{Descriptor.Id}' required a workspace, but no loaded project contains '{filePath}'."),
                    });
            }

            IReadOnlyList<Diagnostic> diagnostics;
            string effectiveMode;
            if (solutionDiagnostics is not null)
            {
                diagnostics = solutionDiagnostics;
                effectiveMode = diagnosticMode ?? "workspace_updated_source";
            }
            else
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(content, path: filePath, cancellationToken: cancellationToken);
                diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { tree }, cancellationToken);
                effectiveMode = "file_ad_hoc";
            }

            NormalizedDiagnostic[] allNormalized = CompilationDiagnostics.Normalize(diagnostics);
            NormalizedDiagnostic[] normalized = allNormalized
                .Take(maxDiagnostics)
                .ToArray();
            diagnosticsData = new
            {
                evaluated = true,
                reason = string.Empty,
                mode = effectiveMode,
                total = diagnostics.Count,
                returned = normalized.Length,
                errors = allNormalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = allNormalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                diagnostics = normalized,
            };
        }

        object data = new
        {
            file_path = filePath,
            workspace_path = workspacePath,
            workspace_handle = workspaceHandle,
            require_workspace = requireWorkspace,
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
            hot_workspace_refresh = hotWorkspaceRefresh,
            diagnostics_after_create = diagnosticsData,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static async Task<(IReadOnlyList<Diagnostic>? Diagnostics, string? Mode)> TryEvaluateSolutionDiagnosticsAsync(
        string filePath,
        string content,
        Encoding encoding,
        string? workspacePath,
        string? workspaceHandle,
        CancellationToken cancellationToken)
    {
        Solution? baseSolution = TryFindHostedSolution(filePath, workspaceHandle);
        if (baseSolution is null && !string.IsNullOrWhiteSpace(workspacePath))
        {
            (StaticAnalysisWorkspace? loaded, CommandError? _) = await StaticAnalysisWorkspace.LoadAsync(
                    workspacePath,
                    includeGenerated: false,
                    maxFiles: 20_000,
                    cancellationToken)
                .ConfigureAwait(false);
            baseSolution = loaded?.Solution;
        }

        if (baseSolution is null && File.Exists(filePath))
        {
            CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(
                    filePath,
                    cancellationToken,
                    workspacePath,
                    workspaceHandle)
                .ConfigureAwait(false);
            if (analysis.Document is not null)
            {
                baseSolution = analysis.Document.Project.Solution;
            }
        }

        if (baseSolution is null)
        {
            return (null, null);
        }

        SourceText sourceText = SourceText.From(content, encoding);
        Solution updatedSolution = baseSolution;
        List<DocumentId> affectedDocumentIds = new();

        Document[] existingDocs = SolutionEditSupport.FindDocumentsByPath(updatedSolution, filePath).ToArray();
        if (existingDocs.Length > 0)
        {
            foreach (Document doc in existingDocs)
            {
                updatedSolution = updatedSolution.WithDocumentText(doc.Id, sourceText);
                affectedDocumentIds.Add(doc.Id);
            }
        }
        else
        {
            IReadOnlyList<Project> containingProjects = StaticAnalysisWorkspace.FindContainingProjectsForNewSourceFile(updatedSolution, filePath);
            if (containingProjects.Count == 0)
            {
                return (null, null);
            }

            string fileName = Path.GetFileName(filePath);
            foreach (Project targetProject in containingProjects)
            {
                Project? currentProject = updatedSolution.GetProject(targetProject.Id);
                if (currentProject is null || string.IsNullOrWhiteSpace(currentProject.FilePath))
                {
                    continue;
                }

                string projectDir = Path.GetDirectoryName(Path.GetFullPath(currentProject.FilePath)) ?? string.Empty;
                string fileDir = Path.GetDirectoryName(filePath) ?? projectDir;
                string relativeDir = Path.GetRelativePath(projectDir, fileDir);
                string[] folders = string.IsNullOrWhiteSpace(relativeDir) || string.Equals(relativeDir, ".", StringComparison.Ordinal)
                    ? Array.Empty<string>()
                    : relativeDir.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

                Document added = currentProject.AddDocument(fileName, sourceText, folders, filePath);
                updatedSolution = added.Project.Solution;
                affectedDocumentIds.Add(added.Id);
            }
        }

        if (affectedDocumentIds.Count == 0)
        {
            return (null, null);
        }

        IReadOnlyList<Diagnostic> diagnostics = await WorkspaceDiagnostics.GetDiagnosticsForUpdatedSolutionAsync(
                updatedSolution,
                affectedDocumentIds,
                cancellationToken)
            .ConfigureAwait(false);
        return (diagnostics, "workspace_updated_source");
    }

    private static Solution? TryFindHostedSolution(string filePath, string? workspaceHandle)
    {
        IWorkspaceHostStore store = WorkspaceHostStoreProvider.Current;
        if (!string.IsNullOrWhiteSpace(workspaceHandle) &&
            store.TryGet(workspaceHandle.Trim(), out HostedWorkspace? explicitWorkspace) &&
            explicitWorkspace?.Workspace.Solution is { } explicitSolution)
        {
            return explicitSolution;
        }

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        foreach (HostedWorkspace workspace in store.List())
        {
            if (workspace.Workspace.Solution is not { } solution)
            {
                continue;
            }

            if (workspace.TrackedSourcePaths.Contains(filePath, pathComparer) ||
                StaticAnalysisWorkspace.FindContainingProjectsForNewSourceFile(solution, filePath).Count > 0)
            {
                return solution;
            }
        }

        return null;
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
