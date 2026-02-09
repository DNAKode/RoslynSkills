using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAgent.Contracts;
using System.Text.Json;

namespace RoslynAgent.Core.Commands;

public sealed class FileOutlineCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.file_outline",
        Summary: "Return a structural outline of namespaces, types, and members in a C# file.",
        InputSchemaVersion: "1.0",
        OutputSchemaVersion: "1.0",
        MutatesState: false);

    public IReadOnlyList<CommandError> Validate(JsonElement input)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return errors;
        }

        if (!File.Exists(filePath))
        {
            errors.Add(new CommandError("file_not_found", $"Input file '{filePath}' does not exist."));
        }

        return errors;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        List<CommandError> errors = new();
        if (!InputParsing.TryGetRequiredString(input, "file_path", errors, out string filePath))
        {
            return new CommandExecutionResult(null, errors);
        }

        if (!File.Exists(filePath))
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("file_not_found", $"Input file '{filePath}' does not exist.") });
        }

        bool includeUsings = InputParsing.GetOptionalBool(input, "include_usings", defaultValue: true);
        bool includeMembers = InputParsing.GetOptionalBool(input, "include_members", defaultValue: true);
        int maxTypes = InputParsing.GetOptionalInt(input, "max_types", defaultValue: 500, minValue: 1, maxValue: 10_000);
        int maxMembers = InputParsing.GetOptionalInt(input, "max_members", defaultValue: 2_000, minValue: 1, maxValue: 50_000);

        CommandFileAnalysis analysis = await CommandFileAnalysis.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        CompilationUnitSyntax? root = analysis.Root as CompilationUnitSyntax;
        if (root is null)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_syntax_tree", "Could not parse compilation unit for the specified file.") });
        }

        string[] usings = includeUsings
            ? root.Usings
                .Select(u => u.Name?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

        List<TypeOutline> typeOutlines = new();
        int remainingMembers = maxMembers;
        foreach (BaseTypeDeclarationSyntax typeDeclaration in root.DescendantNodes(descendIntoTrivia: false)
                     .OfType<BaseTypeDeclarationSyntax>()
                     .Take(maxTypes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinePosition position = analysis.SourceText.Lines.GetLinePosition(typeDeclaration.SpanStart);

            List<MemberOutline> memberOutlines = new();
            if (includeMembers && remainingMembers > 0)
            {
                if (typeDeclaration is TypeDeclarationSyntax memberContainer)
                {
                    foreach (MemberDeclarationSyntax member in memberContainer.Members)
                    {
                        if (remainingMembers <= 0)
                        {
                            break;
                        }

                        memberOutlines.Add(CreateMemberOutline(member, analysis.SourceText));
                        remainingMembers--;
                    }
                }
                else if (typeDeclaration is EnumDeclarationSyntax enumDeclaration)
                {
                    foreach (EnumMemberDeclarationSyntax enumMember in enumDeclaration.Members)
                    {
                        if (remainingMembers <= 0)
                        {
                            break;
                        }

                        memberOutlines.Add(CreateEnumMemberOutline(enumMember, analysis.SourceText));
                        remainingMembers--;
                    }
                }
            }

            typeOutlines.Add(new TypeOutline(
                namespace_name: typeDeclaration
                    .Ancestors()
                    .OfType<BaseNamespaceDeclarationSyntax>()
                    .FirstOrDefault()?
                    .Name
                    .ToString(),
                type_kind: typeDeclaration.Kind().ToString(),
                type_name: GetTypeName(typeDeclaration),
                line: position.Line + 1,
                column: position.Character + 1,
                modifiers: typeDeclaration.Modifiers.Select(m => m.ValueText).ToArray(),
                base_types: GetBaseTypes(typeDeclaration),
                members: memberOutlines));
        }

        int globalStatementCount = root.Members.OfType<GlobalStatementSyntax>().Count();
        object data = new
        {
            file_path = filePath,
            summary = new
            {
                using_count = usings.Length,
                type_count = typeOutlines.Count,
                member_count = typeOutlines.Sum(t => t.members.Count),
                global_statement_count = globalStatementCount,
                include_usings = includeUsings,
                include_members = includeMembers,
                max_types = maxTypes,
                max_members = maxMembers,
            },
            usings,
            types = typeOutlines,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static string GetTypeName(BaseTypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration switch
        {
            EnumDeclarationSyntax enumDeclaration => enumDeclaration.Identifier.ValueText,
            RecordDeclarationSyntax recordDeclaration => recordDeclaration.Identifier.ValueText,
            _ => typeDeclaration.Identifier.ValueText,
        };
    }

    private static string[] GetBaseTypes(BaseTypeDeclarationSyntax typeDeclaration)
    {
        BaseListSyntax? baseList = typeDeclaration.BaseList;
        if (baseList is null)
        {
            return Array.Empty<string>();
        }

        return baseList.Types
            .Select(t => t.Type.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
    }

    private static MemberOutline CreateMemberOutline(MemberDeclarationSyntax member, SourceText sourceText)
    {
        LinePosition position = sourceText.Lines.GetLinePosition(member.SpanStart);
        return new MemberOutline(
            member_kind: member.Kind().ToString(),
            member_name: GetMemberName(member),
            signature: GetMemberSignature(member),
            line: position.Line + 1,
            column: position.Character + 1,
            modifiers: GetModifiers(member));
    }

    private static MemberOutline CreateEnumMemberOutline(EnumMemberDeclarationSyntax enumMember, SourceText sourceText)
    {
        LinePosition position = sourceText.Lines.GetLinePosition(enumMember.SpanStart);
        return new MemberOutline(
            member_kind: enumMember.Kind().ToString(),
            member_name: enumMember.Identifier.ValueText,
            signature: enumMember.Identifier.ValueText,
            line: position.Line + 1,
            column: position.Character + 1,
            modifiers: Array.Empty<string>());
    }

    private static string GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            EventDeclarationSyntax @event => @event.Identifier.ValueText,
            EventFieldDeclarationSyntax eventField => string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            IndexerDeclarationSyntax => "this[]",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion => conversion.Type.ToString(),
            BaseTypeDeclarationSyntax nestedType => GetTypeName(nestedType),
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
            _ => member.Kind().ToString(),
        };
    }

    private static string GetMemberSignature(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => $"{method.ReturnType} {method.Identifier.ValueText}({method.ParameterList.Parameters})",
            ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.ValueText}({constructor.ParameterList.Parameters})",
            PropertyDeclarationSyntax property => $"{property.Type} {property.Identifier.ValueText}",
            FieldDeclarationSyntax field => $"{field.Declaration.Type} {string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText))}",
            EventDeclarationSyntax @event => $"{@event.Type} {@event.Identifier.ValueText}",
            EventFieldDeclarationSyntax eventField => $"{eventField.Declaration.Type} {string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.ValueText))}",
            IndexerDeclarationSyntax indexer => $"{indexer.Type} this[{indexer.ParameterList.Parameters}]",
            OperatorDeclarationSyntax op => $"{op.ReturnType} operator {op.OperatorToken.ValueText}({op.ParameterList.Parameters})",
            ConversionOperatorDeclarationSyntax conversion => $"{(conversion.ImplicitOrExplicitKeyword.ValueText)} operator {conversion.Type}({conversion.ParameterList.Parameters})",
            BaseTypeDeclarationSyntax nestedType => $"{nestedType.Kind()} {GetTypeName(nestedType)}",
            DelegateDeclarationSyntax @delegate => $"delegate {@delegate.ReturnType} {@delegate.Identifier.ValueText}({@delegate.ParameterList.Parameters})",
            _ => member.Kind().ToString(),
        };
    }

    private static string[] GetModifiers(MemberDeclarationSyntax member)
    {
        return member switch
        {
            BaseTypeDeclarationSyntax type => type.Modifiers.Select(m => m.ValueText).ToArray(),
            BaseMethodDeclarationSyntax method => method.Modifiers.Select(m => m.ValueText).ToArray(),
            BasePropertyDeclarationSyntax property => property.Modifiers.Select(m => m.ValueText).ToArray(),
            BaseFieldDeclarationSyntax field => field.Modifiers.Select(m => m.ValueText).ToArray(),
            DelegateDeclarationSyntax @delegate => @delegate.Modifiers.Select(m => m.ValueText).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    private sealed record TypeOutline(
        string? namespace_name,
        string type_kind,
        string type_name,
        int line,
        int column,
        IReadOnlyList<string> modifiers,
        IReadOnlyList<string> base_types,
        IReadOnlyList<MemberOutline> members);

    private sealed record MemberOutline(
        string member_kind,
        string member_name,
        string signature,
        int line,
        int column,
        IReadOnlyList<string> modifiers);
}
