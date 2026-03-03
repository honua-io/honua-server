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
    /// Removes a replica record from persistent storage
    /// </summary>
    /// <param name="replicaId">Unique replica identifier to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the replica was found and removed, false otherwise</returns>
    Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default);
}
