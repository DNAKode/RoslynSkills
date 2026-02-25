using XmlSkills.Contracts;
using XmlSkills.Core.Commands;

namespace XmlSkills.Core;

public static class DefaultRegistryFactory
{
    public static ICommandRegistry Create()
    {
        IAgentCommand[] commands =
        {
            new PingCommand(),
            new BackendCapabilitiesCommand(),
            new ValidateDocumentCommand(),
            new FileOutlineCommand(),
            new FindElementsCommand(),
            new ReplaceElementTextCommand(),
            new ParseCompareCommand(),
        };

        return new CommandRegistry(commands);
    }
}
