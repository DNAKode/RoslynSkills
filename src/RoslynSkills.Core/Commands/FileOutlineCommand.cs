using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSkills.Contracts;
using System.Text.Json;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace RoslynSkills.Core.Commands;

public sealed class FileOutlineCommand : IAgentCommand
{
    public CommandDescriptor Descriptor { get; } = new(
        Id: "ctx.file_outline",
        Summary: "Return a structural outline of namespaces, types, and members in a C# or VB file.",
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

        string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        string language = CommandLanguageServices.DetectLanguageFromFilePath(filePath);
        SyntaxTree syntaxTree = CommandLanguageServices.ParseSyntaxTree(source, filePath, language, cancellationToken);
        SyntaxNode rootNode = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        SourceText sourceText = syntaxTree.GetText(cancellationToken);

        if (string.Equals(language, LanguageNames.VisualBasic, StringComparison.Ordinal))
        {
            return ExecuteVisualBasic(
                filePath,
                rootNode,
                sourceText,
                includeUsings,
                includeMembers,
                maxTypes,
                maxMembers,
                cancellationToken);
        }

        return ExecuteCSharp(
            filePath,
            rootNode,
            sourceText,
            includeUsings,
            includeMembers,
            maxTypes,
            maxMembers,
            cancellationToken);
    }

