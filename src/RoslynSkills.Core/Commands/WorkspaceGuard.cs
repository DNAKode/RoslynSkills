using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

internal static class WorkspaceGuard
{
    public static CommandExecutionResult? RequireWorkspaceIfRequested(
        string commandId,
        bool requireWorkspace,
        CommandFileAnalysis analysis)
    {
        if (!requireWorkspace)
        {
            return null;
        }

        if (string.Equals(analysis.WorkspaceContext.mode, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string fallbackReason = string.IsNullOrWhiteSpace(analysis.WorkspaceContext.fallback_reason)
            ? "Workspace context could not be resolved."
            : analysis.WorkspaceContext.fallback_reason;

        return new CommandExecutionResult(
            null,
            new[]
            {
                    new CommandError(
                        "workspace_required",
                    $"Command '{commandId}' requires workspace context for '{analysis.FilePath}', but mode was '{analysis.WorkspaceContext.mode}'. {fallbackReason} Pass workspace_path (.csproj/.vbproj/.sln/.slnx or containing directory) and retry."),
            });
    }
}
