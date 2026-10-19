// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>Durable unresolved commit-decision lineage; never authorizes executing or reading data.</summary>
/// <param name="IntentFingerprint">Full immutable accepted job intent.</param>
/// <param name="WorkerId">Worker that last entered or adopted receipt reconciliation.</param>
/// <param name="AttemptCount">Claimed attempt that last entered or adopted receipt reconciliation.</param>
/// <param name="FirstObservedAt">First durable uncertainty timestamp, retained across replacement claims.</param>
public sealed record ExecutionJobCommitRecovery(string IntentFingerprint, string WorkerId, int AttemptCount, DateTimeOffset FirstObservedAt)
{
    /// <summary>Whether the canonical execution path was entered, rather than only the receipt reader.</summary>
    public bool ExecutionStarted { get; init; }
    /// <summary>Deferred ordinary terminal disposition, applied only after authoritative absence.</summary>
    public ExecutionJobStatus? DeferredStatus { get; init; }
    /// <summary>Whether the original execution failure permits retry after proven absence.</summary>
    public bool RetryAllowed { get; init; } = true;
    /// <summary>Whether an administrative shutdown requires rescheduling after proven absence.</summary>
    public bool ForceRetry { get; init; }
    /// <summary>Bounded safe execution failure reason; never a database payload or policy claim.</summary>
    public string? FailureMessage { get; init; }
}
