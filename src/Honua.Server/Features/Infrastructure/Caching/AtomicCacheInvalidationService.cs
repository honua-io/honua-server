// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Provides atomic cache invalidation operations to prevent race conditions
/// during cache updates and invalidations.
/// </summary>
public interface IAtomicCacheInvalidationService
{
    /// <summary>
    /// Atomically invalidates a cache entry and ensures no concurrent operations
    /// are affected by race conditions.
    /// </summary>
    Task InvalidateAtomicAsync(string cacheKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically invalidates multiple cache entries with a pattern.
    /// </summary>
    Task InvalidatePatternAtomicAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a two-phase cache update: invalidate, then allow repopulation.
    /// Prevents cache stampede during invalidation.
    /// </summary>
    Task<T> UpdateAtomicAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> valueFactory,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of atomic cache invalidation service that coordinates
/// invalidations across distributed and memory caches while preventing race conditions.
/// </summary>
internal sealed class AtomicCacheInvalidationService : IAtomicCacheInvalidationService
{
    private readonly IDistributedCache? _distributedCache;
    private readonly IMemoryCache? _memoryCache;
    private readonly ILogger<AtomicCacheInvalidationService> _logger;

    // Coordination locks to prevent concurrent invalidations of the same key
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _invalidationLocks = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _updateLocks = new();

    public AtomicCacheInvalidationService(
        IDistributedCache? distributedCache,
        IMemoryCache? memoryCache,
        ILogger<AtomicCacheInvalidationService> logger)
    {
        _distributedCache = distributedCache;
        _memoryCache = memoryCache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvalidateAtomicAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cacheKey))
        {
            return;
        }

        var lockKey = $"invalidate:{cacheKey}";
        var semaphore = _invalidationLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken);

            // Invalidate from distributed cache first
            if (_distributedCache != null)
            {
                await _distributedCache.RemoveAsync(cacheKey, cancellationToken);
            }

            // Then invalidate from memory cache
            _memoryCache?.Remove(cacheKey);

            // Also cancel any pending updates for this key to prevent stale data
            if (_updateLocks.TryRemove(cacheKey, out var pendingUpdate))
            {
                pendingUpdate.TrySetCanceled(cancellationToken);
            }

            _logger.LogDebug("Cache key {CacheKey} invalidated atomically", cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to atomically invalidate cache key {CacheKey}", cacheKey);
            throw;
        }
        finally
        {
            semaphore.Release();

            // Clean up the lock if no one else is waiting
            if (semaphore.CurrentCount == 1 && _invalidationLocks.TryRemove(lockKey, out var removedSemaphore))
            {
                if (removedSemaphore != semaphore)
                {
                    // Someone else added a new one, put it back
                    _invalidationLocks.TryAdd(lockKey, removedSemaphore);
                }
            }
        }
    }

    public async Task InvalidatePatternAtomicAsync(string pattern, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }

        _logger.LogDebug("Starting atomic pattern invalidation for pattern: {Pattern}", pattern);

        try
        {
            // For distributed cache, we need to implement pattern-based invalidation
            // This is cache-implementation specific (Redis supports KEYS command, others may not)
            if (_distributedCache is Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache redisCache)
            {
                await InvalidateRedisPatternAsync(redisCache, pattern, cancellationToken);
            }

            // For memory cache, we need to track keys ourselves since IMemoryCache doesn't expose keys
            // This is a limitation - consider using a wrapper that tracks keys if pattern invalidation is critical
            _logger.LogWarning("Pattern invalidation for memory cache not fully supported. Pattern: {Pattern}", pattern);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate cache pattern {Pattern}", pattern);
            throw;
        }
    }

    public async Task<T> UpdateAtomicAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> valueFactory,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cacheKey))
        {
            throw new ArgumentException("Cache key cannot be null or empty", nameof(cacheKey));
        }

        if (valueFactory == null)
        {
            throw new ArgumentNullException(nameof(valueFactory));
        }

        // Check if someone else is already updating this key
        var tcs = new TaskCompletionSource<object?>();
        var existingTcs = _updateLocks.GetOrAdd(cacheKey, tcs);

        if (existingTcs != tcs)
        {
            // Another update is in progress, wait for it
            try
            {
                var result = await existingTcs.Task;
                return (T)result!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concurrent cache update failed for key {CacheKey}, will retry", cacheKey);
                // If the concurrent update failed, we'll fall through to try ourselves
                _updateLocks.TryRemove(cacheKey, out _);
            }
        }

        try
        {
            // We're responsible for the update
            _logger.LogDebug("Starting atomic cache update for key {CacheKey}", cacheKey);

            // Step 1: Invalidate existing cache entries
            await InvalidateAtomicAsync(cacheKey, cancellationToken);

            // Step 2: Generate new value
            var newValue = await valueFactory(cancellationToken);

            // Step 3: Store in caches
            var options = new MemoryCacheEntryOptions();
            var distributedOptions = new DistributedCacheEntryOptions();

            if (expiry.HasValue)
            {
                options.AbsoluteExpirationRelativeToNow = expiry.Value;
                distributedOptions.AbsoluteExpirationRelativeToNow = expiry.Value;
            }

            // Store in memory cache first (faster)
            _memoryCache?.Set(cacheKey, newValue, options);

            // Then store in distributed cache
            if (_distributedCache != null && newValue != null)
            {
                var serialized = System.Text.Json.JsonSerializer.Serialize(newValue);
                await _distributedCache.SetStringAsync(cacheKey, serialized, distributedOptions, cancellationToken);
            }

            // Signal success to waiting threads
            tcs.SetResult(newValue);

            _logger.LogDebug("Completed atomic cache update for key {CacheKey}", cacheKey);
            return newValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed atomic cache update for key {CacheKey}", cacheKey);
            tcs.SetException(ex);
            throw;
        }
        finally
        {
            _updateLocks.TryRemove(cacheKey, out _);
        }
    }

    private async Task InvalidateRedisPatternAsync(
        Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache redisCache,
        string pattern,
        CancellationToken cancellationToken)
    {
        try
        {
            // Use reflection to access the underlying Redis connection
            var type = redisCache.GetType();
            var connectionField = type.GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var database = connectionField?.GetValue(redisCache) as StackExchange.Redis.IDatabase;

            if (database?.Multiplexer != null)
            {
                var server = database.Multiplexer.GetServer(database.Multiplexer.GetEndPoints().First());

                // Use SCAN instead of KEYS for better performance in production
                await foreach (var key in server.ScanAsync(pattern: pattern))
                {
                    await database.KeyDeleteAsync(key);

                    if (cancellationToken.IsCancellationRequested)
                        break;
                }

                _logger.LogDebug("Redis pattern invalidation completed for pattern: {Pattern}", pattern);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed Redis pattern invalidation for pattern: {Pattern}", pattern);
            // Don't rethrow - this is a best-effort operation
        }
    }
}

/// <summary>
/// Service collection extensions for atomic cache invalidation.
/// </summary>
public static class AtomicCacheInvalidationServiceCollectionExtensions
{
    /// <summary>
    /// Adds atomic cache invalidation services to the service collection.
    /// </summary>
    public static IServiceCollection AddAtomicCacheInvalidation(this IServiceCollection services)
    {
        services.AddScoped<IAtomicCacheInvalidationService, AtomicCacheInvalidationService>();
        return services;
    }
}