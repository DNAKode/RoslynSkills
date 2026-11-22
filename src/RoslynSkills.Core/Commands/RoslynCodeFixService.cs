using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Reflection;

namespace RoslynSkills.Core.Commands;

internal enum CodeFixTier
{
    Primary = 1,
    Secondary = 2,
    ExplicitOnly = 3,
}

internal sealed record RoslynCodeFixCandidate(
    string Title,
    string ProviderName,
    string? EquivalenceKey,
    CodeActionPriority Priority,
    CodeFixTier Tier,
    CodeAction Action,
    Diagnostic Diagnostic)
{
    public bool AutoSelectable => Tier == CodeFixTier.Primary;

    public object ToMetadata() => new
    {
        title = Title,
        provider = ProviderName,
        equivalence_key = EquivalenceKey,
        priority = Priority.ToString(),
        tier = Tier switch
        {
            CodeFixTier.Primary => "primary",
            CodeFixTier.Secondary => "secondary",
            _ => "explicit_only",
        },
        auto_selectable = AutoSelectable,
    };
}

internal sealed record RoslynCodeFixApplyOutcome(
    bool Ok,
    SolutionEditPlan? EditPlan,
    IReadOnlyList<AddedSolutionFile> AddedFiles,
    CommandError? Error);

internal sealed record AddedSolutionFile(
    string FilePath,
    DocumentId DocumentId,
    string Text);

