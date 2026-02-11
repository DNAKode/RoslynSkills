using RoslynSkills.Contracts;
using System.Text.Json;

namespace RoslynSkills.Core.Commands;

internal static class WorkspaceInput
{
    public static string? GetOptionalWorkspacePath(JsonElement input)
    {
        if (!input.TryGetProperty("workspace_path", out JsonElement workspacePathProperty))
        {
            return null;
        }

        if (workspacePathProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? workspacePath = workspacePathProperty.GetString();
        return string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim();
    }

    public static void ValidateOptionalWorkspacePath(JsonElement input, List<CommandError> errors)
    {
        if (!input.TryGetProperty("workspace_path", out JsonElement workspacePathProperty))
        {
            return;
        }

        if (workspacePathProperty.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError(
                "invalid_input",
                "Property 'workspace_path' must be a string when provided."));
            return;
        }

        string? workspacePath = workspacePathProperty.GetString();
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            errors.Add(new CommandError(
                "invalid_input",
                "Property 'workspace_path' must not be empty when provided."));
            return;
        }

        string normalizedPath = Path.GetFullPath(workspacePath);
        if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
        {
            errors.Add(new CommandError(
                "workspace_not_found",
                $"Workspace path '{workspacePath}' does not exist."));
        }
    }
}
