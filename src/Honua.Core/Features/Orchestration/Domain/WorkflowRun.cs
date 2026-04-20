// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Durable record for a single workflow execution instance.
/// </summary>
public sealed record WorkflowRun
{
    /// <summary>
    /// Stable run identifier used by the orchestration engine and APIs.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Back-reference to the declarative workflow this run executes.
    /// </summary>
    public required string WorkflowId { get; init; }

    /// <summary>
    /// Current run status.
    /// </summary>
    public required WorkflowRunStatus Status { get; init; }

    /// <summary>
    /// Relative operator priority propagated to child jobs.
    /// </summary>
    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    /// <summary>
    /// Time when the run was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Time when the run record was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Time when the run reached a terminal state.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Per-step runtime state for every step in the workflow definition.
    /// </summary>
    public required IReadOnlyList<WorkflowStepState> StepStates { get; init; }

    /// <summary>
    /// Error message that caused the run to fail, if any.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warnings collected during reconciliation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Identity and audit metadata captured when the run was created.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// Source that produced this run.
    /// </summary>
    public WorkflowTriggerKind TriggerKind { get; init; } = WorkflowTriggerKind.Manual;

    /// <summary>
    /// Opaque free-form metadata attached to the run, including scheduler fire timestamps.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
