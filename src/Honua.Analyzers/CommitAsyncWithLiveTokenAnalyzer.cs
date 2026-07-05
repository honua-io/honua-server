// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Honua.Analyzers;

/// <summary>
/// HN0001 — Detects calls to <c>DbTransaction.CommitAsync(CancellationToken)</c>
/// where the argument is not <c>CancellationToken.None</c>.
/// </summary>
/// <remarks>
/// Passing a live <see cref="System.Threading.CancellationToken"/> to
/// <c>DbTransaction.CommitAsync(CancellationToken)</c> creates a
/// phantom-commit race condition: if the token fires after the database server
/// accepts COMMIT but before the network ACK arrives, the driver throws
/// <see cref="System.OperationCanceledException"/> yet the transaction is
/// durably committed server-side. Callers that observe the exception as failure
/// and retry will insert duplicates.
///
/// Use <c>DbTransactionExtensions.CommitSafelyAsync(cancellationToken)</c>
/// instead: it pre-checks the token (failing cleanly if already cancelled) then
/// commits with <see cref="System.Threading.CancellationToken.None"/> so the
/// in-flight COMMIT round-trip is never interrupted.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CommitAsyncWithLiveTokenAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic identifier.</summary>
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

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    /// <inheritdoc />
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
