using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSkills.Core.Commands;

internal static class SymbolResolution
{
    public static ISymbol? GetSymbolForToken(
        SyntaxToken token,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        SyntaxNode? node = token.Parent;
        if (node is null)
        {
            return null;
        }

        ISymbol? declaredSymbol = node switch
        {
            ClassDeclarationSyntax classDeclaration when classDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken),
            StructDeclarationSyntax structDeclaration when structDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(structDeclaration, cancellationToken),
            InterfaceDeclarationSyntax interfaceDeclaration when interfaceDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(interfaceDeclaration, cancellationToken),
            EnumDeclarationSyntax enumDeclaration when enumDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(enumDeclaration, cancellationToken),
            RecordDeclarationSyntax recordDeclaration when recordDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(recordDeclaration, cancellationToken),
            MethodDeclarationSyntax methodDeclaration when methodDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken),
            ConstructorDeclarationSyntax constructorDeclaration when constructorDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(constructorDeclaration, cancellationToken),
            PropertyDeclarationSyntax propertyDeclaration when propertyDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(propertyDeclaration, cancellationToken),
            EventDeclarationSyntax eventDeclaration when eventDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(eventDeclaration, cancellationToken),
            DelegateDeclarationSyntax delegateDeclaration when delegateDeclaration.Identifier == token =>
                semanticModel.GetDeclaredSymbol(delegateDeclaration, cancellationToken),
            VariableDeclaratorSyntax variableDeclarator when variableDeclarator.Identifier == token =>
                semanticModel.GetDeclaredSymbol(variableDeclarator, cancellationToken),
            ParameterSyntax parameter when parameter.Identifier == token =>
                semanticModel.GetDeclaredSymbol(parameter, cancellationToken),
            LocalFunctionStatementSyntax localFunction when localFunction.Identifier == token =>
                semanticModel.GetDeclaredSymbol(localFunction, cancellationToken),
            NamespaceDeclarationSyntax namespaceDeclaration =>
                semanticModel.GetDeclaredSymbol(namespaceDeclaration, cancellationToken),
            FileScopedNamespaceDeclarationSyntax fileScopedNamespace =>
                semanticModel.GetDeclaredSymbol(fileScopedNamespace, cancellationToken),
            _ => null,
        };

        if (declaredSymbol is not null)
        {
            return declaredSymbol;
        }

        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
        if (symbolInfo.Symbol is not null)
        {
            return symbolInfo.Symbol;
        }

        return symbolInfo.CandidateSymbols.FirstOrDefault();
    }
}

