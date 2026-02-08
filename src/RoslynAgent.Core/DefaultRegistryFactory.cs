using RoslynAgent.Contracts;
using RoslynAgent.Core.Commands;

namespace RoslynAgent.Core;

public static class DefaultRegistryFactory
{
    public static ICommandRegistry Create()
    {
        IAgentCommand[] commands =
        {
            new PingCommand(),
            new FindSymbolCommand(),
            new GetFileDiagnosticsCommand(),
        };

        return new CommandRegistry(commands);
    }
}
