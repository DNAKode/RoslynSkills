namespace RoslynSkills.Core.Commands;

internal static class WorkspaceContextPayload
{
    public static object Build(WorkspaceContextInfo context)
    {
        return new
        {
            mode = context.mode,
            resolution_source = context.resolution_source,
            requested_workspace_path = context.requested_workspace_path,
            resolved_workspace_path = context.resolved_workspace_path,
            project_path = context.project_path,
            fallback_reason = context.fallback_reason,
            attempted_workspace_paths = context.attempted_workspace_paths,
            workspace_diagnostics = context.workspace_diagnostics,
        };
    }
}