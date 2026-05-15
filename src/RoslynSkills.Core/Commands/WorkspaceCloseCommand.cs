using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

public sealed class WorkspaceCloseCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "workspace.close",
        Summary: "Close and discard a hot workspace handle.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: true);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        InputParsing.TryGetRequiredString(input, "workspace_handle", errors, out _);
        return errors;
    }

    public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "workspace_handle", errors, out string handle))
        {
            return Task.FromResult(new CommandExecutionResult(null, errors));
        }

        IWorkspaceHostStore workspaceStore = WorkspaceHostStoreProvider.Current;
        if (!workspaceStore.TryRemove(handle, out _))
        {
            return Task.FromResult(new CommandExecutionResult(
                null,
                new[] { new CommandError("workspace_not_found", $"Workspace handle '{handle}' was not found.") }));
        }

        object data = new
        {
            workspace_handle = handle,
            closed = true,
            store_workspace_count = workspaceStore.Count,
        };

        return Task.FromResult(new CommandExecutionResult(data, Array.Empty<CommandError>()));
    }
}
