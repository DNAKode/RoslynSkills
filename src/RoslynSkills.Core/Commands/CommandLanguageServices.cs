using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace RoslynSkills.Core.Commands;

internal static class CommandLanguageServices
{
    private static readonly HashSet<string> CSharpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
    };

    private static readonly HashSet<string> VisualBasicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vb",
    };

    public static bool IsSupportedSourceFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return CSharpExtensions.Contains(extension) || VisualBasicExtensions.Contains(extension);
    }

    public static bool IsSupportedProjectFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase);
    }

    public static string DetectLanguageFromFilePath(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (VisualBasicExtensions.Contains(extension))
        {
            return LanguageNames.VisualBasic;
        }

        return LanguageNames.CSharp;
    }

    public static bool IsIdentifierToken(SyntaxToken token, string language)
    {
        if (string.Equals(language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return token.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.IdentifierToken);
        }

        return token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken);
    }

    public static string GetSyntaxKindName(SyntaxNode? node)
    {
        if (node is null)
        {
            return "Unknown";
        }

        if (string.Equals(node.Language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return ((Microsoft.CodeAnalysis.VisualBasic.SyntaxKind)node.RawKind).ToString();
        }

        return ((Microsoft.CodeAnalysis.CSharp.SyntaxKind)node.RawKind).ToString();
    }

    public static SyntaxTree ParseSyntaxTree(
        string source,
        string filePath,
        CancellationToken cancellationToken)
    {
        string language = DetectLanguageFromFilePath(filePath);
        return ParseSyntaxTree(source, filePath, language, cancellationToken);
    }

    public static SyntaxTree ParseSyntaxTree(
        string source,
        string filePath,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.Equals(language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return VisualBasicSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        }

        return CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
    }

    public static Compilation CreateCompilation(
        string assemblyName,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        string language)
    {
        IEnumerable<MetadataReference> references = CompilationReferenceBuilder.BuildMetadataReferences();
        if (string.Equals(language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return VisualBasicCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: syntaxTrees,
                references: references,
                options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
