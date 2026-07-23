// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal interface IReplicaStore
{
    Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conditionally persists a replica's sync state: the write only applies when the stored
    /// <see cref="ReplicaState.LastSyncGeneration"/> and <see cref="ReplicaState.UploadBaseGeneration"/>
    /// still match the expected values the caller read before the sync. Returns false when another
    /// synchronization advanced the cursors concurrently (or the replica no longer exists), so the
    /// caller can reject the sync instead of clobbering the winner's cursor.
    /// </summary>
    Task<bool> TrySetSyncStateAsync(
        ReplicaState replica,
        long expectedLastSyncGeneration,
        long expectedUploadBaseGeneration,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<ReplicaState?> GetAsync(string replicaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the registered replicas for a service from the live registry. The <c>/replicas</c>
    /// enumeration is served from here so it reflects createReplica / unRegisterReplica immediately,
    /// rather than from a lagging cached snapshot.
    /// </summary>
    Task<IReadOnlyList<ReplicaState>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default);
}

internal sealed partial class DistributedReplicaStore : IReplicaStore
{
    private const string KeyPrefix = "featureserver:replica:";
    private const int MaxFallbackEntries = 5000;
    private static readonly TimeSpan _defaultTtl = TimeSpan.FromDays(7);

    private readonly IDistributedCache? _cache;
    private readonly ILogger<DistributedReplicaStore> _logger;
    private readonly ConcurrentDictionary<string, FallbackReplicaEntry> _fallback = new(StringComparer.OrdinalIgnoreCase);

    public DistributedReplicaStore(
        IDistributedCache? cache,
        ILogger<DistributedReplicaStore> logger)
    {
        _cache = cache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveTtl = ttl ?? _defaultTtl;
        var now = DateTimeOffset.UtcNow;

        if (_cache == null)
        {
            _fallback[replica.ReplicaId] = new FallbackReplicaEntry(replica, now.Add(effectiveTtl));
            CleanupFallback(now, enforceLimit: true);
            return;
        }

        try
        {
            var key = BuildKey(replica.ReplicaId);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new ReplicaStateEnvelope(replica, now.Add(effectiveTtl)),
                FeatureServerJsonContext.Default.ReplicaStateEnvelope);
            await _cache.SetAsync(key, payload, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = effectiveTtl
            }, cancellationToken).ConfigureAwait(false);
        }
        // Intentional catch-all: IDistributedCache implementations can throw a wide range of
        // provider-specific exceptions (network, serialization, timeout); all are logged and
        // translated to the single ServiceUnavailableException contract callers expect.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.WriteReplicaFailed(_logger, replica.ReplicaId, ex);
            throw new ServiceUnavailableException(
                "Distributed replica state is unavailable while attempting to persist replica state.",
                ex);
        }
    }

    public async Task<bool> TrySetSyncStateAsync(
        ReplicaState replica,
        long expectedLastSyncGeneration,
        long expectedUploadBaseGeneration,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveTtl = ttl ?? _defaultTtl;
        var now = DateTimeOffset.UtcNow;

        if (_cache == null)
        {
            // In-process fallback: a real compare-and-set loop over the concurrent dictionary.
            while (true)
            {
                if (!_fallback.TryGetValue(replica.ReplicaId, out var entry) || entry.ExpiresAt <= now)
                {
                    return false;
                }

                if (entry.Replica.LastSyncGeneration != expectedLastSyncGeneration ||
                    entry.Replica.UploadBaseGeneration != expectedUploadBaseGeneration)
                {
                    return false;
                }

                if (_fallback.TryUpdate(
                        replica.ReplicaId,
                        new FallbackReplicaEntry(replica, now.Add(effectiveTtl)),
                        entry))
                {
                    CleanupFallback(now, enforceLimit: true);
                    return true;
                }
            }
        }

        // IDistributedCache exposes no atomic compare-and-set, so the distributed path is a
        // best-effort read-compare-write. This store is the cache tier only; the authoritative
        // compare-and-set lives in IReplicaRepository (CachingReplicaStore writes through it).
        var current = await GetAsync(replica.ReplicaId, cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.LastSyncGeneration != expectedLastSyncGeneration ||
            current.UploadBaseGeneration != expectedUploadBaseGeneration)
        {
            return false;
        }

        await SetAsync(replica, ttl, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ReplicaState?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;

        if (_cache != null)
        {
            try
            {
                var payload = await _cache.GetAsync(BuildKey(replicaId), cancellationToken).ConfigureAwait(false);
                if (payload != null)
                {
                    if (TryDeserializeEnvelope(payload, out var envelope))
                    {
                        if (envelope!.ExpiresAt <= now)
                        {
                            return null;
                        }

                        return envelope.Replica;
                    }

                    var replica = JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.ReplicaState);
                    if (replica != null)
                    {
                        return replica;
                    }
                }
                return null;
            }
            // Intentional catch-all: a distributed cache read failure degrades to a cache miss
            // (logged) rather than surfacing a provider-specific exception to the caller.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.ReadReplicaFailed(_logger, replicaId, ex);
                return null;
            }
        }

        CleanupFallback(now, enforceLimit: false);
        if (_fallback.TryGetValue(replicaId, out var entry))
        {
            if (entry.ExpiresAt > now)
            {
                return entry.Replica;
            }

            _fallback.TryRemove(replicaId, out _);
        }

        return null;
    }

    public Task<IReadOnlyList<ReplicaState>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        // IDistributedCache cannot enumerate keys, so the distributed cache tier can only enumerate the
        // in-process fallback registry. When a distributed cache is configured the authoritative
        // enumeration is served by CachingReplicaStore through IReplicaRepository.
        var now = DateTimeOffset.UtcNow;
        if (_cache != null)
        {
            return Task.FromResult<IReadOnlyList<ReplicaState>>([]);
        }

        CleanupFallback(now, enforceLimit: false);
        var matches = _fallback.Values
            .Where(entry => entry.ExpiresAt > now
                && string.Equals(entry.Replica.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Replica)
            .OrderByDescending(replica => replica.CreatedAt)
            .ThenBy(replica => replica.ReplicaId, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ReplicaState>>(matches);
    }

    public async Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_cache == null)
        {
            return _fallback.TryRemove(replicaId, out _);
        }

        try
        {
            var key = BuildKey(replicaId);
            var existing = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return existing != null;
        }
        // Intentional catch-all: a distributed cache removal failure degrades to "not removed"
        // (logged) rather than surfacing a provider-specific exception to the caller.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RemoveReplicaFailed(_logger, replicaId, ex);
            return false;
        }
    }

    private static string BuildKey(string replicaId) => $"{KeyPrefix}{replicaId}";

    private void CleanupFallback(DateTimeOffset now, bool enforceLimit)
    {
        var expiredKeys = _fallback
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToArray();

        foreach (var expiredKey in expiredKeys)
        {
            _fallback.TryRemove(expiredKey, out _);
        }

        if (!enforceLimit || _fallback.Count <= MaxFallbackEntries)
        {
            return;
        }

        var overflow = _fallback.Count - MaxFallbackEntries;
        var oldestKeys = _fallback
            .OrderBy(kvp => kvp.Value.ExpiresAt)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToArray();

        foreach (var oldestKey in oldestKeys)
        {
            _fallback.TryRemove(oldestKey, out _);
        }
    }

    private sealed record FallbackReplicaEntry(ReplicaState Replica, DateTimeOffset ExpiresAt);
    internal sealed record ReplicaStateEnvelope(ReplicaState Replica, DateTimeOffset ExpiresAt);

    private static bool TryDeserializeEnvelope(byte[] payload, out ReplicaStateEnvelope? envelope)
    {
        try
        {
            envelope = JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.ReplicaStateEnvelope);
            return envelope != null;
        }
        catch (JsonException)
        {
            envelope = null;
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 7710, Level = LogLevel.Warning, Message = "Failed to persist replica state for {ReplicaId}")]
        public static partial void WriteReplicaFailed(ILogger logger, string replicaId, Exception exception);

        [LoggerMessage(EventId = 7711, Level = LogLevel.Warning, Message = "Failed to read replica state for {ReplicaId}")]
        public static partial void ReadReplicaFailed(ILogger logger, string replicaId, Exception exception);

        [LoggerMessage(EventId = 7712, Level = LogLevel.Warning, Message = "Failed to remove replica state for {ReplicaId}")]
        public static partial void RemoveReplicaFailed(ILogger logger, string replicaId, Exception exception);
    }
}

internal sealed record ReplicaState(
    string ReplicaId,
    string ReplicaName,
    string ServiceId,
    string SyncModel,
    int[] LayerIds,
    DateTimeOffset CreatedAt)
{
    public DateTimeOffset LastSyncTime { get; init; } = CreatedAt;

    public long LastSyncGeneration { get; init; }

    /// <summary>
    /// Server generation produced by the most recent upload sync. Used as the download "since"
    /// cursor so a replica does not receive its own just-applied edits back (#1272).
    /// </summary>
    public long UploadBaseGeneration { get; init; }
}
