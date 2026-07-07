// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Outcome category from routing a mutating operation through the guardrail
/// ladder at the shared gateway choke point (#1693).
/// </summary>
public enum OperationGatewayOutcome
{
    /// <summary>
    /// The operation executed directly through the existing pipeline.
    /// </summary>
    Executed = 0,

    /// <summary>
    /// The operation was persisted as a proposal awaiting human approval.
    /// </summary>
    ProposalCreated = 1,

    /// <summary>
    /// The operation was blocked by the guardrail ladder / entitlement gate.
    /// </summary>
    Blocked = 2,

    /// <summary>
    /// No executor is registered for the requested operation class; the operation
    /// was not performed. This differs from <see cref="Blocked"/> (which means the
    /// guardrail ladder denied the operation for the current edition) — NotSupported
    /// means the runtime has no handler for the kind regardless of edition.
    /// </summary>
    NotSupported = 3,
}

/// <summary>
/// Request to route a mutating operation through the gateway. The gateway resolves
/// the guardrail tier, then either executes directly, persists a proposal, or
/// blocks. Protocol adapters build this neutral request from their own inputs.
/// </summary>
public sealed record OperationGatewayRequest
{
    /// <summary>
    /// Operation class being routed.
    /// </summary>
    public required OperationClass Kind { get; init; }

    /// <summary>
    /// Optional ops-action discriminator (for example <c>alerts.redrive_dead_letters</c>)
    /// used to resolve a per-action guardrail tier from the ops-action catalog. When
    /// set on an <see cref="OperationClass.AdminConfigChange"/> request the gateway
    /// resolves the action's declared tier; an unknown action fails closed to
    /// <see cref="Honua.Core.Features.Guardrails.Domain.GuardrailTier.Blocked"/>.
    /// </summary>
    public string? ActionDiscriminator { get; init; }

    /// <summary>
    /// Operator or service principal that requested the operation.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Agent identifier when the request originated from an autonomous agent.
    /// </summary>
    public string? RequestedByAgent { get; init; }

    /// <summary>
    /// Free-form operator reason or change note.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Correlation identifier propagated from the calling request.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Stable idempotency key for the underlying operation.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Structured plan/diff/dry-run/risk for the operation. When omitted the
    /// gateway asks the registered executor to produce one.
    /// </summary>
    public OperationProposalPlan? Plan { get; init; }

    /// <summary>
    /// Opaque, class-specific execution payload (JSON) the executor needs to run
    /// the operation. Persisted with the proposal so approval can replay it.
    /// </summary>
    public string? ExecutionPayload { get; init; }
}

/// <summary>
/// Result of routing a mutating operation through the gateway.
/// </summary>
public sealed record OperationGatewayResult
{
    /// <summary>
    /// Routing outcome.
    /// </summary>
    public required OperationGatewayOutcome Outcome { get; init; }

    /// <summary>
    /// Guardrail decision that produced the outcome.
    /// </summary>
    public required GuardrailDecision Decision { get; init; }

    /// <summary>
    /// Proposal identifier when a proposal was created.
    /// </summary>
    public string? ProposalId { get; init; }

    /// <summary>
    /// Underlying execution operation identifier when the operation executed.
    /// </summary>
    public string? ExecutionOperationId { get; init; }

    /// <summary>
    /// Operator-facing message for blocked or executed outcomes.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Shared choke point that routes mutating operations by guardrail tier:
/// <see cref="GuardrailTier.Blocked"/> rejects, <see cref="GuardrailTier.DirectExecute"/>
/// executes via the existing pipeline, and <see cref="GuardrailTier.RequiresApproval"/>
/// persists a proposal, audits <c>operation.proposed</c>, notifies, and returns a
/// proposal identifier (#1693). Gating, audit, and notification live here, not in
/// per-caller code.
/// </summary>
public interface IOperationGateway
{
    /// <summary>
    /// Routes the supplied operation request through the guardrail ladder.
    /// </summary>
    /// <param name="request">Neutral operation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The routing result.</returns>
    Task<OperationGatewayResult> RouteAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a previously created proposal once a human approves it, handing the
    /// stored execution payload to the registered executor. The proposal must be
    /// in <see cref="OperationProposalStatus.AwaitingApproval"/>.
    /// </summary>
    /// <param name="proposalId">Proposal identifier.</param>
    /// <param name="approvedBy">Principal that approved the proposal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated proposal, or null when the proposal is not found.</returns>
    Task<OperationProposal?> ApplyApprovedProposalAsync(
        string proposalId,
        string approvedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a previously created proposal with a required reason.
    /// </summary>
    /// <param name="proposalId">Proposal identifier.</param>
    /// <param name="rejectedBy">Principal that rejected the proposal.</param>
    /// <param name="reason">Required rejection reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated proposal, or null when the proposal is not found.</returns>
    Task<OperationProposal?> RejectProposalAsync(
        string proposalId,
        string rejectedBy,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-operation-class executor that knows how to (a) produce a plan and (b)
/// execute an operation through the existing pipeline. The gateway resolves the
/// executor by <see cref="OperationClass"/>; Deploy is the first adapter (#1693).
/// </summary>
public interface IOperationExecutor
{
    /// <summary>
    /// Operation class this executor handles.
    /// </summary>
    OperationClass OperationClass { get; }

    /// <summary>
    /// Produces a plan for the supplied request when the caller did not provide
    /// one. Returns null to use the request's plan as-is.
    /// </summary>
    /// <param name="request">Operation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A plan, or null.</returns>
    Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the operation through the existing pipeline.
    /// </summary>
    /// <param name="request">Operation request.</param>
    /// <param name="executionPayload">Class-specific execution payload (JSON).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The underlying execution operation identifier, when one exists.</returns>
    Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default);
}
