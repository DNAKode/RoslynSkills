using Microsoft.CodeAnalysis;
using System.Reflection;

namespace RoslynAgent.Core.Commands;

internal static class CompilationReferenceBuilder
{
    public static IEnumerable<MetadataReference> BuildMetadataReferences()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        // Prefer the runtime's trusted platform assembly list when available; this
        // avoids missing-framework-type false positives in ad-hoc compilations.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies &&
            !string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            foreach (string path in trustedPlatformAssemblies.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (File.Exists(path))
                {
                    paths.Add(path);
                }
            }
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            string? location = assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            paths.Add(location);
        }

        return paths.Select(path => MetadataReference.CreateFromFile(path));
    }
}