    private static CommandExecutionResult ExecuteCSharp(
        string filePath,
        SyntaxNode rootNode,
        SourceText sourceText,
        bool includeUsings,
        bool includeMembers,
        int maxTypes,
        int maxMembers,
        CancellationToken cancellationToken)
    {
        if (rootNode is not CSharpSyntax.CompilationUnitSyntax root)
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
        foreach (CSharpSyntax.BaseTypeDeclarationSyntax typeDeclaration in root.DescendantNodes(descendIntoTrivia: false)
                     .OfType<CSharpSyntax.BaseTypeDeclarationSyntax>()
                     .Take(maxTypes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinePosition position = sourceText.Lines.GetLinePosition(typeDeclaration.SpanStart);

            List<MemberOutline> memberOutlines = new();
            if (includeMembers && remainingMembers > 0)
            {
                if (typeDeclaration is CSharpSyntax.TypeDeclarationSyntax memberContainer)
                {
                    foreach (CSharpSyntax.MemberDeclarationSyntax member in memberContainer.Members)
                    {
                        if (remainingMembers <= 0)
                        {
                            break;
                        }

                        memberOutlines.Add(CreateCSharpMemberOutline(member, sourceText));
                        remainingMembers--;
                    }
                }
                else if (typeDeclaration is CSharpSyntax.EnumDeclarationSyntax enumDeclaration)
                {
                    foreach (CSharpSyntax.EnumMemberDeclarationSyntax enumMember in enumDeclaration.Members)
                    {
                        if (remainingMembers <= 0)
                        {
                            break;
                        }

                        memberOutlines.Add(CreateCSharpEnumMemberOutline(enumMember, sourceText));
                        remainingMembers--;
                    }
                }
            }

            typeOutlines.Add(new TypeOutline(
                namespace_name: typeDeclaration
                    .Ancestors()
                    .OfType<CSharpSyntax.BaseNamespaceDeclarationSyntax>()
                    .FirstOrDefault()?
                    .Name
                    .ToString(),
                type_kind: typeDeclaration.Kind().ToString(),
                type_name: GetCSharpTypeName(typeDeclaration),
                line: position.Line + 1,
                column: position.Character + 1,
                modifiers: typeDeclaration.Modifiers.Select(m => m.ValueText).ToArray(),
                base_types: GetCSharpBaseTypes(typeDeclaration),
                members: memberOutlines));
        }

        int globalStatementCount = root.Members.OfType<CSharpSyntax.GlobalStatementSyntax>().Count();
        object data = new
        {
            file_path = Path.GetFullPath(filePath),
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

    private static CommandExecutionResult ExecuteVisualBasic(
        string filePath,
        SyntaxNode rootNode,
        SourceText sourceText,
        bool includeUsings,
        bool includeMembers,
        int maxTypes,
        int maxMembers,
        CancellationToken cancellationToken)
    {
        if (rootNode is not VbSyntax.CompilationUnitSyntax root)
        {
            return new CommandExecutionResult(
                null,
                new[] { new CommandError("invalid_syntax_tree", "Could not parse compilation unit for the specified file.") });
        }

        string[] imports = includeUsings
            ? root.Imports
                .SelectMany(i => i.ImportsClauses)
                .Select(c => c.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

        List<TypeOutline> typeOutlines = new();
        int remainingMembers = maxMembers;
        foreach (SyntaxNode typeNode in root.DescendantNodes(descendIntoTrivia: false)
                     .Where(IsVbTypeNode)
                     .Take(maxTypes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinePosition position = sourceText.Lines.GetLinePosition(typeNode.SpanStart);

            List<MemberOutline> memberOutlines = new();
            if (includeMembers && remainingMembers > 0)
            {
                foreach (SyntaxNode memberNode in GetVbMemberNodes(typeNode))
                {
                    if (remainingMembers <= 0)
                    {
                        break;
                    }

                    memberOutlines.Add(CreateVbMemberOutline(memberNode, sourceText));
                    remainingMembers--;
                }
            }

            typeOutlines.Add(new TypeOutline(
                namespace_name: typeNode
                    .Ancestors()
                    .OfType<VbSyntax.NamespaceBlockSyntax>()
                    .FirstOrDefault()?
                    .NamespaceStatement
                    .Name
                    .ToString(),
                type_kind: CommandLanguageServices.GetSyntaxKindName(typeNode),
                type_name: GetVbTypeName(typeNode),
                line: position.Line + 1,
                column: position.Character + 1,
                modifiers: GetVbTypeModifiers(typeNode),
                base_types: GetVbBaseTypes(typeNode),
                members: memberOutlines));
        }

        object data = new
        {
            file_path = Path.GetFullPath(filePath),
            summary = new
            {
                using_count = imports.Length,
                type_count = typeOutlines.Count,
                member_count = typeOutlines.Sum(t => t.members.Count),
                global_statement_count = 0,
                include_usings = includeUsings,
                include_members = includeMembers,
                max_types = maxTypes,
                max_members = maxMembers,
            },
            usings = imports,
            types = typeOutlines,
        };

        return new CommandExecutionResult(data, Array.Empty<CommandError>());
    }

    private static bool IsVbTypeNode(SyntaxNode node)
    {
        return node is VbSyntax.ClassBlockSyntax
            or VbSyntax.StructureBlockSyntax
            or VbSyntax.InterfaceBlockSyntax
            or VbSyntax.ModuleBlockSyntax
            or VbSyntax.EnumBlockSyntax;
    }

    private static IEnumerable<SyntaxNode> GetVbMemberNodes(SyntaxNode typeNode)
    {
        return typeNode switch
        {
            VbSyntax.ClassBlockSyntax classBlock => classBlock.Members.Cast<SyntaxNode>(),
            VbSyntax.StructureBlockSyntax structureBlock => structureBlock.Members.Cast<SyntaxNode>(),
            VbSyntax.InterfaceBlockSyntax interfaceBlock => interfaceBlock.Members.Cast<SyntaxNode>(),
            VbSyntax.ModuleBlockSyntax moduleBlock => moduleBlock.Members.Cast<SyntaxNode>(),
            VbSyntax.EnumBlockSyntax enumBlock => enumBlock.Members.Cast<SyntaxNode>(),
            _ => Array.Empty<SyntaxNode>(),
        };
    }

    private static string GetVbTypeName(SyntaxNode typeNode)
    {
        return typeNode switch
        {
            VbSyntax.ClassBlockSyntax classBlock => classBlock.ClassStatement.Identifier.ValueText,
            VbSyntax.StructureBlockSyntax structureBlock => structureBlock.StructureStatement.Identifier.ValueText,
            VbSyntax.InterfaceBlockSyntax interfaceBlock => interfaceBlock.InterfaceStatement.Identifier.ValueText,
            VbSyntax.ModuleBlockSyntax moduleBlock => moduleBlock.ModuleStatement.Identifier.ValueText,
            VbSyntax.EnumBlockSyntax enumBlock => enumBlock.EnumStatement.Identifier.ValueText,
            _ => CommandLanguageServices.GetSyntaxKindName(typeNode),
        };
    }

    private static string[] GetVbTypeModifiers(SyntaxNode typeNode)
    {
        SyntaxTokenList modifiers = typeNode switch
        {
            VbSyntax.ClassBlockSyntax classBlock => classBlock.ClassStatement.Modifiers,
            VbSyntax.StructureBlockSyntax structureBlock => structureBlock.StructureStatement.Modifiers,
            VbSyntax.InterfaceBlockSyntax interfaceBlock => interfaceBlock.InterfaceStatement.Modifiers,
            VbSyntax.ModuleBlockSyntax moduleBlock => moduleBlock.ModuleStatement.Modifiers,
            VbSyntax.EnumBlockSyntax enumBlock => enumBlock.EnumStatement.Modifiers,
            _ => default,
        };

        return modifiers
            .Select(m => m.ValueText)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
    }

    private static string[] GetVbBaseTypes(SyntaxNode typeNode)
    {
        IEnumerable<string> values = typeNode switch
        {
            VbSyntax.ClassBlockSyntax classBlock => classBlock.Inherits
                .SelectMany(i => i.Types)
                .Concat(classBlock.Implements.SelectMany(i => i.Types))
                .Select(t => t.ToString()),
            VbSyntax.StructureBlockSyntax structureBlock => structureBlock.Implements
                .SelectMany(i => i.Types)
                .Select(t => t.ToString()),
            VbSyntax.InterfaceBlockSyntax interfaceBlock => interfaceBlock.Inherits
                .SelectMany(i => i.Types)
                .Select(t => t.ToString()),
            _ => Enumerable.Empty<string>(),
        };

        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static MemberOutline CreateCSharpMemberOutline(CSharpSyntax.MemberDeclarationSyntax member, SourceText sourceText)
    {
        LinePosition position = sourceText.Lines.GetLinePosition(member.SpanStart);
        return new MemberOutline(
            member_kind: member.Kind().ToString(),
            member_name: GetCSharpMemberName(member),
            signature: GetCSharpMemberSignature(member),
            line: position.Line + 1,
            column: position.Character + 1,
            modifiers: GetCSharpModifiers(member));
    }

    private static MemberOutline CreateCSharpEnumMemberOutline(CSharpSyntax.EnumMemberDeclarationSyntax enumMember, SourceText sourceText)
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

    private static MemberOutline CreateVbMemberOutline(SyntaxNode memberNode, SourceText sourceText)
    {
        LinePosition position = sourceText.Lines.GetLinePosition(memberNode.SpanStart);

        return new MemberOutline(
            member_kind: CommandLanguageServices.GetSyntaxKindName(memberNode),
            member_name: GetVbMemberName(memberNode),
            signature: GetVbMemberSignature(memberNode),
            line: position.Line + 1,
            column: position.Character + 1,
            modifiers: GetVbMemberModifiers(memberNode));
    }

    private static string GetVbMemberName(SyntaxNode memberNode)
    {
        return memberNode switch
        {
            VbSyntax.MethodBlockBaseSyntax methodBlock => GetVbMethodBaseName(methodBlock.BlockStatement),
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier.ValueText,
            VbSyntax.SubNewStatementSyntax => "New",
            VbSyntax.OperatorStatementSyntax => "Operator",
            VbSyntax.PropertyBlockSyntax propertyBlock => propertyBlock.PropertyStatement.Identifier.ValueText,
            VbSyntax.PropertyStatementSyntax propertyStatement => propertyStatement.Identifier.ValueText,
            VbSyntax.FieldDeclarationSyntax field => string.Join(", ", field.Declarators.SelectMany(d => d.Names).Select(n => n.Identifier.ValueText)),
            VbSyntax.EventBlockSyntax eventBlock => eventBlock.EventStatement.Identifier.ValueText,
            VbSyntax.EventStatementSyntax eventStatement => eventStatement.Identifier.ValueText,
            VbSyntax.EnumMemberDeclarationSyntax enumMember => enumMember.Identifier.ValueText,
            VbSyntax.DelegateStatementSyntax delegateStatement => delegateStatement.Identifier.ValueText,
            _ => CommandLanguageServices.GetSyntaxKindName(memberNode),
        };
    }

    private static string GetVbMemberSignature(SyntaxNode memberNode)
    {
        return memberNode switch
        {
            VbSyntax.EnumMemberDeclarationSyntax enumMember => enumMember.Identifier.ValueText,
            VbSyntax.FieldDeclarationSyntax field => field.ToString(),
            VbSyntax.MethodBlockBaseSyntax methodBlock => GetSingleLineText(methodBlock.BlockStatement),
            _ => GetSingleLineText(memberNode),
        };
    }

    private static string GetSingleLineText(SyntaxNode node)
    {
        string text = node.ToString();
        int newLine = text.IndexOfAny(new[] { '\r', '\n' });
        return newLine >= 0 ? text[..newLine].Trim() : text.Trim();
    }

    private static string[] GetVbMemberModifiers(SyntaxNode memberNode)
    {
        SyntaxTokenList modifiers = memberNode switch
        {
            VbSyntax.MethodBlockBaseSyntax methodBlock => methodBlock.BlockStatement.Modifiers,
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Modifiers,
            VbSyntax.PropertyBlockSyntax propertyBlock => propertyBlock.PropertyStatement.Modifiers,
            VbSyntax.PropertyStatementSyntax propertyStatement => propertyStatement.Modifiers,
            VbSyntax.FieldDeclarationSyntax fieldDeclaration => fieldDeclaration.Modifiers,
            VbSyntax.EventBlockSyntax eventBlock => eventBlock.EventStatement.Modifiers,
            VbSyntax.EventStatementSyntax eventStatement => eventStatement.Modifiers,
            VbSyntax.DelegateStatementSyntax delegateStatement => delegateStatement.Modifiers,
            VbSyntax.EnumMemberDeclarationSyntax => default,
            _ => default,
        };

        return modifiers
            .Select(m => m.ValueText)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
    }

    private static string GetVbMethodBaseName(VbSyntax.MethodBaseSyntax methodBase)
    {
        return methodBase switch
        {
            VbSyntax.MethodStatementSyntax methodStatement => methodStatement.Identifier.ValueText,
            VbSyntax.SubNewStatementSyntax => "New",
            VbSyntax.OperatorStatementSyntax => "Operator",
            _ => methodBase.Kind().ToString(),
        };
    }

    private static string GetCSharpTypeName(CSharpSyntax.BaseTypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration switch
        {
            CSharpSyntax.EnumDeclarationSyntax enumDeclaration => enumDeclaration.Identifier.ValueText,
            CSharpSyntax.RecordDeclarationSyntax recordDeclaration => recordDeclaration.Identifier.ValueText,
            _ => typeDeclaration.Identifier.ValueText,
        };
    }

    private static string[] GetCSharpBaseTypes(CSharpSyntax.BaseTypeDeclarationSyntax typeDeclaration)
    {
        CSharpSyntax.BaseListSyntax? baseList = typeDeclaration.BaseList;
        if (baseList is null)
        {
            return Array.Empty<string>();
        }

        return baseList.Types
            .Select(t => t.Type.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
    }

    private static string GetCSharpMemberName(CSharpSyntax.MemberDeclarationSyntax member)
    {
        return member switch
        {
            CSharpSyntax.MethodDeclarationSyntax method => method.Identifier.ValueText,
            CSharpSyntax.ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            CSharpSyntax.PropertyDeclarationSyntax property => property.Identifier.ValueText,
            CSharpSyntax.FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            CSharpSyntax.EventDeclarationSyntax @event => @event.Identifier.ValueText,
            CSharpSyntax.EventFieldDeclarationSyntax eventField => string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            CSharpSyntax.IndexerDeclarationSyntax => "this[]",
            CSharpSyntax.OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
            CSharpSyntax.ConversionOperatorDeclarationSyntax conversion => conversion.Type.ToString(),
            CSharpSyntax.BaseTypeDeclarationSyntax nestedType => GetCSharpTypeName(nestedType),
            CSharpSyntax.DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
            _ => member.Kind().ToString(),
        };
    }

    private static string GetCSharpMemberSignature(CSharpSyntax.MemberDeclarationSyntax member)
    {
        return member switch
        {
            CSharpSyntax.MethodDeclarationSyntax method => $"{method.ReturnType} {method.Identifier.ValueText}({method.ParameterList.Parameters})",
            CSharpSyntax.ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.ValueText}({constructor.ParameterList.Parameters})",
            CSharpSyntax.PropertyDeclarationSyntax property => $"{property.Type} {property.Identifier.ValueText}",
            CSharpSyntax.FieldDeclarationSyntax field => $"{field.Declaration.Type} {string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText))}",
            CSharpSyntax.EventDeclarationSyntax @event => $"{@event.Type} {@event.Identifier.ValueText}",
            CSharpSyntax.EventFieldDeclarationSyntax eventField => $"{eventField.Declaration.Type} {string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.ValueText))}",
            CSharpSyntax.IndexerDeclarationSyntax indexer => $"{indexer.Type} this[{indexer.ParameterList.Parameters}]",
            CSharpSyntax.OperatorDeclarationSyntax op => $"{op.ReturnType} operator {op.OperatorToken.ValueText}({op.ParameterList.Parameters})",
            CSharpSyntax.ConversionOperatorDeclarationSyntax conversion => $"{conversion.ImplicitOrExplicitKeyword.ValueText} operator {conversion.Type}({conversion.ParameterList.Parameters})",
            CSharpSyntax.BaseTypeDeclarationSyntax nestedType => $"{nestedType.Kind()} {GetCSharpTypeName(nestedType)}",
            CSharpSyntax.DelegateDeclarationSyntax @delegate => $"delegate {@delegate.ReturnType} {@delegate.Identifier.ValueText}({@delegate.ParameterList.Parameters})",
            _ => member.Kind().ToString(),
        };
    }

    private static string[] GetCSharpModifiers(CSharpSyntax.MemberDeclarationSyntax member)
    {
        return member switch
        {
            CSharpSyntax.BaseTypeDeclarationSyntax type => type.Modifiers.Select(m => m.ValueText).ToArray(),
            CSharpSyntax.BaseMethodDeclarationSyntax method => method.Modifiers.Select(m => m.ValueText).ToArray(),
            CSharpSyntax.BasePropertyDeclarationSyntax property => property.Modifiers.Select(m => m.ValueText).ToArray(),
            CSharpSyntax.BaseFieldDeclarationSyntax field => field.Modifiers.Select(m => m.ValueText).ToArray(),
            CSharpSyntax.DelegateDeclarationSyntax @delegate => @delegate.Modifiers.Select(m => m.ValueText).ToArray(),
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
