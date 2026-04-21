// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Terminal outcome of an admission evaluation.
/// </summary>
public enum ExecutionAdmissionOutcome
{
    /// <summary>
    /// The request is admitted and may enter the execution pipeline.
    /// </summary>
    Admitted,

    /// <summary>
    /// The request exceeded a per-caller rate limit and should be retried later.
    /// </summary>
    Throttled,

    /// <summary>
    /// The request is rejected because a system or partition capacity limit is reached.
    /// </summary>
    Denied
}

/// <summary>
/// Control dimension that caused a non-admitted outcome.
/// </summary>
public enum ExecutionAdmissionDimension
{
    /// <summary>
    /// Per-principal submission rate limit over a sliding window.
    /// </summary>
    Rate,

    /// <summary>
    /// Per-partition active concurrency limit.
    /// </summary>
    Concurrency,

    /// <summary>
    /// Aggregate cost-weight limit within a partition.
    /// </summary>
    Cost,

    /// <summary>
    /// System-wide backpressure limit (global active jobs).
    /// </summary>
    Backpressure
}

/// <summary>
/// Point-in-time utilization snapshot captured during admission evaluation.
/// Exposed on the decision so callers and telemetry can observe headroom and load.
/// </summary>
public sealed record ExecutionAdmissionSnapshot
{
    /// <summary>
    /// Active job count matching the request partition and kind.
    /// </summary>
    public int ActiveJobsInPartition { get; init; }

    /// <summary>
    /// Active job count across all partitions for all kinds.
    /// </summary>
    public int ActiveJobsGlobal { get; init; }

    /// <summary>
    /// Submissions observed in the current rate window for the principal and kind.
    /// </summary>
    public int SubmissionsInWindow { get; init; }

    /// <summary>
    /// Sum of observed admission cost weights currently active in the partition.
    /// </summary>
    public double ActiveCostWeightInPartition { get; init; }
}

/// <summary>
/// Result of evaluating an <see cref="ExecutionAdmissionRequest"/>.
/// Transport-neutral: carries policy metadata, retry hints, and a utilization snapshot
/// so adapters can translate to protocol-specific responses without re-deriving state.
/// </summary>
public sealed record ExecutionAdmissionDecision
{
    /// <summary>
    /// Admission outcome.
    /// </summary>
    public required ExecutionAdmissionOutcome Outcome { get; init; }

    /// <summary>
    /// Dimension that denied the request, or null when admitted.
    /// </summary>
    public ExecutionAdmissionDimension? DenyingDimension { get; init; }

    /// <summary>
    /// Machine-readable policy reference (for example, <c>concurrency:geoprocessing:per-partition</c>).
    /// Null when admitted.
    /// </summary>
    public string? PolicyRef { get; init; }

    /// <summary>
    /// Human-readable reason for a non-admitted outcome.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Suggested retry delay in seconds for throttled or denied outcomes.
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// Utilization snapshot at evaluation time.
    /// </summary>
    public required ExecutionAdmissionSnapshot Snapshot { get; init; }

    /// <summary>
    /// Creates an admitted decision with the captured snapshot.
    /// </summary>
    public static ExecutionAdmissionDecision Admitted(ExecutionAdmissionSnapshot snapshot) => new()
    {
        Outcome = ExecutionAdmissionOutcome.Admitted,
        Snapshot = snapshot
    };

    /// <summary>
    /// Creates a throttled decision for the given dimension.
    /// </summary>
    public static ExecutionAdmissionDecision Throttled(
        ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfterSeconds,
        ExecutionAdmissionSnapshot snapshot) => new()
        {
            Outcome = ExecutionAdmissionOutcome.Throttled,
            DenyingDimension = dimension,
            PolicyRef = policyRef,
            Reason = reason,
            RetryAfterSeconds = retryAfterSeconds,
            Snapshot = snapshot
        };

    /// <summary>
    /// Creates a denied decision for the given dimension.
    /// </summary>
    public static ExecutionAdmissionDecision Denied(
        ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfterSeconds,
        ExecutionAdmissionSnapshot snapshot) => new()
        {
            Outcome = ExecutionAdmissionOutcome.Denied,
            DenyingDimension = dimension,
            PolicyRef = policyRef,
            Reason = reason,
            RetryAfterSeconds = retryAfterSeconds,
            Snapshot = snapshot
        };
}
