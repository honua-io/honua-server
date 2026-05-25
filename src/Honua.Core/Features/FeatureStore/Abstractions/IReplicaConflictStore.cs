// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Durable storage for disconnected-sync conflict records. Conflicts are always read from
/// persistent storage (never from the replica cache) so they survive the sync response and
/// can be reviewed and resolved later (#1167).
/// </summary>
public interface IReplicaConflictStore
{
    /// <summary>
    /// Persists a newly detected conflict.
    /// </summary>
    /// <param name="conflict">The conflict to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(ReplicaConflict conflict, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists conflicts for a replica ordered newest-first, with keyset pagination.
    /// </summary>
    /// <param name="replicaId">Replica identifier.</param>
    /// <param name="pendingOnly">When true, returns only unresolved conflicts.</param>
    /// <param name="limit">Maximum number of conflicts to return.</param>
    /// <param name="afterConflictId">Exclusive keyset cursor; pass the last id from the prior page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ReplicaConflict>> ListByReplicaAsync(
        string replicaId,
        bool pendingOnly,
        int limit,
        Guid? afterConflictId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts pending (unresolved) conflicts grouped by replica for a set of replicas, in a single
    /// query. Replicas with no pending conflicts are omitted from the result.
    /// </summary>
    /// <param name="replicaIds">Replicas to count pending conflicts for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Map of replica id to pending conflict count.</returns>
    Task<IReadOnlyDictionary<string, int>> CountPendingByReplicaAsync(
        IReadOnlyCollection<string> replicaIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single conflict by its identifier.
    /// </summary>
    /// <param name="conflictId">Conflict identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The conflict if found, otherwise <see langword="null"/>.</returns>
    Task<ReplicaConflict?> GetAsync(Guid conflictId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a pending conflict for resolution and holds provider-level ownership until the
    /// returned claim is completed or disposed. Returns <see langword="null"/> when the conflict
    /// does not exist or has already reached a terminal resolution.
    /// </summary>
    /// <param name="conflictId">Conflict identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A resolution claim for the pending conflict, or <see langword="null"/>.</returns>
    Task<IReplicaConflictResolutionClaim?> TryClaimResolutionAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a resolution decision against a pending conflict. Idempotent: returns
    /// <see langword="false"/> when the conflict does not exist or was already resolved.
    /// </summary>
    /// <param name="conflictId">Conflict identifier.</param>
    /// <param name="resolution">The applied resolution.</param>
    /// <param name="resolvedBy">Principal recording the resolution.</param>
    /// <param name="resolutionPayloadJson">Optional merged feature payload for merge-field resolutions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a pending conflict was updated.</returns>
    Task<bool> ResolveAsync(
        Guid conflictId,
        ReplicaConflictResolution resolution,
        string resolvedBy,
        string? resolutionPayloadJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider-owned lease for completing a pending disconnected-replica conflict resolution.
/// Disposing without completing abandons the claim and leaves the conflict pending.
/// </summary>
public interface IReplicaConflictResolutionClaim : IAsyncDisposable
{
    /// <summary>
    /// Conflict row captured while the pending resolution claim is held.
    /// </summary>
    ReplicaConflict Conflict { get; }

    /// <summary>
    /// Completes the claimed conflict with a terminal resolution decision.
    /// </summary>
    /// <param name="resolution">The applied resolution.</param>
    /// <param name="resolvedBy">Principal recording the resolution.</param>
    /// <param name="resolutionPayloadJson">Optional merged feature payload for merge-field resolutions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the pending conflict was completed.</returns>
    Task<bool> CompleteAsync(
        ReplicaConflictResolution resolution,
        string resolvedBy,
        string? resolutionPayloadJson,
        CancellationToken cancellationToken = default);
}
