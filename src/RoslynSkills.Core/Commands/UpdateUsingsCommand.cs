using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class UpdateUsingsCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.update_usings",
        Summary: "Add, remove, sort, and optionally prune unused using directives.",
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

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
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
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        bool apply = InputParsing.GetOptionalBool(input, "apply", defaultValue: true);
        bool sort = InputParsing.GetOptionalBool(input, "sort", defaultValue: true);
        bool removeUnused = InputParsing.GetOptionalBool(input, "remove_unused", defaultValue: false);
        int maxDiagnostics = InputParsing.GetOptionalInt(input, "max_diagnostics", defaultValue: 50, minValue: 1, maxValue: 500);

        string[] addNamespaces = InputParsing.GetOptionalStringArray(input, "add_namespaces");
        string[] removeNamespaces = InputParsing.GetOptionalStringArray(input, "remove_namespaces");
        HashSet<string> removeSet = removeNamespaces.ToHashSet(StringComparer.Ordinal);
        HashSet<string> addedSet = addNamespaces.ToHashSet(StringComparer.Ordinal);
        HashSet<string> removedUnusedSet = new(StringComparer.Ordinal);

        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_target", "The input file is not a C# compilation unit.") });
        }

        List<UsingDirectiveSyntax> usingList = compilationUnit.Usings.ToList();

        if (removeUnused)
        {
            RemoveUnusedUsings(filePath, syntaxTree, usingList, removedUnusedSet, cancellationToken);
        }

        if (removeSet.Count > 0)
        {
            usingList = usingList
                .Where(u => !removeSet.Contains(GetUsingNamespace(u)))
                .ToList();
        }

        foreach (string namespaceToAdd in addNamespaces)
        {
            if (usingList.Any(u => string.Equals(GetUsingNamespace(u), namespaceToAdd, StringComparison.Ordinal)))
            {
                continue;
            }

            usingList.Add(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceToAdd)));
        }

        if (sort)
        {
            usingList = usingList
                .OrderBy(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? 0 : 1)
                .ThenBy(GetUsingNamespace, StringComparer.Ordinal)
                .ToList();
        }

        CompilationUnitSyntax updatedCompilationUnit = compilationUnit.WithUsings(SyntaxFactory.List(usingList));
        string updatedSource = updatedCompilationUnit.ToFullString();
        bool changed = !string.Equals(source, updatedSource, StringComparison.Ordinal);

        SyntaxTree updatedTree = CSharpSyntaxTree.ParseText(updatedSource, path: filePath, cancellationToken: cancellationToken);
        IReadOnlyList<Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { updatedTree }, cancellationToken);
        NormalizedDiagnostic[] normalized = CompilationDiagnostics.Normalize(diagnostics).Take(maxDiagnostics).ToArray();

        bool wroteFile = false;
        if (apply && changed)
        {
            await File.WriteAllTextAsync(filePath, updatedSource, cancellationToken).ConfigureAwait(false);
            wroteFile = true;
        }

        HashSet<string> finalUsings = usingList
            .Select(GetUsingNamespace)
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .ToHashSet(StringComparer.Ordinal);
        string[] actuallyAdded = addNamespaces.Where(finalUsings.Contains).ToArray();
        string[] actuallyRemoved = removeNamespaces.Where(ns => !finalUsings.Contains(ns)).ToArray();

        object data = new
        {
            file_path = filePath,
            apply_changes = apply,
            wrote_file = wroteFile,
            changed,
            added_namespaces = actuallyAdded,
            removed_namespaces = actuallyRemoved,
            removed_unused_namespaces = removedUnusedSet.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
            diagnostics_after_edit = new
            {
                total = diagnostics.Count,
                returned = normalized.Length,
                errors = normalized.Count(d => string.Equals(d.severity, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = normalized.Count(d => string.Equals(d.severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                diagnostics = normalized,
            },
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static void RemoveUnusedUsings(
        string filePath,
        SyntaxTree syntaxTree,
        List<UsingDirectiveSyntax> usingList,
        HashSet<string> removedUnusedSet,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Diagnostic> diagnostics = CompilationDiagnostics.GetDiagnostics(new[] { syntaxTree }, cancellationToken);
        HashSet<int> unusedUsingLines = diagnostics
            .Where(d => string.Equals(d.Id, "CS8019", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Id, "IDE0005", StringComparison.OrdinalIgnoreCase))
            .Select(d =>
            {
                FileLinePositionSpan lineSpan = d.Location.GetLineSpan();
                return lineSpan.Path == filePath ? lineSpan.StartLinePosition.Line + 1 : -1;
            })
            .Where(line => line > 0)
            .ToHashSet();

        if (unusedUsingLines.Count == 0)
        {
            return;
        }

        List<UsingDirectiveSyntax> filtered = new();
        foreach (UsingDirectiveSyntax usingDirective in usingList)
        {
            FileLinePositionSpan lineSpan = usingDirective.GetLocation().GetLineSpan();
            int line = lineSpan.StartLinePosition.Line + 1;
            if (unusedUsingLines.Contains(line))
            {
                string namespaceName = GetUsingNamespace(usingDirective);
                if (!string.IsNullOrWhiteSpace(namespaceName))
                {
                    removedUnusedSet.Add(namespaceName);
                }

                continue;
            }

            filtered.Add(usingDirective);
        }

        usingList.Clear();
        usingList.AddRange(filtered);
    }

    private static string GetUsingNamespace(UsingDirectiveSyntax usingDirective)
        => usingDirective.Name?.ToString() ?? string.Empty;
}

