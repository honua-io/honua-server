// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Honua.Analyzers;

internal static class ReviewFindingIdentity
{
    internal static string Fingerprint(SyntaxTree tree, int position, string message)
    {
        var source = tree.GetText();
        var line = source.Lines.GetLineFromPosition(position).ToString().Trim();
        var context = string.Join("/", tree.GetRoot().FindToken(position).Parent!.AncestorsAndSelf()
            .Select(Scope).Where(value => value is not null).Reverse());
        var value = message + "\n" + context + "\n" + line;
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
#else
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
            .Replace("-", string.Empty).ToLowerInvariant();
#endif
    }

    private static string Arity(TypeParameterListSyntax? parameters) =>
        parameters is null ? string.Empty : "`" + parameters.Parameters.Count;

    private static string? Scope(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax declaration => declaration.Name.ToString(),
        TypeDeclarationSyntax declaration => declaration.Identifier.ValueText + Arity(declaration.TypeParameterList),
        BaseTypeDeclarationSyntax declaration => declaration.Identifier.ValueText,
        MethodDeclarationSyntax declaration => declaration.ExplicitInterfaceSpecifier + declaration.Identifier.ValueText +
            Arity(declaration.TypeParameterList) + declaration.ParameterList,
        ConstructorDeclarationSyntax declaration =>
            (declaration.Modifiers.Any(token => token.ValueText == "static") ? "static " : string.Empty) +
            declaration.Identifier.ValueText + declaration.ParameterList,
        DestructorDeclarationSyntax declaration => "~" + declaration.Identifier.ValueText,
        OperatorDeclarationSyntax declaration => declaration.ExplicitInterfaceSpecifier + "operator" + declaration.OperatorToken + declaration.ParameterList,
        ConversionOperatorDeclarationSyntax declaration => declaration.ExplicitInterfaceSpecifier + "operator" + declaration.Type + declaration.ParameterList,
        LocalFunctionStatementSyntax declaration => declaration.Identifier.ValueText +
            Arity(declaration.TypeParameterList) + declaration.ParameterList,
        PropertyDeclarationSyntax declaration => declaration.ExplicitInterfaceSpecifier + declaration.Identifier.ValueText,
        EventDeclarationSyntax declaration => declaration.ExplicitInterfaceSpecifier + declaration.Identifier.ValueText,
        IndexerDeclarationSyntax declaration => declaration.ExplicitInterfaceSpecifier + "this" + declaration.ParameterList,
        AccessorDeclarationSyntax declaration => declaration.Keyword.ValueText,
        VariableDeclaratorSyntax declaration => declaration.Identifier.ValueText,
        AnonymousFunctionExpressionSyntax declaration => declaration.ToString(),
        _ => null
    };
}
