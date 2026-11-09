// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Caching.Distributed;

namespace Honua.Infrastructure.Caching;

internal sealed partial class RedisCacheService
{
    private const int MaximumRecoveryFences = 3;
    private readonly object _redisRecoveryLock = new();
    private bool _restoringRedis;
    private long _responseInvalidationRevision;

    private void TrackResponseInvalidation(string key)
    {
        if (_options.ResponseCachingEnabled && key.StartsWith(ResponseCacheVersionKeys.Prefix, StringComparison.Ordinal))
        {
            lock (_redisRecoveryLock)
            {
                _responseInvalidationRevision++;
            }
        }
    }

    private async Task<bool> TryRestoreRedisAsync(CancellationToken cancellationToken)
    {
        lock (_redisRecoveryLock)
        {
            if (_distributedCache is null || _restoringRedis)
            {
                return false;
            }
            if (!_isUsingFallback)
            {
                return true;
            }
            _restoringRedis = true;
        }
        try
        {
            for (var attempt = 0; attempt < MaximumRecoveryFences; attempt++)
            {
                long invalidationRevision;
                lock (_redisRecoveryLock)
                {
                    invalidationRevision = _responseInvalidationRevision;
                }
                await _redisGetPolicy.ExecuteAsync(
                    ct => _distributedCache.GetAsync(HealthCheckKey, ct), cancellationToken).ConfigureAwait(false);
                await ReplayPendingInvalidationsAsync(cancellationToken).ConfigureAwait(false);
                if (_options.ResponseCachingEnabled)
                {
                    await PublishResponseRecoveryEpochAsync(cancellationToken).ConfigureAwait(false);
                }
                lock (_redisRecoveryLock)
                {
                    if (invalidationRevision != _responseInvalidationRevision)
                    {
                        continue;
                    }
                    _fallbackCache.Clear();
                    _writeMetadata.Clear();
                    _isUsingFallback = false;
                    RedisCacheServiceLog.RedisConnectionRestored(_logger);
                    return true;
                }
            }
            Volatile.Write(ref _lastRedisFailureTicks, DateTime.UtcNow.Ticks);
            RedisCacheServiceLog.RedisRecoveryFencesExhausted(_logger);
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RedisCacheServiceLog.RedisRestoreAttemptFailed(_logger, exception);
            Volatile.Write(ref _lastRedisFailureTicks, DateTime.UtcNow.Ticks);
            return false;
        }
        finally
        {
            lock (_redisRecoveryLock)
            {
                _restoringRedis = false;
            }
        }
    }

    private Task PublishResponseRecoveryEpochAsync(CancellationToken cancellationToken)
    {
        var key = GetPrefixedKey(ResponseCacheVersionKeys.RecoveryEpoch);
        if (!TrySerialize(Guid.NewGuid().ToString("N"), key, out var payload))
        {
            throw new InvalidOperationException("Response-cache recovery generation could not be serialized.");
        }
        return SetDistributedEntryAsync(new DistributedCacheWrite(key, payload, ResponseCacheVersionKeys.Lifetime), cancellationToken);
    }

    private async Task SetDistributedEntryAsync(DistributedCacheWrite entry, CancellationToken cancellationToken)
    {
        if (_redis is not null)
        {
            await _redisPolicy.ExecuteAsync(
                _ => SetRedisEntryWithIndexAsync(entry.Key, entry.Payload, entry.Ttl, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        else if (_distributedCache is not null)
        {
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = entry.Ttl };
            await _redisPolicy.ExecuteAsync(
                ct => _distributedCache.SetAsync(entry.Key, entry.Payload, options, ct), cancellationToken).ConfigureAwait(false);
            await TrackIndexedKeyAsync(entry.Key, entry.Ttl, cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct DistributedCacheWrite(string Key, byte[] Payload, TimeSpan Ttl);
}
