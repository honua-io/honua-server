// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Lifecycle status for a durable, protocol-neutral operation proposal. Reuses
/// the existing operation lifecycle vocabulary and adds <see cref="Rejected"/>
/// for the human approve/reject decision (#1692).
/// </summary>
public enum OperationProposalStatus
{
    /// <summary>
    /// Proposal has been planned but not yet routed for approval.
    /// </summary>
    Planned = 0,

    /// <summary>
    /// Proposal is waiting for a human approve/reject decision.
    /// </summary>
    AwaitingApproval = 1,

    /// <summary>
    /// Proposal was approved and submitted to the underlying execution pipeline.
    /// </summary>
    Submitted = 2,

    /// <summary>
    /// The underlying operation is reconciling toward the desired state.
    /// </summary>
    Reconciling = 3,

    /// <summary>
    /// The underlying operation completed successfully.
    /// </summary>
    Succeeded = 4,

    /// <summary>
    /// The underlying operation failed.
    /// </summary>
    Failed = 5,

    /// <summary>
    /// The proposal was rejected by a human approver.
    /// </summary>
    Rejected = 6,

    /// <summary>
    /// The applied operation was rolled back.
    /// </summary>
    RolledBack = 7,
}

/// <summary>
/// Risk level for a proposed operation, surfaced to the approval surface so a
/// reviewer can weigh the change. Mirrors the edit-pipeline risk vocabulary.
/// </summary>
public enum ProposalRiskLevel
{
    /// <summary>
    /// Low-risk change.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Medium-risk change.
    /// </summary>
    Medium = 1,

    /// <summary>
    /// High-risk change.
    /// </summary>
    High = 2,
}

/// <summary>
/// Structured plan attached to an <see cref="OperationProposal"/>. Produced from
/// the existing plan/diff/risk helpers (deploy plan, edit performance estimate,
/// replica conflict diff) so the approval surface renders the same artifacts the
/// direct-execute path would have produced.
/// </summary>
public sealed record OperationProposalPlan
{
    /// <summary>
    /// Human-readable, single-line summary of the proposed change.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Structured diff lines describing what would change. Producers may emit a
    /// field-level or revision-level diff depending on the operation class.
    /// </summary>
    public IReadOnlyList<string> Diff { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Dry-run output summary (estimated effects, side effects, artifacts).
    /// </summary>
    public IReadOnlyList<string> DryRun { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Assessed risk level for the proposed change.
    /// </summary>
    public ProposalRiskLevel RiskLevel { get; init; } = ProposalRiskLevel.Low;

    /// <summary>
    /// Reasons that currently block the proposal from being applied.
    /// </summary>
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Non-blocking advisory messages for the reviewer.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Opaque execution payload (JSON) the gateway hands back to the originating
    /// pipeline when the proposal is approved. Not rendered to reviewers verbatim.
    /// </summary>
    public string? ExecutionPayload { get; init; }
}

/// <summary>
/// Durable, protocol-neutral record of an agent- or operator-proposed mutating
/// operation that a human can later review and approve or reject. Generalizes the
/// bespoke Deploy <c>AwaitingApproval</c> flow across all in-scope operation
/// classes (#1692).
/// </summary>
public sealed record OperationProposal
{
    /// <summary>
    /// Stable proposal identifier.
    /// </summary>
    public required string ProposalId { get; init; }

    /// <summary>
    /// Operation class this proposal represents.
    /// </summary>
    public required OperationClass Kind { get; init; }

    /// <summary>
    /// Current proposal lifecycle status.
    /// </summary>
    public required OperationProposalStatus Status { get; init; }

    /// <summary>
    /// Operator or service principal that requested the operation.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Agent identifier when the proposal originated from an autonomous agent.
    /// </summary>
    public string? RequestedByAgent { get; init; }

    /// <summary>
    /// Optimistic-concurrency version token incremented on every store write.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Structured plan/diff/dry-run/risk produced for the approval surface.
    /// </summary>
    public OperationProposalPlan Plan { get; init; } = new();

    /// <summary>
    /// Guardrail decision that routed this operation to approval.
    /// </summary>
    public GuardrailDecision? GuardrailDecision { get; init; }

    /// <summary>
    /// Request metadata captured when the proposal was created.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// Stable identifier of the underlying execution operation created when the
    /// proposal is approved (for example a deploy workflow operation id).
    /// </summary>
    public string? ExecutionOperationId { get; init; }

    /// <summary>
    /// Principal that resolved (approved or rejected) the proposal.
    /// </summary>
    public string? ResolvedBy { get; init; }

    /// <summary>
    /// Reviewer-supplied reason for the resolution. Required on rejection.
    /// </summary>
    public string? ResolutionReason { get; init; }

    /// <summary>
    /// Time when the proposal was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Time when the proposal record was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Time when the proposal reached a terminal state.
    /// </summary>
    public DateTimeOffset? ResolvedAt { get; init; }
}
