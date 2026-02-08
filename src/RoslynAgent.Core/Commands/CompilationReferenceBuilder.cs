using Microsoft.CodeAnalysis;
using System.Reflection;

namespace RoslynAgent.Core.Commands;

internal static class CompilationReferenceBuilder
{
    public static IEnumerable<MetadataReference> BuildMetadataReferences()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
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
