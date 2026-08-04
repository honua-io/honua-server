// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Runtime state for a single step inside a <see cref="WorkflowRun"/>.
/// </summary>
public sealed record WorkflowStepState
{
    /// <summary>
    /// Step identifier from the owning <see cref="WorkflowDefinition"/>.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Canonical analysis plan identifier executed for this step.
    /// </summary>
    public required string PlanId { get; init; }

    /// <summary>
    /// Current step status.
    /// </summary>
    public required WorkflowStepStatus Status { get; init; }

    /// <summary>
    /// Canonical execution-job identifier returned by the geoprocessing job service.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// The expanded step plan as the submit-time layer gate authorized it at RUN CREATION,
    /// carrying that authorization's per-layer bindings.
    /// </summary>
    /// <remarks>
    /// Reconciliation prefers this over re-deriving the plan from the stored definition. For a
    /// ForEach step the concrete layer/dataset id exists only after expansion, so publication
    /// saw a placeholder and stamped no binding; submitting the definition's plan therefore
    /// arrived at the gate unpinned and was refused, breaking every dynamic iteration
    /// (honua-server#3043 review). <see langword="null"/> on runs created before this field
    /// existed, which fall back to the definition plan exactly as they did.
    /// </remarks>
    public AnalysisPlan? AuthorizedPlan { get; init; }

    /// <summary>
    /// Number of completed submission attempts for this step.
    /// </summary>
    public int AttemptCount { get; init; }

    /// <summary>
    /// Earliest time at which the next attempt may be submitted after a retry delay.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>
    /// Time when the most recent attempt was submitted.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Time when the step reached a terminal state.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Error message captured from the most recent failed attempt.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Analysis plan inputs after upstream binding resolution, captured for auditability.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ResolvedInputs { get; init; }

    /// <summary>
    /// Artifacts captured from the completed job's result package, used to feed downstream bindings.
    /// </summary>
    public IReadOnlyList<ArtifactRef>? OutputArtifacts { get; init; }
}
