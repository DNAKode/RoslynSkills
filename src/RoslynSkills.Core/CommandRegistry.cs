using System.Text.Json;
using RoslynSkills.Contracts;

namespace RoslynSkills.Core;

public interface ICommandRegistry
{
    IReadOnlyList<CommandDescriptor> ListCommands();

    bool TryGet(string commandId, out IAgentCommand? command);
}

public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, IAgentCommand> _commands;

    public CommandRegistry(IEnumerable<IAgentCommand> commands)
    {
        _commands = commands.ToDictionary(
            c => c.Descriptor.Id,
            EnrichWithInputSchema,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CommandDescriptor> ListCommands()
        => _commands.Values
            .Select(c => c.Descriptor)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToArray();

    public bool TryGet(string commandId, out IAgentCommand? command)
        => _commands.TryGetValue(commandId, out command);

    private static IAgentCommand EnrichWithInputSchema(IAgentCommand command)
    {
        if (command.Descriptor.InputSchema is not null)
        {
            return command;
        }

        CommandInputSchema? schema = CommandInputSchemaCatalog.GetSchema(command.Descriptor.Id);
        return schema is null
            ? command
            : new SchemaEnrichedCommand(command, command.Descriptor with { InputSchema = schema });
    }

    private sealed class SchemaEnrichedCommand : IAgentCommand
    {
        private readonly IAgentCommand _inner;

        public SchemaEnrichedCommand(IAgentCommand inner, CommandDescriptor descriptor)
        {
            _inner = inner;
            Descriptor = descriptor;
        }

        public CommandDescriptor Descriptor { get; }

        public IReadOnlyList<CommandError> Validate(JsonElement input) => _inner.Validate(input);

        public Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => _inner.ExecuteAsync(input, cancellationToken);
    }
}
