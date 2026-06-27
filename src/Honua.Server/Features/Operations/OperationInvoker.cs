// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Resolves an operation from the catalog, runs it through the policy decision point,
/// and executes the operation only when the decision is allow. On require-approval,
/// dry-run-first, or deny, it returns a result describing the lane without executing
/// the operation. This is the single invocation entry point for the one surface.
/// </summary>
internal sealed class OperationInvoker(
    IOperationCatalog catalog,
    IOperationPolicyDecisionPoint decisionPoint,
    IEnumerable<IHonuaOperation> operations,
    ILogger<OperationInvoker> logger) : IOperationInvoker
{
    private readonly IReadOnlyList<IHonuaOperation> _operations = operations.ToArray();

    public async Task<OperationResult> InvokeAsync(
        OperationInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var definition = await catalog.GetOperationAsync(context.OperationId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            OperationsLog.OperationNotFound(logger, context.OperationId);
            return OperationResult.Failed(
                context.OperationId,
                "operation.not_found",
                "The requested operation is not registered in the catalog.");
        }

        // The guardrail seam: every invocation flows through the decision point before
        // any side effect, even when the default decision point is a pass-through.
        var decision = await decisionPoint.EvaluateAsync(definition, context, cancellationToken).ConfigureAwait(false);
        OperationsLog.OperationPolicyDecision(logger, definition.OperationId, decision.Outcome);

        if (decision.Outcome != OperationPolicyOutcome.Allow)
        {
            return OperationResult.FromPolicyLane(definition.OperationId, decision.Outcome, decision.Reason);
        }

        var operation = _operations.FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, definition.OperationId, StringComparison.Ordinal));
        if (operation is null)
        {
            OperationsLog.OperationNotFound(logger, definition.OperationId);
            return OperationResult.Failed(
                definition.OperationId,
                "operation.not_executable",
                "The operation is catalogued but has no registered executor.");
        }

        var result = await operation.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            OperationsLog.OperationExecuted(logger, definition.OperationId);
        }

        return result;
    }
}
