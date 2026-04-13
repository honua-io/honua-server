// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Redis;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Redis;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Standardized Redis-backed distributed replica store with consistent fallback behavior.
/// </summary>
internal sealed partial class StandardizedDistributedReplicaStore : RedisServiceBase, IReplicaStore
{
    private const string KeyPrefix = "featureserver:replica:";
    private const int MaxFallbackEntries = 5000;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private readonly IDistributedCache? _distributedCache;
    private readonly ConcurrentDictionary<string, FallbackReplicaEntry> _fallback = new(StringComparer.OrdinalIgnoreCase);

    public StandardizedDistributedReplicaStore(
        IDistributedCache? distributedCache,
        IConnectionMultiplexer? redis,
        IRedisHealthMonitor healthMonitor,
        IHostEnvironment hostEnvironment,
        ILogger<StandardizedDistributedReplicaStore> logger)
        : base(redis, healthMonitor, RedisFallbackStrategy.InMemoryFallback, hostEnvironment, logger)
    {
        _distributedCache = distributedCache;

        Log.ReplicaStoreInitialized(Logger, FallbackMode, IsRedisConfigured);
    }

    public async Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveTtl = ttl ?? DefaultTtl;
        var now = DateTimeOffset.UtcNow;
        var expirationTime = now.Add(effectiveTtl);

        try
        {
            await ExecuteWithFallbackAsync(
                "set-replica",
                async (database, ct) =>
                {
                    if (_distributedCache != null)
                    {
                        var key = BuildKey(replica.ReplicaId);
                        var payload = JsonSerializer.SerializeToUtf8Bytes(
                            new ReplicaStateEnvelope(replica, expirationTime),
                            FeatureServerJsonContext.Default.ReplicaStateEnvelope);

                        await _distributedCache.SetAsync(key, payload, new DistributedCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = effectiveTtl
                        }, ct).ConfigureAwait(false);
                    }
                    return true;
                },
                fallbackOperation: ct =>
                {
                    SetFallback(replica, expirationTime);
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            Log.ReplicaStateSet(Logger, replica.ReplicaId, IsUsingRedis ? "Redis" : "InMemory");
        }
        catch (Exception ex)
        {
            Log.SetReplicaFailed(Logger, replica.ReplicaId, ex);
            // On failure, always try to store in fallback to maintain availability
            SetFallback(replica, expirationTime);
        }
    }

    public async Task<ReplicaState?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;

        try
        {
            var replica = await ExecuteWithFallbackAsync(
                "get-replica",
                async (database, ct) =>
                {
                    if (_distributedCache != null)
                    {
                        var payload = await _distributedCache.GetAsync(BuildKey(replicaId), ct).ConfigureAwait(false);
                        if (payload != null)
                        {
                            if (TryDeserializeEnvelope(payload, out var envelope))
                            {
                                return envelope!.ExpiresAt <= now ? null : envelope.Replica;
                            }

                            // Try legacy format
                            var legacyReplica = JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.ReplicaState);
                            return legacyReplica;
                        }
                    }
                    return null;
                },
                fallbackOperation: ct => Task.FromResult(GetFallback(replicaId, now)),
                cancellationToken).ConfigureAwait(false);

            Log.ReplicaStateRetrieved(Logger, replicaId, replica != null, IsUsingRedis ? "Redis" : "InMemory");
            return replica;
        }
        catch (Exception ex)
        {
            Log.GetReplicaFailed(Logger, replicaId, ex);
            // On failure, try fallback
            return GetFallback(replicaId, now);
        }
    }

    public async Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var removed = await ExecuteWithFallbackAsync(
                "remove-replica",
                async (database, ct) =>
                {
                    if (_distributedCache != null)
                    {
                        var key = BuildKey(replicaId);
                        var existing = await _distributedCache.GetAsync(key, ct).ConfigureAwait(false);
                        await _distributedCache.RemoveAsync(key, ct).ConfigureAwait(false);
                        return existing != null;
                    }
                    return false;
                },
                fallbackOperation: ct => Task.FromResult(RemoveFallback(replicaId)),
                cancellationToken).ConfigureAwait(false);

            Log.ReplicaStateRemoved(Logger, replicaId, removed, IsUsingRedis ? "Redis" : "InMemory");
            return removed;
        }
        catch (Exception ex)
        {
            Log.RemoveReplicaFailed(Logger, replicaId, ex);
            // On failure, try fallback
            return RemoveFallback(replicaId);
        }
    }

    private static string BuildKey(string replicaId) => $"{KeyPrefix}{replicaId}";

    private void SetFallback(ReplicaState replica, DateTimeOffset expirationTime)
    {
        _fallback[replica.ReplicaId] = new FallbackReplicaEntry(replica, expirationTime);
        CleanupFallback(DateTimeOffset.UtcNow, enforceLimit: true);
    }

    private ReplicaState? GetFallback(string replicaId, DateTimeOffset now)
    {
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

    private bool RemoveFallback(string replicaId)
    {
        return _fallback.TryRemove(replicaId, out _);
    }

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

        Log.FallbackCacheEvicted(Logger, overflow);
    }

    private static bool TryDeserializeEnvelope(byte[] payload, out DistributedReplicaStore.ReplicaStateEnvelope? envelope)
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

    private sealed record FallbackReplicaEntry(ReplicaState Replica, DateTimeOffset ExpiresAt);

    private static partial class Log
    {
        [LoggerMessage(EventId = 9301, Level = LogLevel.Information,
            Message = "Replica store initialized (Mode: {Mode}, Redis: {RedisConfigured})")]
        public static partial void ReplicaStoreInitialized(ILogger logger, RedisFallbackMode mode, bool redisConfigured);

        [LoggerMessage(EventId = 9302, Level = LogLevel.Debug,
            Message = "Replica state set (ReplicaId: {ReplicaId}, Source: {Source})")]
        public static partial void ReplicaStateSet(ILogger logger, string replicaId, string source);

        [LoggerMessage(EventId = 9303, Level = LogLevel.Debug,
            Message = "Replica state retrieved (ReplicaId: {ReplicaId}, Found: {Found}, Source: {Source})")]
        public static partial void ReplicaStateRetrieved(ILogger logger, string replicaId, bool found, string source);

        [LoggerMessage(EventId = 9304, Level = LogLevel.Debug,
            Message = "Replica state removed (ReplicaId: {ReplicaId}, Found: {Found}, Source: {Source})")]
        public static partial void ReplicaStateRemoved(ILogger logger, string replicaId, bool found, string source);

        [LoggerMessage(EventId = 9305, Level = LogLevel.Warning,
            Message = "Set replica failed (ReplicaId: {ReplicaId})")]
        public static partial void SetReplicaFailed(ILogger logger, string replicaId, Exception exception);

        [LoggerMessage(EventId = 9306, Level = LogLevel.Warning,
            Message = "Get replica failed (ReplicaId: {ReplicaId})")]
        public static partial void GetReplicaFailed(ILogger logger, string replicaId, Exception exception);

        [LoggerMessage(EventId = 9307, Level = LogLevel.Warning,
            Message = "Remove replica failed (ReplicaId: {ReplicaId})")]
        public static partial void RemoveReplicaFailed(ILogger logger, string replicaId, Exception exception);

        [LoggerMessage(EventId = 9308, Level = LogLevel.Information,
            Message = "Fallback cache evicted {Count} entries")]
        public static partial void FallbackCacheEvicted(ILogger logger, int count);
    }
}