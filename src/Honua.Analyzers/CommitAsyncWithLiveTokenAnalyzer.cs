// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Honua.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CommitAsyncWithLiveTokenAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HN0001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "CommitAsync called with a live CancellationToken",
        messageFormat: "'{0}' passes a live CancellationToken to CommitAsync. Use CommitSafelyAsync(cancellationToken) to prevent phantom commits.",
        category: "Honua.DataIntegrity",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Passing a live CancellationToken to DbTransaction.CommitAsync creates a phantom-commit race: " +
            "if the token fires after the server accepts COMMIT but before the ACK arrives, the driver throws " +
            "OperationCanceledException yet the transaction is durably committed. Use CommitSafelyAsync(ct) instead.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Must be a member-access call
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        // Must be named CommitAsync
        if (!string.Equals(memberAccess.Name.Identifier.Text, "CommitAsync", System.StringComparison.Ordinal))
        {
            return;
        }

        // Must have exactly one argument
        var args = invocation.ArgumentList.Arguments;
        if (args.Count != 1)
        {
            return;
        }

        // If the argument is CancellationToken.None, it is already the safe form
        if (IsCancellationTokenNone(args[0].Expression))
        {
            return;
        }

        // Resolve the method symbol and verify it is DbTransaction.CommitAsync
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol method)
        {
            return;
        }

        if (!IsDbTransactionCommitAsync(method))
        {
            return;
        }

        var containingType = context.ContainingSymbol?.ContainingType?.ToDisplayString() ?? "(unknown)";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), containingType));
    }

    private static bool IsCancellationTokenNone(ExpressionSyntax expression)
    {
        // Matches: CancellationToken.None
        return expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "None" } ma
               && string.Equals(ma.Expression.ToString(), "CancellationToken", System.StringComparison.Ordinal);
    }

    private static bool IsDbTransactionCommitAsync(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        while (containingType is not null)
        {
            if (string.Equals(
                    containingType.ToDisplayString(),
                    "System.Data.Common.DbTransaction",
                    System.StringComparison.Ordinal))
            {
                return true;
            }

            containingType = containingType.BaseType;
        }

        return false;
    }
}
