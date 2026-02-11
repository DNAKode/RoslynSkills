using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSkills.Core.Commands;

internal sealed record WorkspaceContextInfo(
    string mode,
    string resolution_source,
    string? requested_workspace_path,
    string? resolved_workspace_path,
    string? project_path,
    string? fallback_reason,
    IReadOnlyList<string> attempted_workspace_paths,
    IReadOnlyList<string> workspace_diagnostics);

internal sealed record WorkspaceSemanticLoadResult(
    string file_path,
    string source,
    SyntaxTree syntax_tree,
    SyntaxNode root,
    SourceText source_text,
    CSharpCompilation compilation,
    SemanticModel semantic_model,
    WorkspaceContextInfo workspace_context);

internal static class WorkspaceSemanticLoader
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly object MsBuildRegistrationLock = new();
    private static bool _msBuildRegistrationAttempted;
    private static string? _msBuildRegistrationError;

    public static async Task<WorkspaceSemanticLoadResult> LoadForFileAsync(
        string filePath,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        string normalizedFilePath = NormalizePath(filePath);
        WorkspaceCandidatePlan candidatePlan = BuildCandidatePlan(normalizedFilePath, workspacePath);
        List<string> attemptedWorkspacePaths = new();
        List<string> workspaceDiagnostics = new();
        string? fallbackReason = candidatePlan.plan_error;

        if (candidatePlan.candidates.Count > 0)
        {
            if (TryEnsureMsBuildRegistered(out string? registrationError))
            {
                foreach (WorkspaceCandidate candidate in candidatePlan.candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attemptedWorkspacePaths.Add(candidate.path);

                    List<string> candidateDiagnostics = new();
                    try
                    {
                        using MSBuildWorkspace workspace = MSBuildWorkspace.Create();
                        workspace.WorkspaceFailed += (_, args) =>
                        {
                            if (args.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                            {
                                return;
                            }

                            string message = args.Diagnostic.Message;
                            if (ShouldIncludeWorkspaceDiagnostic(message))
                            {
                                candidateDiagnostics.Add(message);
                            }
                        };

                        Solution solution = await OpenSolutionAsync(workspace, candidate, cancellationToken).ConfigureAwait(false);
                        Document? document = FindDocument(solution, normalizedFilePath);
                        if (document is null)
                        {
                            fallbackReason = $"File '{normalizedFilePath}' was not found in workspace candidate '{candidate.path}'.";
                            AddDistinctLimited(workspaceDiagnostics, candidateDiagnostics, maxCount: 30);
                            continue;
                        }

                        SyntaxTree? syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                        SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                        if (syntaxTree is null || semanticModel is null)
                        {
                            fallbackReason = $"Workspace candidate '{candidate.path}' could not provide semantic context for '{normalizedFilePath}'.";
                            AddDistinctLimited(workspaceDiagnostics, candidateDiagnostics, maxCount: 30);
                            continue;
                        }

                        CSharpCompilation? compilation = semanticModel.Compilation as CSharpCompilation;
                        if (compilation is null)
                        {
                            Compilation? projectCompilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                            compilation = projectCompilation as CSharpCompilation;
                        }

                        if (compilation is null)
                        {
                            fallbackReason = $"Workspace candidate '{candidate.path}' returned a non-C# compilation for '{normalizedFilePath}'.";
                            AddDistinctLimited(workspaceDiagnostics, candidateDiagnostics, maxCount: 30);
                            continue;
                        }

                        SourceText sourceText = await syntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                        WorkspaceContextInfo workspaceContext = new(
                            mode: "workspace",
                            resolution_source: candidatePlan.resolution_source,
                            requested_workspace_path: candidatePlan.requested_workspace_path,
                            resolved_workspace_path: candidate.path,
                            project_path: document.Project.FilePath,
                            fallback_reason: null,
                            attempted_workspace_paths: attemptedWorkspacePaths.ToArray(),
                            workspace_diagnostics: candidateDiagnostics
                                .Distinct(StringComparer.Ordinal)
                                .Take(30)
                                .ToArray());

                        return new WorkspaceSemanticLoadResult(
                            file_path: normalizedFilePath,
                            source: sourceText.ToString(),
                            syntax_tree: syntaxTree,
                            root: root,
                            source_text: sourceText,
                            compilation: compilation,
                            semantic_model: semanticModel,
                            workspace_context: workspaceContext);
                    }
                    catch (Exception ex)
                    {
                        fallbackReason = $"Workspace candidate '{candidate.path}' failed to load: {ex.Message}";
                        candidateDiagnostics.Add(ex.Message);
                        AddDistinctLimited(workspaceDiagnostics, candidateDiagnostics, maxCount: 30);
                    }
                }
            }
            else
            {
                fallbackReason = registrationError;
            }
        }

        string source = await File.ReadAllTextAsync(normalizedFilePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree fallbackTree = CSharpSyntaxTree.ParseText(source, path: normalizedFilePath, cancellationToken: cancellationToken);
        SyntaxNode fallbackRoot = await fallbackTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        SourceText fallbackSourceText = fallbackTree.GetText(cancellationToken);
        CSharpCompilation fallbackCompilation = CommandFileAnalysis.CreateCompilation("RoslynSkills.Command", new[] { fallbackTree });
        SemanticModel fallbackSemanticModel = fallbackCompilation.GetSemanticModel(fallbackTree);

        if (string.IsNullOrWhiteSpace(fallbackReason))
        {
            fallbackReason = candidatePlan.candidates.Count == 0
                ? "No workspace candidate (.csproj/.sln/.slnx) could be inferred for this file path."
                : "Workspace resolution failed; using ad-hoc file compilation.";
        }

        WorkspaceContextInfo fallbackContext = new(
            mode: "ad_hoc",
            resolution_source: candidatePlan.resolution_source,
            requested_workspace_path: candidatePlan.requested_workspace_path,
            resolved_workspace_path: null,
            project_path: null,
            fallback_reason: fallbackReason,
            attempted_workspace_paths: attemptedWorkspacePaths.ToArray(),
            workspace_diagnostics: workspaceDiagnostics.ToArray());

        return new WorkspaceSemanticLoadResult(
            file_path: normalizedFilePath,
            source: source,
            syntax_tree: fallbackTree,
            root: fallbackRoot,
            source_text: fallbackSourceText,
            compilation: fallbackCompilation,
            semantic_model: fallbackSemanticModel,
            workspace_context: fallbackContext);
    }

    private static WorkspaceCandidatePlan BuildCandidatePlan(string filePath, string? workspacePath)
    {
        string fileDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
        HashSet<string> seen = new(PathComparer);
        List<WorkspaceCandidate> candidates = new();
        string resolutionSource = string.IsNullOrWhiteSpace(workspacePath) ? "auto" : "explicit";
        string? requestedWorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? null : NormalizePath(workspacePath!);
        string? planError = null;

        if (!string.IsNullOrWhiteSpace(requestedWorkspacePath))
        {
            if (File.Exists(requestedWorkspacePath))
            {
                string extension = Path.GetExtension(requestedWorkspacePath);
                if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCandidate(candidates, seen, requestedWorkspacePath, "project");
                }
                else if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCandidate(candidates, seen, requestedWorkspacePath, "solution");
                }
                else
                {
                    planError = $"Explicit workspace_path '{requestedWorkspacePath}' is not a .csproj/.sln/.slnx file.";
                }
            }
            else if (Directory.Exists(requestedWorkspacePath))
            {
                bool fileUnderWorkspaceRoot = IsPathUnderRoot(filePath, requestedWorkspacePath);
                IReadOnlyList<string> ancestors = fileUnderWorkspaceRoot
                    ? GetAncestorDirectories(fileDirectory, requestedWorkspacePath)
                    : Array.Empty<string>();

                AddCandidatesFromAncestors(ancestors, candidates, seen);

                if (candidates.Count == 0)
                {
                    AddTopLevelDirectoryCandidates(requestedWorkspacePath, candidates, seen);
                }

                if (candidates.Count == 0)
                {
                    AddRecursiveDirectoryCandidates(requestedWorkspacePath, candidates, seen);
                }

                if (candidates.Count == 0)
                {
                    planError = $"No .csproj/.sln/.slnx files were found under explicit workspace_path '{requestedWorkspacePath}'.";
                }
            }
            else
            {
                planError = $"Explicit workspace_path '{requestedWorkspacePath}' does not exist.";
            }

            return new WorkspaceCandidatePlan(resolutionSource, requestedWorkspacePath, planError, candidates);
        }

        IReadOnlyList<string> autoAncestors = GetAncestorDirectories(fileDirectory, stopAtDirectoryInclusive: null);
        AddCandidatesFromAncestors(autoAncestors, candidates, seen);
        if (candidates.Count == 0)
        {
            planError = $"No .csproj/.sln/.slnx files were found while traversing parent directories from '{fileDirectory}'.";
        }

        return new WorkspaceCandidatePlan(resolutionSource, requestedWorkspacePath, planError, candidates);
    }

    private static void AddCandidatesFromAncestors(
        IReadOnlyList<string> ancestors,
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen)
    {
        foreach (string directory in ancestors)
        {
            foreach (string projectPath in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                TryAddCandidate(candidates, seen, projectPath, "project");
            }
        }

        foreach (string directory in ancestors)
        {
            foreach (string solutionPath in EnumerateSolutions(directory))
            {
                TryAddCandidate(candidates, seen, solutionPath, "solution");
            }
        }
    }

    private static void AddTopLevelDirectoryCandidates(
        string directoryPath,
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen)
    {
        foreach (string projectPath in Directory.EnumerateFiles(directoryPath, "*.csproj", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            TryAddCandidate(candidates, seen, projectPath, "project");
        }

        foreach (string solutionPath in EnumerateSolutions(directoryPath, SearchOption.TopDirectoryOnly))
        {
            TryAddCandidate(candidates, seen, solutionPath, "solution");
        }
    }

    private static void AddRecursiveDirectoryCandidates(
        string directoryPath,
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen)
    {
        foreach (string projectPath in Directory.EnumerateFiles(directoryPath, "*.csproj", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            TryAddCandidate(candidates, seen, projectPath, "project");
        }

        foreach (string solutionPath in EnumerateSolutions(directoryPath, SearchOption.AllDirectories))
        {
            TryAddCandidate(candidates, seen, solutionPath, "solution");
        }
    }

    private static IEnumerable<string> EnumerateSolutions(string directoryPath, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.EnumerateFiles(directoryPath, "*.sln", searchOption)
            .Concat(Directory.EnumerateFiles(directoryPath, "*.slnx", searchOption))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryAddCandidate(
        List<WorkspaceCandidate> candidates,
        HashSet<string> seen,
        string candidatePath,
        string kind)
    {
        string normalized = NormalizePath(candidatePath);
        if (!seen.Add(normalized))
        {
            return;
        }

        candidates.Add(new WorkspaceCandidate(normalized, kind));
    }

    private static bool TryEnsureMsBuildRegistered(out string? error)
    {
        lock (MsBuildRegistrationLock)
        {
            if (!_msBuildRegistrationAttempted)
            {
                _msBuildRegistrationAttempted = true;
                try
                {
                    if (!MSBuildLocator.IsRegistered)
                    {
                        VisualStudioInstance? instance = MSBuildLocator.QueryVisualStudioInstances()
                            .OrderByDescending(candidate => candidate.Version)
                            .FirstOrDefault();

                        if (instance is not null)
                        {
                            MSBuildLocator.RegisterInstance(instance);
                        }
                        else
                        {
                            MSBuildLocator.RegisterDefaults();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _msBuildRegistrationError = $"MSBuild locator registration failed: {ex.Message}";
                }
            }
        }

        error = _msBuildRegistrationError;
        return string.IsNullOrWhiteSpace(error);
    }

    private static async Task<Solution> OpenSolutionAsync(
        MSBuildWorkspace workspace,
        WorkspaceCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (string.Equals(candidate.kind, "project", StringComparison.OrdinalIgnoreCase))
        {
            Project project = await workspace.OpenProjectAsync(candidate.path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return project.Solution;
        }

        return await workspace.OpenSolutionAsync(candidate.path, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static Document? FindDocument(Solution solution, string filePath)
    {
        string normalizedFilePath = NormalizePath(filePath);
        foreach (Project project in solution.Projects.Where(p => string.Equals(p.Language, LanguageNames.CSharp, StringComparison.Ordinal)))
        {
            foreach (Document document in project.Documents)
            {
                string? candidatePath = document.FilePath;
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                if (PathComparer.Equals(NormalizePath(candidatePath), normalizedFilePath))
                {
                    return document;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetAncestorDirectories(string startDirectory, string? stopAtDirectoryInclusive)
    {
        List<string> directories = new();
        string? normalizedStop = string.IsNullOrWhiteSpace(stopAtDirectoryInclusive)
            ? null
            : NormalizePath(stopAtDirectoryInclusive);

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string currentPath = NormalizePath(current.FullName);
            directories.Add(currentPath);

            if (!string.IsNullOrWhiteSpace(normalizedStop) &&
                PathComparer.Equals(currentPath, normalizedStop))
            {
                break;
            }

            current = current.Parent;
        }

        return directories;
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        string normalizedPath = NormalizePath(path);
        string normalizedRoot = NormalizePath(root);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return true;
        }

        string rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        string rootWithAltSeparator = normalizedRoot + Path.AltDirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, comparison) ||
               normalizedPath.StartsWith(rootWithAltSeparator, comparison);
    }

    private static void AddDistinctLimited(List<string> destination, IEnumerable<string> source, int maxCount)
    {
        foreach (string value in source)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (destination.Contains(value, StringComparer.Ordinal))
            {
                continue;
            }

            destination.Add(value);
            if (destination.Count >= maxCount)
            {
                return;
            }
        }
    }

    private static bool ShouldIncludeWorkspaceDiagnostic(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (message.IndexOf("known high severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("known moderate severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("known low severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("known critical severity vulnerability", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        if (message.IndexOf("NU1901", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("NU1902", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("NU1903", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("NU1904", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (fullPath.Length > root.Length)
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return fullPath;
    }

    private sealed record WorkspaceCandidate(string path, string kind);

    private sealed record WorkspaceCandidatePlan(
        string resolution_source,
        string? requested_workspace_path,
        string? plan_error,
        IReadOnlyList<WorkspaceCandidate> candidates);
}
