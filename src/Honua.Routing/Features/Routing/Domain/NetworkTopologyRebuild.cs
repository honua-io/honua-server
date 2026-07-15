// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Ordered stages of an isolated shadow-topology rebuild attempt (#2718). Persisted as
/// per-stage checkpoints so a restarted worker can resume or safely repeat an idempotent
/// stage instead of starting over.
/// </summary>
public enum NetworkTopologyRebuildStage
{
    /// <summary>Snapshot the generation's staged edits into isolated shadow tables.</summary>
    Snapshot,

    /// <summary>Build the pgRouting-shaped edge/vertex shadow topology.</summary>
    Build,

    /// <summary>Run graph analysis (connectivity, isolated nodes) over the shadow topology.</summary>
    Analyze,

    /// <summary>Validate graph integrity evidence before the attempt can become ready.</summary>
    Validate,

    /// <summary>Release attempt-scoped resources (superseded shadow tables from prior attempts).</summary>
    Cleanup,
}

/// <summary>
/// Status of a single rebuild-stage checkpoint.
/// </summary>
public enum NetworkTopologyRebuildCheckpointStatus
{
    /// <summary>The stage has not started.</summary>
    Pending,

    /// <summary>The stage is currently executing.</summary>
    InProgress,

    /// <summary>The stage completed successfully.</summary>
    Completed,

    /// <summary>The stage failed.</summary>
    Failed,
}

/// <summary>
/// Lifecycle state of one rebuild attempt (#2718/#2720). Distinct from, but kept
/// consistent with, the owning generation's <see cref="NetworkTopologyGenerationState"/>:
/// an attempt starting resets the generation to <c>building</c>, and the attempt's terminal
/// state drives the generation to <c>ready</c> or <c>failed</c>.
/// </summary>
public enum NetworkTopologyRebuildAttemptState
{
    /// <summary>The attempt is actively building (or eligible for lease takeover).</summary>
    Building,

    /// <summary>The attempt completed successfully; its shadow topology is ready for promotion.</summary>
    Ready,

    /// <summary>The attempt failed or was cancelled with a sanitized, stable failure code.</summary>
    Failed,
}

/// <summary>
/// Provider-neutral record of one isolated shadow-topology rebuild attempt for a
/// <c>dirty</c> topology generation (#2718). Carries the multi-node fencing lease
/// (#2720): every checkpoint, completion, failure, and cleanup mutation must present the
/// current <see cref="FencingToken"/>, which increments on every lease acquisition or
/// takeover so a stale owner's writes are rejected deterministically rather than silently
/// corrupting a newer attempt's state.
/// </summary>
/// <param name="DatasetId">Stable network-dataset identifier.</param>
/// <param name="Generation">Topology generation this attempt is rebuilding.</param>
/// <param name="Attempt">Monotonically increasing attempt number within the generation.</param>
/// <param name="State">Current attempt lifecycle state.</param>
/// <param name="OperationId">Durable execution-job operation id driving this attempt.</param>
/// <param name="ExpectedSourceRevision">Source revision the attempt was fenced against at submission.</param>
/// <param name="ExpectedRowVersion">Generation row version the attempt was fenced against at submission.</param>
/// <param name="ShadowEdgeTable">Schema-qualified shadow edge table, set once the build stage completes.</param>
/// <param name="ShadowVertexTable">Schema-qualified shadow vertex table, set once the build stage completes.</param>
/// <param name="EvidenceDigest">Deterministic integrity-evidence digest recorded on successful completion.</param>
/// <param name="FailureCode">Sanitized stable failure code; set only when <see cref="State"/> is <see cref="NetworkTopologyRebuildAttemptState.Failed"/>.</param>
/// <param name="OwnerId">Identity of the worker currently holding the rebuild lease, if any.</param>
/// <param name="FencingToken">Monotonic fencing token. Every mutation must present this exact value.</param>
/// <param name="LeaseExpiresAt">When the current lease expires and becomes eligible for takeover.</param>
/// <param name="LastHeartbeatAt">Last time the owning worker renewed the lease.</param>
/// <param name="CreatedAt">Attempt creation timestamp.</param>
/// <param name="UpdatedAt">Last mutation timestamp.</param>
/// <param name="CompletedAt">Terminal (ready/failed) timestamp, if reached.</param>
public sealed record NetworkTopologyRebuildAttempt(
    string DatasetId,
    long Generation,
    long Attempt,
    NetworkTopologyRebuildAttemptState State,
    string OperationId,
    long ExpectedSourceRevision,
    long ExpectedRowVersion,
    string? ShadowEdgeTable,
    string? ShadowVertexTable,
    string? EvidenceDigest,
    string? FailureCode,
    string? OwnerId,
    long FencingToken,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Provider-neutral per-stage checkpoint for one rebuild attempt (#2718).
/// </summary>
/// <param name="DatasetId">Stable network-dataset identifier.</param>
/// <param name="Generation">Topology generation being rebuilt.</param>
/// <param name="Attempt">Attempt number within the generation.</param>
/// <param name="Stage">Rebuild stage this checkpoint records.</param>
/// <param name="Status">Stage status.</param>
/// <param name="Detail">Sanitized, stable stage detail (e.g. edge/vertex counts); never raw geometry or SQL.</param>
/// <param name="UpdatedAt">Last checkpoint mutation timestamp.</param>
public sealed record NetworkTopologyRebuildCheckpoint(
    string DatasetId,
    long Generation,
    long Attempt,
    NetworkTopologyRebuildStage Stage,
    NetworkTopologyRebuildCheckpointStatus Status,
    string? Detail,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Stable, sanitized rejection reasons for rebuild-store mutations (#2718/#2720). Callers
/// map these to precise HTTP problem responses without leaking SQL, physical identifiers,
/// or exception text.
/// </summary>
public enum NetworkTopologyRebuildRejection
{
    /// <summary>The generation does not exist.</summary>
    GenerationNotFound,

    /// <summary>The generation is not <c>dirty</c>, so a rebuild cannot start.</summary>
    GenerationNotDirty,

    /// <summary>The generation's row version no longer matches the caller's expectation.</summary>
    StaleRowVersion,

    /// <summary>The generation's source revision no longer matches the caller's expectation.</summary>
    StaleSourceRevision,

    /// <summary>An active (non-terminal) rebuild attempt already exists for this generation.</summary>
    AttemptAlreadyActive,

    /// <summary>The rebuild attempt does not exist.</summary>
    AttemptNotFound,

    /// <summary>The caller's fencing token no longer matches the attempt's current token.</summary>
    StaleFencingToken,

    /// <summary>The lease is currently held by another owner and has not expired.</summary>
    LeaseHeldByAnotherOwner,
}

/// <summary>
/// Thrown when a rebuild-store mutation is rejected deterministically for a stable,
/// sanitized reason (#2718/#2720). Callers map <see cref="Reason"/> to HTTP 404/409.
/// </summary>
public sealed class NetworkTopologyRebuildConflictException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyRebuildConflictException"/> class.
    /// </summary>
    public NetworkTopologyRebuildConflictException(NetworkTopologyRebuildRejection reason, string message)
        : base(message)
        => Reason = reason;

    /// <summary>Gets the stable rejection reason.</summary>
    public NetworkTopologyRebuildRejection Reason { get; }
}
