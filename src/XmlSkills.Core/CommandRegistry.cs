using XmlSkills.Contracts;

namespace XmlSkills.Core;

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
            c => c,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CommandDescriptor> ListCommands()
        => _commands.Values
            .Select(c => c.Descriptor)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToArray();

    public bool TryGet(string commandId, out IAgentCommand? command)
        => _commands.TryGetValue(commandId, out command);
}
