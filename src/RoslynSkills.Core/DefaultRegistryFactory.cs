using RoslynSkills.Contracts;
using RoslynSkills.Core.Commands;

namespace RoslynSkills.Core;

public static class DefaultRegistryFactory
{
    public static ICommandRegistry Create()
    {
        IAgentCommand[] commands =
        {
            new PingCommand(),
            new FindSymbolCommand(),
            new FindReferencesCommand(),
            new FindImplementationsCommand(),
            new FindOverridesCommand(),
            new SymbolEnvelopeCommand(),
            new FileOutlineCommand(),
            new MemberSourceCommand(),
            new CallChainSliceCommand(),
            new DependencySliceCommand(),
            new GetFileDiagnosticsCommand(),
            new GetAfterEditDiagnosticsCommand(),
            new GetSolutionSnapshotCommand(),
            new GetWorkspaceSnapshotCommand(),
            new DiagnosticsDiffCommand(),
            new RenameSymbolCommand(),
            new ChangeSignatureCommand(),
            new AddMemberCommand(),
            new ReplaceMemberBodyCommand(),
            new UpdateUsingsCommand(),
            new ApplyCodeFixCommand(),
            new CreateFileCommand(),
            new EditTransactionCommand(),
            new ProposeFromDiagnosticsCommand(),
            new ApplyRepairPlanCommand(),
            new SessionOpenCommand(),
            new SessionSetContentCommand(),
            new SessionApplyTextEditsCommand(),
            new SessionApplyAndCommitCommand(),
            new SessionGetDiagnosticsCommand(),
            new SessionStatusCommand(),
            new SessionDiffCommand(),
            new SessionCommitCommand(),
            new SessionCloseCommand(),
        };

        return new CommandRegistry(commands);
    }
}

