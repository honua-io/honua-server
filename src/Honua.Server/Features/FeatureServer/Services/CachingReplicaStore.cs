// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Write-through caching wrapper that persists replica state to Postgres first,
/// then updates the distributed cache. Reads check the cache first and fall back
/// to Postgres on cache miss, backfilling the cache on hit.
/// </summary>
internal sealed class CachingReplicaStore : IReplicaStore
{
    private readonly DistributedReplicaStore _cache;
    private readonly IReplicaRepository _repository;

    public CachingReplicaStore(
        DistributedReplicaStore cache,
        IReplicaRepository repository)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        // Write-through: Postgres first, then cache
        var record = ToRecord(replica);
        await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await _cache.SetAsync(replica, ttl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReplicaState?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        // Read-through: cache first
        var cached = await _cache.GetAsync(replicaId, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        // Cache miss: fall back to Postgres
        var record = await _repository.GetAsync(replicaId, cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return null;
        }

        // Backfill cache
        var state = ToState(record.Value);
        await _cache.SetAsync(state, cancellationToken: cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        // Remove from both stores
        var removedFromDb = await _repository.RemoveAsync(replicaId, cancellationToken).ConfigureAwait(false);
        var removedFromCache = await _cache.RemoveAsync(replicaId, cancellationToken).ConfigureAwait(false);
        return removedFromDb || removedFromCache;
    }

    private static ReplicaRecord ToRecord(ReplicaState state) => new()
    {
        ReplicaId = state.ReplicaId,
        ReplicaName = state.ReplicaName,
        ServiceId = state.ServiceId,
        SyncModel = state.SyncModel,
        LayerIds = state.LayerIds,
        CreatedAt = state.CreatedAt,
        LastSyncTime = state.LastSyncTime,
        LastSyncGeneration = state.LastSyncGeneration
    };

    private static ReplicaState ToState(ReplicaRecord record) => new(
        record.ReplicaId,
        record.ReplicaName,
        record.ServiceId,
        record.SyncModel,
        record.LayerIds,
        record.CreatedAt)
    {
        LastSyncTime = record.LastSyncTime,
        LastSyncGeneration = record.LastSyncGeneration
    };
}
