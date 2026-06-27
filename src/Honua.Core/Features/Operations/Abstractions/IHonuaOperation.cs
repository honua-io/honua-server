// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// An executable Honua operation. Implementations adapt an operation invocation onto
/// an existing canonical service; they do not reimplement the underlying behaviour.
/// </summary>
public interface IHonuaOperation
{
    /// <summary>
    /// Stable operation identifier this implementation executes, for example
    /// <c>service.publish</c>.
    /// </summary>
    string OperationId { get; }

    /// <summary>
    /// Executes the operation for the supplied invocation context.
    /// </summary>
    /// <param name="context">The invocation context carrying the input payload,
    /// caller identity, tier, and role.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    Task<OperationResult> ExecuteAsync(
        OperationInvocationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider that contributes operation definitions to the operation catalog. Mirrors
/// <see cref="Honua.Core.Features.WorkflowPackages.Abstractions.IWorkflowNodeProvider"/>.
/// </summary>
public interface IOperationProvider
{
    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Returns the operation definitions contributed by the provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<HonuaOperationDefinition>> ListOperationsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only catalog of server-owned operations. Aggregates every
/// <see cref="IOperationProvider"/> into a versioned snapshot. Mirrors
/// <see cref="Honua.Core.Features.WorkflowPackages.Abstractions.IWorkflowNodeRegistry"/> one level up.
/// </summary>
public interface IOperationCatalog
{
    /// <summary>
    /// Builds the current operation catalog snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an operation definition by operation identifier.
    /// </summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<HonuaOperationDefinition?> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The guardrail seam. Every operation invocation flows through a decision point that
/// resolves a tier- and role-aware <see cref="PolicyDecision"/> (allow, require
/// approval, dry-run-first, or deny) before the operation executes.
/// </summary>
/// <remarks>
/// This spike provides only a pass-through implementation (Community tier). The
/// enforcement engine is a later phase; the contract and hook exist now so it is
/// never a retrofit.
/// </remarks>
public interface IOperationPolicyDecisionPoint
{
    /// <summary>
    /// Evaluates the policy for an operation invocation.
    /// </summary>
    /// <param name="operation">The operation definition, including its policy metadata.</param>
    /// <param name="context">The invocation context, including caller tier and role.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The policy decision.</returns>
    Task<PolicyDecision> EvaluateAsync(
        HonuaOperationDefinition operation,
        OperationInvocationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves an operation from the catalog, runs it through the policy decision point,
/// and executes the operation only when the decision is allow. This is the single
/// entry point used by both the deterministic UI surface and (later) the AI
/// orchestrator.
/// </summary>
public interface IOperationInvoker
{
    /// <summary>
    /// Invokes an operation by identifier.
    /// </summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The operation result, or a result describing the policy lane when the
    /// decision point did not allow execution.</returns>
    Task<OperationResult> InvokeAsync(
        OperationInvocationContext context,
        CancellationToken cancellationToken = default);
}
