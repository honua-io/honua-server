// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Write-through caching wrapper that persists replica state to Postgres first,
/// then updates the distributed cache best-effort. Reads check the cache first
/// and fall back to Postgres on cache miss, backfilling the cache on hit.
/// </summary>
internal sealed partial class CachingReplicaStore : IReplicaStore
{
    private readonly DistributedReplicaStore _cache;
    private readonly IReplicaRepository _repository;
    private readonly ILogger<CachingReplicaStore> _logger;

    public CachingReplicaStore(
        DistributedReplicaStore cache,
        IReplicaRepository repository,
        ILogger<CachingReplicaStore>? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? NullLogger<CachingReplicaStore>.Instance;
    }

    public async Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        // Write-through: Postgres first, then cache
        var record = ToRecord(replica);
        await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

        try
        {
            await _cache.SetAsync(replica, ttl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.CacheWriteFailed(_logger, replica.ReplicaId, ex);
        }
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

        if (!removedFromDb)
        {
            return false;
        }

        try
        {
            await _cache.RemoveAsync(replicaId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.CacheRemoveFailed(_logger, replicaId, ex);
        }

        return true;
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 7720, Level = LogLevel.Warning, Message = "Failed to update distributed replica cache for {ReplicaId}")]
        public static partial void CacheWriteFailed(ILogger logger, string replicaId, Exception exception);

        [LoggerMessage(EventId = 7721, Level = LogLevel.Warning, Message = "Failed to remove distributed replica cache for {ReplicaId}")]
        public static partial void CacheRemoveFailed(ILogger logger, string replicaId, Exception exception);
    }
}
