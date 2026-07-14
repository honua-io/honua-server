// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

/// <summary>
/// Canonical store for isolated shadow-topology rebuild attempts (#2718) and their
/// multi-node fencing lease (#2720). This is the sole mutation path for rebuild
/// checkpoints, shadow-artifact bookkeeping, and the attempt/generation lifecycle
/// transitions a rebuild drives; every equivalent path (durable job executor, admin
/// submission endpoint, reconciler) calls this one store.
/// </summary>
public interface INetworkTopologyRebuildStore
{
    /// <summary>
    /// Creates a new rebuild attempt for a <c>dirty</c> generation, atomically transitioning
    /// the generation to <c>building</c> via compare-and-swap. Fails when the generation is
    /// missing, not <c>dirty</c>, its row version/source revision no longer match the
    /// caller's expectation, or a non-terminal attempt already exists for this generation.
    /// </summary>
    Task<NetworkTopologyRebuildAttempt> CreateAttemptAsync(
        string datasetId,
        long generation,
        long expectedRowVersion,
        long expectedSourceRevision,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a single rebuild attempt, or <c>null</c> when it does not exist.</summary>
    Task<NetworkTopologyRebuildAttempt?> GetAttemptAsync(
        string datasetId,
        long generation,
        long attempt,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the most recently created rebuild attempt for a generation, if any.</summary>
    Task<NetworkTopologyRebuildAttempt?> GetLatestAttemptAsync(
        string datasetId,
        long generation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every <c>building</c> attempt whose lease has expired as of <paramref name="asOf"/>
    /// (#2720 reconciler input).
    /// </summary>
    Task<IReadOnlyList<NetworkTopologyRebuildAttempt>> ListExpiredLeasesAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires the rebuild lease for a fresh (unowned) attempt, or takes it over when the
    /// current lease has expired, incrementing the monotonic fencing token either way.
    /// Returns <see langword="null"/> when the lease is currently held by another owner and
    /// has not expired.
    /// </summary>
    Task<NetworkTopologyRebuildAttempt?> TryAcquireOrTakeoverLeaseAsync(
        string datasetId,
        long generation,
        long attempt,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the lease for the current fenced owner. Returns <see langword="false"/> when
    /// <paramref name="fencingToken"/> is stale (the caller is no longer the fenced owner).
    /// </summary>
    Task<bool> TryHeartbeatAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (upserts) a stage checkpoint. Returns <see langword="false"/> without writing
    /// when <paramref name="fencingToken"/> is stale.
    /// </summary>
    Task<bool> TryWriteCheckpointAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        NetworkTopologyRebuildStage stage,
        NetworkTopologyRebuildCheckpointStatus status,
        string? detail,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every checkpoint recorded for an attempt, ordered by stage.</summary>
    Task<IReadOnlyList<NetworkTopologyRebuildCheckpoint>> ListCheckpointsAsync(
        string datasetId,
        long generation,
        long attempt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically completes the attempt (<c>building</c> -&gt; <c>ready</c>) and the owning
    /// generation (<c>building</c> -&gt; <c>ready</c>), recording the shadow table names and
    /// integrity-evidence digest. Returns <see langword="false"/> without mutating when
    /// <paramref name="fencingToken"/> is stale.
    /// </summary>
    Task<bool> TryCompleteAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        string shadowEdgeTable,
        string shadowVertexTable,
        string evidenceDigest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically fails the attempt and the owning generation with a sanitized, stable
    /// failure code. Returns <see langword="false"/> without mutating when
    /// <paramref name="fencingToken"/> is stale. Idempotent-safe: failing an
    /// already-failed/already-terminal attempt with the same owner/token is a no-op success.
    /// </summary>
    Task<bool> TryFailAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        string failureCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the shadow edge/vertex tables for every attempt of a generation other than
    /// <paramref name="keepAttempt"/> (superseded attempts and terminal failures past
    /// retention). Never touches the active generation's tables. Best-effort: table absence
    /// is not an error.
    /// </summary>
    Task CleanupOrphanShadowArtifactsAsync(
        string datasetId,
        long generation,
        long? keepAttempt,
        CancellationToken cancellationToken = default);
}
