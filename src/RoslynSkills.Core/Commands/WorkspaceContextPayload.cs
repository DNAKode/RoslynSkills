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
            workspace_load_duration_ms = context.workspace_load_duration_ms,
            msbuild_registration_duration_ms = context.msbuild_registration_duration_ms,
            workspace_cache_mode = context.workspace_cache_mode,
            workspace_cache_hit = context.workspace_cache_hit,
        };
    }
}
