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
    /// Creates or updates a replica record in persistent storage
    /// </summary>
    /// <param name="record">The replica record to persist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default);

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
    /// Lists persisted replicas across all services for operator review, newest-first, with
    /// optional service/status filters and keyset pagination.
    /// </summary>
    /// <param name="serviceId">Optional feature service filter.</param>
    /// <param name="status">Optional lifecycle status filter (active, stale, expired, unregistered).</param>
    /// <param name="limit">Maximum number of replicas to return.</param>
    /// <param name="afterReplicaId">Exclusive keyset cursor; pass the last id from the prior page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Replica records ordered by creation time descending.</returns>
    Task<IReadOnlyList<ReplicaRecord>> ListAllAsync(
        string? serviceId,
        string? status,
        int limit,
        string? afterReplicaId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a replica record from persistent storage
    /// </summary>
    /// <param name="replicaId">Unique replica identifier to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the replica was found and removed, false otherwise</returns>
    Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default);
}
