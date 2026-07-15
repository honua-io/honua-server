// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

/// <summary>
/// Canonical edit service for batched, transactional edge and turn-restriction content
/// mutations against one non-active topology generation (#2716). Every equivalent admin
/// mutation path must call this one store so validation, concurrency, idempotency, dirty
/// transition, and audit/telemetry behavior stay consistent; the NAServer/GeoServices
/// protocol adapter never calls this interface and remains read-only.
/// </summary>
public interface INetworkTopologyEditStore
{
    /// <summary>
    /// Applies a batch of edge and turn-restriction mutations to one generation inside a
    /// single all-or-nothing Postgres transaction. On success the generation's source
    /// revision and row version are incremented and its state moves to (or stays) <c>dirty</c>;
    /// the active generation pointer is never changed.
    /// </summary>
    /// <param name="datasetId">Stable network-dataset identifier.</param>
    /// <param name="generation">Target generation number. Must not be the active generation.</param>
    /// <param name="expectedRowVersion">
    /// Row version the caller last observed (from <c>If-Match</c>). A mismatch is a stale
    /// concurrency conflict.
    /// </param>
    /// <param name="idempotencyKey">Client-supplied at-most-once key, scoped to dataset + generation.</param>
    /// <param name="contentHash">
    /// Stable hash of the exact request payload. A replayed idempotency key must carry the
    /// same hash; a mismatch is rejected deterministically rather than silently replayed or
    /// silently re-applied.
    /// </param>
    /// <param name="batch">The validated edit batch (callers must run <see cref="NetworkTopologyEditValidation"/> first).</param>
    /// <param name="actor">Authenticated admin identity performing the edit, for structured audit logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting generation state and per-list mutation counts.</returns>
    /// <exception cref="NetworkTopologyGenerationNotFoundException">The dataset/generation does not exist.</exception>
    /// <exception cref="NetworkTopologyEditConflictException">
    /// The row version is stale, the generation is not editable, or the idempotency key was
    /// reused with a different payload.
    /// </exception>
    /// <exception cref="NetworkTopologyEditValidationException">
    /// A referenced edge does not exist, an id already exists (create) or does not exist
    /// (update/delete), or an edge is still referenced by a turn restriction outside this batch.
    /// </exception>
    Task<NetworkTopologyEditResult> ApplyEditBatchAsync(
        string datasetId,
        long generation,
        long expectedRowVersion,
        string idempotencyKey,
        string contentHash,
        NetworkTopologyEditBatch batch,
        string actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a batched edit targets a dataset/generation pair that does not exist.
/// </summary>
public sealed class NetworkTopologyGenerationNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyGenerationNotFoundException"/> class.
    /// </summary>
    public NetworkTopologyGenerationNotFoundException(string datasetId, long generation)
        : base($"Topology generation {generation} for dataset '{datasetId}' was not found.")
    {
        DatasetId = datasetId;
        Generation = generation;
    }

    /// <summary>Gets the dataset id that was targeted.</summary>
    public string DatasetId { get; }

    /// <summary>Gets the generation number that was targeted.</summary>
    public long Generation { get; }
}

/// <summary>
/// Stable reason a batched topology edit was rejected deterministically without mutating
/// any content (#2716 safety invariants).
/// </summary>
public enum NetworkTopologyEditConflictReason
{
    /// <summary>The caller's <c>If-Match</c> row version no longer matches persisted state.</summary>
    StaleRowVersion,

    /// <summary>The generation is active, ready, building, failed, or retired and cannot accept content edits.</summary>
    GenerationNotEditable,

    /// <summary>The idempotency key was already used for this generation with a different payload.</summary>
    IdempotencyKeyReused,
}

/// <summary>
/// Thrown when a batched edit is rejected deterministically for concurrency or lifecycle
/// reasons. Callers map <see cref="Reason"/> to HTTP 409 Conflict.
/// </summary>
public sealed class NetworkTopologyEditConflictException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyEditConflictException"/> class.
    /// </summary>
    public NetworkTopologyEditConflictException(NetworkTopologyEditConflictReason reason, string message)
        : base(message)
        => Reason = reason;

    /// <summary>Gets the stable conflict reason.</summary>
    public NetworkTopologyEditConflictReason Reason { get; }
}

/// <summary>
/// Thrown when a batched edit fails a validation rule that can only be checked
/// transactionally against persisted content (an id collision on create, a missing id on
/// update/delete, or a turn restriction referencing an edge that does not exist in this
/// generation). Callers map this to HTTP 400 Bad Request. The message is sanitized and
/// never includes raw geometry, attribute values, or SQL.
/// </summary>
public sealed class NetworkTopologyEditValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyEditValidationException"/> class.
    /// </summary>
    public NetworkTopologyEditValidationException(string message)
        : base(message)
    {
    }
}
