// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Honua.Server.Features.FeatureServer.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Honua.Server.Features.FeatureServer;

internal interface IReplicaStore
{
    Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    Task<ReplicaState?> GetAsync(string replicaId, CancellationToken cancellationToken = default);

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
        _fallback[replica.ReplicaId] = new FallbackReplicaEntry(replica, now.Add(effectiveTtl));
        CleanupFallback(now, enforceLimit: true);

        if (_cache == null)
        {
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
        catch (Exception ex)
        {
            Log.WriteReplicaFailed(_logger, replica.ReplicaId, ex);
        }
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
                            _fallback.TryRemove(replicaId, out _);
                            return null;
                        }

                        _fallback[replicaId] = new FallbackReplicaEntry(envelope.Replica, envelope.ExpiresAt);
                        return envelope.Replica;
                    }

                    var replica = JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.ReplicaState);
                    if (replica != null)
                    {
                        return replica;
                    }
                }
                else
                {
                    _fallback.TryRemove(replicaId, out _);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.ReadReplicaFailed(_logger, replicaId, ex);
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

    public async Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        cancellationToken.ThrowIfCancellationRequested();

        var removedFromFallback = _fallback.TryRemove(replicaId, out _);
        if (_cache == null)
        {
            return removedFromFallback;
        }

        try
        {
            var key = BuildKey(replicaId);
            var existing = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return removedFromFallback || existing != null;
        }
        catch (Exception ex)
        {
            Log.RemoveReplicaFailed(_logger, replicaId, ex);
            return removedFromFallback;
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
}
