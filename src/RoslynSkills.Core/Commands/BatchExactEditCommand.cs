using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Thin alias of <see cref="EditTransactionCommand"/> (<c>edit.transaction</c>).
/// Both commands execute the exact same atomic multi-file edit transaction engine on the hot workspace.
/// </summary>
public sealed class BatchExactEditCommand : IAgentCommand
{
    private readonly EditTransactionCommand _inner = new();

    public CommandDescriptor Descriptor { get; } = new(
        Id: "edit.batch_exact",
        Summary: "Thin alias of edit.transaction. Apply multiple exact text/span/content edits atomically on the hot workspace.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true,
        RunsOnHotWorkspace: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
        => _inner.Validate(input);

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
        => _inner.ExecuteAsync(input, cancellationToken);
}