/// <summary>
/// Discovers and invokes Roslyn's built-in C# <see cref="CodeFixProvider"/>s from
/// <c>Microsoft.CodeAnalysis.Features</c> and <c>Microsoft.CodeAnalysis.CSharp.Features</c>
/// over a loaded <see cref="Solution"/> (or an ad-hoc workspace for standalone files).
/// </summary>
internal static class RoslynCodeFixService
{
    private static readonly HashSet<string> BlockedDiagnosticIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Missing required argument: never guess a method/constructor overload or stub.
        "CS7036",
    };

    private static readonly HashSet<string> ExcludedProviderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddPackage",
        "AddMissingReference",
        "UpgradeProject",
        "UpdateProjectToAllowUnsafe",
        "CopilotImplementNotImplementedException",
        "ConflictMarkerResolution",
    };

    private static readonly HashSet<string> SecondaryProviderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FullyQualify",
    };

    private static readonly HashSet<string> ExplicitOnlyProviderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "GenerateMethod",
        "GenerateVariable",
        "GenerateType",
        "GenerateConstructor",
        "GenerateDefaultConstructors",
        "GenerateEnumMember",
        "GenerateConversion",
        "GenerateDeconstructMethod",
        "AddParameter",
        "SpellCheck",
    };

    private static readonly Lazy<ProviderCatalog> Catalog = new(LoadProviderCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsDiagnosticBlocked(string diagnosticId)
        => BlockedDiagnosticIds.Contains(diagnosticId);

    public static async Task<(Document Document, Diagnostic Diagnostic, IDisposable? OwnedWorkspace)> PrepareDocumentAndDiagnosticAsync(
        CommandFileAnalysis analysis,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        if (analysis.Document is not null)
        {
            return (analysis.Document, diagnostic, null);
        }

        // Ad-hoc fallback for standalone .cs files without a project/solution.
        AdhocWorkspace workspace = new(MefHostServices.DefaultHost);
        try
        {
            ProjectId projectId = ProjectId.CreateNewId();
            DocumentId documentId = DocumentId.CreateNewId(projectId);
            ProjectInfo projectInfo = ProjectInfo.Create(
                id: projectId,
                version: VersionStamp.Create(),
                name: "RoslynSkills.AdHocCodeFix",
                assemblyName: "RoslynSkills.AdHocCodeFix",
                language: analysis.Language,
                parseOptions: analysis.SyntaxTree.Options,
                compilationOptions: analysis.Compilation.Options,
                metadataReferences: analysis.Compilation.References);

            Solution solution = workspace.CurrentSolution
                .AddProject(projectInfo)
                .AddDocument(
                    DocumentInfo.Create(
                        id: documentId,
                        name: Path.GetFileName(analysis.FilePath),
                        loader: TextLoader.From(TextAndVersion.Create(analysis.SourceText, VersionStamp.Create(), analysis.FilePath)),
                        filePath: analysis.FilePath));

            Document document = solution.GetDocument(documentId)!;
            SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            Diagnostic effectiveDiagnostic = diagnostic;
            if (model is not null)
            {
                Diagnostic? matched = model.GetDiagnostics(cancellationToken: cancellationToken)
                    .FirstOrDefault(d =>
                        string.Equals(d.Id, diagnostic.Id, StringComparison.OrdinalIgnoreCase) &&
                        d.Location.SourceSpan == diagnostic.Location.SourceSpan);
                if (matched is not null)
                {
                    effectiveDiagnostic = matched;
                }
            }

            return (document, effectiveDiagnostic, workspace);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public static async Task<IReadOnlyList<RoslynCodeFixCandidate>> GetCodeFixesAsync(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        if (IsDiagnosticBlocked(diagnostic.Id))
        {
            return Array.Empty<RoslynCodeFixCandidate>();
        }

        List<RoslynCodeFixCandidate> candidates = new();
        HashSet<(string Provider, string Title)> seen = new(ProviderTitleComparer.Instance);

        if (Catalog.Value.ProvidersByDiagnosticId.TryGetValue(diagnostic.Id, out IReadOnlyList<RegisteredProvider>? providers))
        {
            foreach (RegisteredProvider registered in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<CodeAction> registeredActions = new();
                CodeFixContext context = new(
                    document,
                    diagnostic,
                    (action, _) => registeredActions.Add(action),
                    cancellationToken);

                try
                {
                    await registered.Provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Never let an individual Roslyn provider exception abort discovery across remaining providers.
                    continue;
                }

                foreach (CodeAction rawAction in registeredActions)
                {
                    foreach (CodeAction leaf in FlattenCodeActions(rawAction))
                    {
                        if (!IsEligibleAction(registered.Name, leaf))
                        {
                            continue;
                        }

                        if (!seen.Add((registered.Name, leaf.Title)))
                        {
                            continue;
                        }

                        candidates.Add(new RoslynCodeFixCandidate(
                            Title: leaf.Title,
                            ProviderName: registered.Name,
                            EquivalenceKey: leaf.EquivalenceKey,
                            Priority: leaf.Priority,
                            Tier: registered.Tier,
                            Action: leaf,
                            Diagnostic: diagnostic));
                    }
                }
            }
        }

        // Built-in deterministic syntax fixes for compiler using diagnostics that Roslyn's
        // RemoveUnnecessaryImportsCodeFixProvider does not register under compiler diagnostic IDs.
        if (candidates.Count == 0 && IsUsingCleanupDiagnostic(diagnostic.Id))
        {
            RoslynCodeFixCandidate? usingCleanup = CreateUsingCleanupCandidate(document, diagnostic);
            if (usingCleanup is not null && seen.Add((usingCleanup.ProviderName, usingCleanup.Title)))
            {
                candidates.Add(usingCleanup);
            }
        }

        return candidates;
    }

    public static async Task<RoslynCodeFixApplyOutcome> ApplyFixAsync(
        RoslynCodeFixCandidate candidate,
        Solution initialSolution,
        CancellationToken cancellationToken)
    {
        ImmutableArray<CodeActionOperation> operations;
        try
        {
            operations = await candidate.Action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RoslynCodeFixApplyOutcome(
                Ok: false,
                EditPlan: null,
                AddedFiles: Array.Empty<AddedSolutionFile>(),
                Error: new CommandError("code_fix_failed", $"Roslyn code fix '{candidate.Title}' failed while computing operations: {ex.Message}"));
        }

        ApplyChangesOperation? applyChanges = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (applyChanges is null)
        {
            return new RoslynCodeFixApplyOutcome(
                Ok: false,
                EditPlan: null,
                AddedFiles: Array.Empty<AddedSolutionFile>(),
                Error: new CommandError(
                    "unsupported_code_fix_operation",
                    $"Roslyn code fix '{candidate.Title}' did not produce a solution-change operation."));
        }

        Solution editedSolution = applyChanges.ChangedSolution;
        SolutionEditPlan editPlan = await SolutionEditSupport.FinalizeSolutionChangesAsync(
                initialSolution,
                editedSolution,
                explicitFormatSpans: null,
                formatChangedSpans: true,
                cancellationToken)
            .ConfigureAwait(false);

        List<AddedSolutionFile> addedFiles = new();
        SolutionChanges solutionChanges = editedSolution.GetChanges(initialSolution);
        foreach (ProjectChanges projectChanges in solutionChanges.GetProjectChanges())
        {
            foreach (DocumentId addedDocId in projectChanges.GetAddedDocuments())
            {
                Document? addedDoc = editedSolution.GetDocument(addedDocId);
                if (addedDoc is null || string.IsNullOrWhiteSpace(addedDoc.FilePath))
                {
                    continue;
                }

                SourceText addedText = await addedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                addedFiles.Add(new AddedSolutionFile(
                    FilePath: Path.GetFullPath(addedDoc.FilePath),
                    DocumentId: addedDocId,
                    Text: addedText.ToString()));
            }
        }

        return new RoslynCodeFixApplyOutcome(
            Ok: true,
            EditPlan: editPlan,
            AddedFiles: addedFiles,
            Error: null);
    }

    public static bool IsImplementInterfaceProvider(string providerName)
        => string.Equals(providerName, "ImplementInterface", StringComparison.OrdinalIgnoreCase);

    public static bool IsAddImportProvider(string providerName)
        => string.Equals(providerName, "AddImport", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsingCleanupDiagnostic(string diagnosticId)
        => string.Equals(diagnosticId, "CS0105", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(diagnosticId, "CS8019", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(diagnosticId, "IDE0005", StringComparison.OrdinalIgnoreCase);

    private static RoslynCodeFixCandidate? CreateUsingCleanupCandidate(Document document, Diagnostic diagnostic)
    {
        bool isDuplicate = string.Equals(diagnostic.Id, "CS0105", StringComparison.OrdinalIgnoreCase);
        string title = isDuplicate ? "Remove duplicate using directive" : "Remove unnecessary using directive";
        string providerName = isDuplicate ? "RemoveDuplicateUsing" : "RemoveUnnecessaryUsing";

        CodeAction action = CodeAction.Create(
            title: title,
            createChangedDocument: ct => ApplyUsingCleanupAsync(document, diagnostic, isDuplicate, ct),
            equivalenceKey: $"{providerName}:{diagnostic.Id}");

        return new RoslynCodeFixCandidate(
            Title: title,
            ProviderName: providerName,
            EquivalenceKey: action.EquivalenceKey,
            Priority: CodeActionPriority.Default,
            Tier: CodeFixTier.Primary,
            Action: action,
            Diagnostic: diagnostic);
    }

    private static async Task<Document> ApplyUsingCleanupAsync(
        Document document,
        Diagnostic diagnostic,
        bool isDuplicate,
        CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return document;
        }

        if (isDuplicate)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<UsingDirectiveSyntax> kept = new(compilationUnit.Usings.Count);
            foreach (UsingDirectiveSyntax usingDirective in compilationUnit.Usings)
            {
                string key = $"{usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)}::{usingDirective.Alias?.Name}::{usingDirective.Name}";
                if (seen.Add(key))
                {
                    kept.Add(usingDirective);
                }
            }

            if (kept.Count == compilationUnit.Usings.Count)
            {
                return document;
            }

            return document.WithSyntaxRoot(compilationUnit.WithUsings(SyntaxFactory.List(kept)));
        }

        FileLinePositionSpan diagSpan = diagnostic.Location.GetLineSpan();
        int targetLine = diagSpan.StartLinePosition.Line;
        List<UsingDirectiveSyntax> filtered = compilationUnit.Usings
            .Where(u => u.GetLocation().GetLineSpan().StartLinePosition.Line != targetLine)
            .ToList();

        if (filtered.Count == compilationUnit.Usings.Count)
        {
            return document;
        }

        return document.WithSyntaxRoot(compilationUnit.WithUsings(SyntaxFactory.List(filtered)));
    }

    private static IEnumerable<CodeAction> FlattenCodeActions(CodeAction action)
    {
        if (!action.NestedActions.IsDefaultOrEmpty)
        {
            foreach (CodeAction child in action.NestedActions)
            {
                foreach (CodeAction leaf in FlattenCodeActions(child))
                {
                    yield return leaf;
                }
            }

            yield break;
        }

        yield return action;
    }

    private static bool IsEligibleAction(string providerName, CodeAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
        {
            return false;
        }

        // Never offer NuGet package installation or project-reference mutation fixes in headless CLI mode.
        if (action.Title.StartsWith("Install package", StringComparison.OrdinalIgnoreCase) ||
            action.Title.StartsWith("Find and install", StringComparison.OrdinalIgnoreCase) ||
            action.Title.StartsWith("Add reference to", StringComparison.OrdinalIgnoreCase) ||
            action.Title.StartsWith("Add project reference", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsAddImportProvider(providerName))
        {
            string trimmed = action.Title.Trim();
            if (!trimmed.StartsWith("using ", StringComparison.Ordinal) &&
                !trimmed.StartsWith("global using ", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ProviderCatalog LoadProviderCatalog()
    {
        ContainerConfiguration configuration = new ContainerConfiguration()
            .WithAssemblies(MSBuildMefHostServices.DefaultAssemblies);
        CompositionHost container = configuration.CreateContainer();

        List<RegisteredProvider> rawProviders = new();
        HashSet<Type> seenTypes = new();

        foreach (CodeFixProvider provider in container.GetExports<CodeFixProvider>())
        {
            Type providerType = provider.GetType();
            if (!seenTypes.Add(providerType))
            {
                continue;
            }

            ExportCodeFixProviderAttribute? exportAttribute = providerType.GetCustomAttribute<ExportCodeFixProviderAttribute>();
            if (exportAttribute is null || !exportAttribute.Languages.Contains(LanguageNames.CSharp, StringComparer.Ordinal))
            {
                continue;
            }

            string providerName = string.IsNullOrWhiteSpace(exportAttribute.Name)
                ? providerType.Name
                : exportAttribute.Name;
            if (ExcludedProviderNames.Contains(providerName))
            {
                continue;
            }

            CodeFixTier tier = ClassifyTier(providerName);
            ExtensionOrderAttribute[] orderAttributes = providerType.GetCustomAttributes<ExtensionOrderAttribute>().ToArray();
            rawProviders.Add(new RegisteredProvider(provider, providerName, tier, orderAttributes));
        }

        IReadOnlyList<RegisteredProvider> ordered = OrderProviders(rawProviders);
        Dictionary<string, List<RegisteredProvider>> byDiagnostic = new(StringComparer.OrdinalIgnoreCase);

        foreach (RegisteredProvider registered in ordered)
        {
            ImmutableArray<string> fixableIds;
            try
            {
                fixableIds = registered.Provider.FixableDiagnosticIds;
            }
            catch
            {
                continue;
            }

            if (fixableIds.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (string id in fixableIds)
            {
                if (string.IsNullOrWhiteSpace(id) || BlockedDiagnosticIds.Contains(id))
                {
                    continue;
                }

                if (!byDiagnostic.TryGetValue(id, out List<RegisteredProvider>? list))
                {
                    list = new List<RegisteredProvider>();
                    byDiagnostic[id] = list;
                }

                list.Add(registered);
            }
        }

        Dictionary<string, IReadOnlyList<RegisteredProvider>> readOnlyMap = byDiagnostic.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<RegisteredProvider>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        return new ProviderCatalog(container, ordered, readOnlyMap);
    }

    private static CodeFixTier ClassifyTier(string providerName)
    {
        if (SecondaryProviderNames.Contains(providerName))
        {
            return CodeFixTier.Secondary;
        }

        if (ExplicitOnlyProviderNames.Contains(providerName))
        {
            return CodeFixTier.ExplicitOnly;
        }

        return CodeFixTier.Primary;
    }

    private static IReadOnlyList<RegisteredProvider> OrderProviders(List<RegisteredProvider> providers)
    {
        // Topological sort honoring Roslyn [ExtensionOrder(Before=..., After=...)], with deterministic tie-breaking:
        // Tier ascending (Primary -> Secondary -> ExplicitOnly), then AddImport/ImplementInterface first, then Name.
        Dictionary<string, List<int>> nameToIndices = new(StringComparer.Ordinal);
        for (int i = 0; i < providers.Count; i++)
        {
            string name = providers[i].Name;
            if (!nameToIndices.TryGetValue(name, out List<int>? indices))
            {
                indices = new List<int>();
                nameToIndices[name] = indices;
            }

            indices.Add(i);
        }

        List<HashSet<int>> edges = Enumerable.Range(0, providers.Count).Select(_ => new HashSet<int>()).ToList();
        int[] inDegree = new int[providers.Count];

        for (int i = 0; i < providers.Count; i++)
        {
            foreach (ExtensionOrderAttribute order in providers[i].OrderAttributes)
            {
                if (!string.IsNullOrWhiteSpace(order.Before) &&
                    nameToIndices.TryGetValue(order.Before, out List<int>? beforeTargets))
                {
                    foreach (int target in beforeTargets)
                    {
                        if (target != i && edges[i].Add(target))
                        {
                            inDegree[target]++;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(order.After) &&
                    nameToIndices.TryGetValue(order.After, out List<int>? afterTargets))
                {
                    foreach (int prerequisite in afterTargets)
                    {
                        if (prerequisite != i && edges[prerequisite].Add(i))
                        {
                            inDegree[i]++;
                        }
                    }
                }
            }
        }

        List<int> ready = new();
        for (int i = 0; i < providers.Count; i++)
        {
            if (inDegree[i] == 0)
            {
                ready.Add(i);
            }
        }

        Comparison<int> priorityCompare = (left, right) =>
        {
            RegisteredProvider a = providers[left];
            RegisteredProvider b = providers[right];
            int tierCmp = ((int)a.Tier).CompareTo((int)b.Tier);
            if (tierCmp != 0)
            {
                return tierCmp;
            }

            int boostCmp = GetProviderSortBoost(a.Name).CompareTo(GetProviderSortBoost(b.Name));
            if (boostCmp != 0)
            {
                return boostCmp;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        };

        List<RegisteredProvider> result = new(providers.Count);
        bool[] visited = new bool[providers.Count];

        while (ready.Count > 0)
        {
            ready.Sort(priorityCompare);
            int current = ready[0];
            ready.RemoveAt(0);
            if (visited[current])
            {
                continue;
            }

            visited[current] = true;
            result.Add(providers[current]);

            foreach (int next in edges[current])
            {
                inDegree[next]--;
                if (inDegree[next] == 0 && !visited[next])
                {
                    ready.Add(next);
                }
            }
        }

        // Cycle safety: append any unvisited nodes in deterministic priority order.
        if (result.Count < providers.Count)
        {
            List<int> remaining = Enumerable.Range(0, providers.Count).Where(i => !visited[i]).ToList();
            remaining.Sort(priorityCompare);
            foreach (int idx in remaining)
            {
                result.Add(providers[idx]);
            }
        }

        return result;
    }

    private static int GetProviderSortBoost(string providerName)
    {
        if (string.Equals(providerName, "AddImport", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(providerName, "ImplementInterface", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(providerName, "ImplementAbstractClass", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 10;
    }

    private sealed record RegisteredProvider(
        CodeFixProvider Provider,
        string Name,
        CodeFixTier Tier,
        ExtensionOrderAttribute[] OrderAttributes);

    private sealed record ProviderCatalog(
        CompositionHost Container,
        IReadOnlyList<RegisteredProvider> OrderedProviders,
        IReadOnlyDictionary<string, IReadOnlyList<RegisteredProvider>> ProvidersByDiagnosticId);

    private sealed class ProviderTitleComparer : IEqualityComparer<(string Provider, string Title)>
    {
        public static readonly ProviderTitleComparer Instance = new();

        public bool Equals((string Provider, string Title) x, (string Provider, string Title) y)
            => string.Equals(x.Provider, y.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Title, y.Title, StringComparison.Ordinal);

        public int GetHashCode((string Provider, string Title) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Provider),
                StringComparer.Ordinal.GetHashCode(obj.Title));
    }
}
