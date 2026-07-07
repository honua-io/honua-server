// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Coarse classification of an observed durable workflow-operation transition. These are the
/// operator-meaningful lifecycle moments surfaced to observers (the operate timeline and, later,
/// the realtime deploy-operations hub) rather than every intermediate status write.
/// </summary>
public enum WorkflowOperationTransitionKind
{
    /// <summary>
    /// The operation record was durably created.
    /// </summary>
    Created,

    /// <summary>
    /// The operation was submitted to its provider backend.
    /// </summary>
    Submitted,

    /// <summary>
    /// The operation reached its desired outcome (promoted / succeeded cutover).
    /// </summary>
    Promoted,

    /// <summary>
    /// A rollback was requested or completed for the operation.
    /// </summary>
    RolledBack,

    /// <summary>
    /// The operation requires explicit operator action beyond automatic reconciliation.
    /// </summary>
    ManualInterventionRequired
}

/// <summary>
/// An observed durable workflow-operation transition. Raised by the store decorator after the
/// authoritative write so observers (for example an operate-timeline producer or the realtime hub)
/// can react without the writer knowing about them. Carries the full <see cref="WorkflowOperationRecord"/>
/// plus the classified <see cref="Kind"/> and the correlation identifiers callers most commonly key on.
/// </summary>
public sealed record WorkflowOperationTransition
{
    /// <summary>
    /// The operation record as persisted by the transition.
    /// </summary>
    public required WorkflowOperationRecord Operation { get; init; }

    /// <summary>
    /// Classified transition kind.
    /// </summary>
    public required WorkflowOperationTransitionKind Kind { get; init; }

    /// <summary>
    /// Wall-clock time the transition was observed.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Durable operation identifier.
    /// </summary>
    public string OperationId => Operation.OperationId;

    /// <summary>
    /// Deploy target identifier when the operation carries a deploy spec.
    /// </summary>
    public string? TargetId => Operation.Deploy?.TargetId;

    /// <summary>
    /// Release identifier associated with the operation: the desired revision for a deploy, or the
    /// metadata release package identifier for a metadata release.
    /// </summary>
    public string? ReleaseId =>
        Operation.Deploy?.DesiredRevision ?? Operation.MetadataRelease?.PackageId;

    /// <summary>
    /// Correlation identifier propagated from the originating request, when known.
    /// </summary>
    public string? CorrelationId => Operation.Audit.CorrelationId;
}
