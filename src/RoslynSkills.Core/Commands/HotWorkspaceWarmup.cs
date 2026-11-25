using Microsoft.CodeAnalysis;

namespace RoslynSkills.Core.Commands;

/// <summary>
/// Moves the cold-compilation cost of the first semantic query out of that query: after a workspace is preloaded the host
/// builds every project's compilation and binds its declarations in the background, in dependency order. A query that
/// arrives meanwhile shares the same cached work instead of repeating it. <c>ROSLYNSKILLS_WARMUP=off</c> disables it.
/// </summary>
internal static class HotWorkspaceWarmup
{
    public static void Start(Solution? solution)
    {
        if (solution is null ||
            string.Equals(Environment.GetEnvironmentVariable("ROSLYNSKILLS_WARMUP")?.Trim(), "off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (ProjectId projectId in solution.GetProjectDependencyGraph().GetTopologicallySortedProjects())
                {
                    Project? project = solution.GetProject(projectId);
                    if (project is null)
                    {
                        continue;
                    }

                    Compilation? compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                    _ = compilation?.GetDeclarationDiagnostics();
                }
            }
            catch (Exception)
            {
                // Warm-up is best effort; a failing project surfaces on the first real query.
            }
        });
    }
}
