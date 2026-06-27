// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Persistent Postgres-backed storage for replica state.
/// Provides durable replica records that survive cache evictions and process restarts.
/// </summary>
public interface IReplicaRepository
{
    /// <summary>
    /// Whether this provider durably persists replica state and can therefore serve the offline
    /// replica sync workflow (createReplica / synchronizeReplica / unRegisterReplica). True for the
    /// Postgres-backed store; false for read-only providers (DuckDB, MySQL/MariaDB) whose replica
    /// repository is a no-op. Protocol adapters consult this so they can return a clear, Esri-shaped
    /// "operation not supported" response on backends that cannot persist replicas instead of
    /// silently accepting a sync that is never durably applied (#2136).
    /// </summary>
    bool SupportsReplicaPersistence => true;

    /// <summary>
    /// Creates or updates a replica record in persistent storage
    /// </summary>
    /// <param name="record">The replica record to persist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically updates a replica's sync-state cursors (<see cref="ReplicaRecord.LastSyncTime"/>,
    /// <see cref="ReplicaRecord.LastSyncGeneration"/> and
    /// <see cref="ReplicaRecord.UploadBaseGeneration"/>) only when the persisted cursors still match
    /// the expected values the caller read before applying the sync. This is the compare-and-set
    /// guard against concurrent synchronizations of the same replica: the losing sync observes
    /// <c>false</c> instead of clobbering the winner's cursor with a stale read-modify-write.
    /// </summary>
    /// <param name="record">The replica record carrying the new sync-state cursors.</param>
    /// <param name="expectedLastSyncGeneration">
    /// The <see cref="ReplicaRecord.LastSyncGeneration"/> the caller read before the sync.
    /// </param>
    /// <param name="expectedUploadBaseGeneration">
    /// The <see cref="ReplicaRecord.UploadBaseGeneration"/> the caller read before the sync.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// True when the update applied; false when the replica does not exist or another sync advanced
    /// the cursors since the caller's read.
    /// </returns>
    Task<bool> TryUpdateSyncStateAsync(
        ReplicaRecord record,
        long expectedLastSyncGeneration,
        long expectedUploadBaseGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a replica record by its unique identifier
    /// </summary>
    /// <param name="replicaId">Unique replica identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Replica record if found, null otherwise</returns>
    Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists persisted replicas for a specific feature service.
    /// </summary>
    /// <param name="serviceId">Feature service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Replica records for the requested service.</returns>
    Task<IReadOnlyList<ReplicaRecord>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a replica record from persistent storage
    /// </summary>
    /// <param name="replicaId">Unique replica identifier to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the replica was found and removed, false otherwise</returns>
    Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default);
}
