// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Distributed cache refresh coordinator using Redis for cross-instance coordination.
/// Falls back to local coordination when Redis is unavailable.
/// </summary>
/// <remarks>
/// Uses Redis SET NX operations for atomic distributed deduplication and Redis pub/sub
/// for cross-instance invalidation notifications. Maintains graceful fallback to local
/// coordination when Redis is unavailable.
/// </remarks>
internal sealed partial class DistributedCacheRefreshCoordinator : BackgroundService, Honua.Core.Features.Caching.Abstractions.IDistributedCacheRefreshCoordinator
{
    private const string MetricsCacheType = "background-refresh";
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RedisLockExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RedisRetryBackoff = TimeSpan.FromSeconds(30);

    private const string RedisKeyPrefix = "honua:cache:refresh:";
    private const string RedisInvalidationChannel = "honua:cache:invalidations";

    private readonly Channel<CacheRefreshItem> _channel;
    private readonly IDatabase? _redisDb;
    private readonly ISubscriber? _redisSubscriber;
    private readonly string _instanceId;
    private readonly bool _allowFallback;

    // Local state for fallback mode and metrics
    private readonly ConcurrentDictionary<string, byte> _localPendingKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _retryAfterUtcTicks = new(StringComparer.Ordinal);

    private readonly CacheOptions _options;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<DistributedCacheRefreshCoordinator> _logger;
    private long _successCount;
    private long _failureCount;
    private long _skippedCount;
    private DateTime _lastRedisFailure = DateTime.MinValue;
    private volatile bool _useRedis;

    public DistributedCacheRefreshCoordinator(
        IOptions<CacheOptions> options,
        IPerformanceMonitor performanceMonitor,
        ILogger<DistributedCacheRefreshCoordinator> logger,
        IConnectionMultiplexer? redis = null,
        bool allowFallback = true)
    {
        _options = options.Value;
        _performanceMonitor = performanceMonitor;
        _logger = logger;
        _instanceId = Environment.MachineName + "_" + Guid.NewGuid().ToString("N")[..8];
        _allowFallback = allowFallback;

        if (redis?.IsConnected == true)
        {
            _redisDb = redis.GetDatabase();
            _redisSubscriber = redis.GetSubscriber();
            _useRedis = true;
            Log.DistributedModeEnabled(_logger);
        }
        else
        {
            _useRedis = false;
            if (!_allowFallback)
            {
                throw new InvalidOperationException("Redis is required for distributed cache coordination but is not available");
            }
            Log.FallbackModeEnabled(_logger);
        }

        // Bounded channel prevents unbounded memory growth if refresh callbacks are slow.
        _channel = Channel.CreateBounded<CacheRefreshItem>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false
        });

        // Subscribe to invalidation notifications if Redis is available
        if (_redisSubscriber != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _redisSubscriber.SubscribeAsync(RedisChannel.Literal(RedisInvalidationChannel), OnInvalidationReceived);
                    Log.InvalidationSubscriptionStarted(_logger);
                }
                catch (Exception ex)
                {
                    Log.InvalidationSubscriptionFailed(_logger, ex);
                }
            });
        }
    }

    /// <inheritdoc />
    public bool IsDistributed => _useRedis && _redisDb != null;

    /// <inheritdoc />
    public int QueueDepth => IsDistributed ? GetDistributedQueueDepth() : _localPendingKeys.Count;

    /// <inheritdoc />
    public long SuccessCount => Volatile.Read(ref _successCount);

    /// <inheritdoc />
    public long FailureCount => Volatile.Read(ref _failureCount);

    /// <inheritdoc />
    public long SkippedCount => Volatile.Read(ref _skippedCount);

    /// <inheritdoc />
    public void NotifyInvalidation(string key)
    {
        _retryAfterUtcTicks.TryRemove(key, out _);

        if (IsDistributed)
        {
            NotifyDistributedInvalidation(key);
        }
        else
        {
            NotifyLocalInvalidation(key);
        }
    }

    /// <inheritdoc />
    public async Task NotifyInvalidationClusterWideAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_redisSubscriber == null)
        {
            // Fall back to local invalidation
            NotifyInvalidation(key);
            return;
        }

        try
        {
            await _redisSubscriber.PublishAsync(RedisChannel.Literal(RedisInvalidationChannel), key);
            Log.ClusterInvalidationSent(_logger, key);
        }
        catch (Exception ex)
        {
            Log.ClusterInvalidationFailed(_logger, key, ex);
            // Fall back to local invalidation
            NotifyInvalidation(key);
        }
    }

    /// <inheritdoc />
    public bool WasInvalidated(string key)
    {
        if (IsDistributed)
        {
            return WasDistributedInvalidated(key);
        }
        else
        {
            return _localPendingKeys.TryGetValue(key, out byte val) && val == 1;
        }
    }

    /// <inheritdoc />
    public bool TryClaimWriteBack(string key)
    {
        if (IsDistributed)
        {
            return TryClaimDistributedWriteBack(key);
        }
        else
        {
            return _localPendingKeys.TryUpdate(key, 2, 0);
        }
    }

    /// <inheritdoc />
    public bool TryEnqueueRefresh(string key, Func<CancellationToken, Task> refreshCallback)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (IsWithinRetryBackoff(key, nowTicks))
        {
            return false;
        }

        bool claimed;
        if (IsDistributed)
        {
            claimed = TryClaimDistributedRefresh(key);
        }
        else
        {
            claimed = _localPendingKeys.TryAdd(key, 0);
        }

        if (!claimed)
        {
            return false;
        }

        // Re-check after the pending claim to close the race with a recently failed
        // refresh that may have published its retry backoff concurrently.
        if (IsWithinRetryBackoff(key, DateTimeOffset.UtcNow.UtcTicks))
        {
            ReleaseRefreshClaim(key);
            return false;
        }

        // Try to write to channel; with DropWrite, this returns false when the channel is full
        if (!_channel.Writer.TryWrite(new CacheRefreshItem(key, refreshCallback)))
        {
            ReleaseRefreshClaim(key);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BackgroundRefreshEnabled)
        {
            Log.BackgroundRefreshDisabled(_logger);
            return;
        }

        Log.BackgroundRefreshStarted(_logger, _options.MaxConcurrentRefreshes, IsDistributed);

        using var semaphore = new SemaphoreSlim(_options.MaxConcurrentRefreshes, _options.MaxConcurrentRefreshes);

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);

            // Fire-and-forget with bounded concurrency
            _ = ProcessRefreshAsync(item, semaphore, stoppingToken);
        }
    }

    private async Task ProcessRefreshAsync(CacheRefreshItem item, SemaphoreSlim semaphore, CancellationToken stoppingToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(_options.RefreshTimeout);

            using var scope = _performanceMonitor.StartOperation("cache_background_refresh")
                .WithTag("cache_type", MetricsCacheType)
                .WithTag("key_family", CacheKeyUtilities.GetCacheKeyFamily(item.Key))
                .WithTag("distributed", IsDistributed.ToString());

            await item.RefreshCallback(timeoutCts.Token).ConfigureAwait(false);

            // Distinguish real refreshes from callbacks that returned early because
            // the key was invalidated while the refresh was in-flight.
            if (WasInvalidated(item.Key))
            {
                Interlocked.Increment(ref _skippedCount);
                _performanceMonitor.RecordCacheMetrics(MetricsCacheType, "refresh_skipped");
                scope.WithTag("result", "skipped_invalidated");
            }
            else
            {
                Interlocked.Increment(ref _successCount);
                _performanceMonitor.RecordCacheMetrics(MetricsCacheType, "refresh_success");
                scope.WithTag("result", "success");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Application shutting down — not a failure
        }
        catch (OperationCanceledException)
        {
            // Refresh timeout — count as failure
            SetRetryBackoff(item.Key);
            Interlocked.Increment(ref _failureCount);
            _performanceMonitor.RecordCacheMetrics(MetricsCacheType, "refresh_timeout");
            Log.BackgroundRefreshTimeout(_logger, item.Key);
        }
        catch (Exception ex)
        {
            SetRetryBackoff(item.Key);
            Interlocked.Increment(ref _failureCount);
            _performanceMonitor.RecordCacheMetrics(MetricsCacheType, "refresh_failure");
            Log.BackgroundRefreshFailed(_logger, item.Key, ex);
        }
        finally
        {
            ReleaseRefreshClaim(item.Key);

            try
            {
                semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Semaphore disposed during shutdown while this fire-and-forget task was in-flight.
            }
        }
    }

    private bool TryClaimDistributedRefresh(string key)
    {
        if (_redisDb == null || !ShouldUseRedis())
        {
            return _localPendingKeys.TryAdd(key, 0);
        }

        try
        {
            var redisKey = RedisKeyPrefix + "pending:" + key;
            var acquired = _redisDb.StringSet(redisKey, _instanceId, RedisLockExpiry, When.NotExists);

            if (acquired)
            {
                // Also track locally for metrics and fallback scenarios
                _localPendingKeys.TryAdd(key, 0);
            }

            return acquired;
        }
        catch (Exception ex)
        {
            Log.RedisOperationFailed(_logger, "TryClaimRefresh", key, ex);
            MarkRedisFailure();
            return _localPendingKeys.TryAdd(key, 0);
        }
    }

    private bool TryClaimDistributedWriteBack(string key)
    {
        if (_redisDb == null || !ShouldUseRedis())
        {
            return _localPendingKeys.TryUpdate(key, 2, 0);
        }

        try
        {
            var pendingKey = RedisKeyPrefix + "pending:" + key;
            var invalidatedKey = RedisKeyPrefix + "invalidated:" + key;

            // Check if key was invalidated
            if (_redisDb.KeyExists(invalidatedKey))
            {
                return false;
            }

            // Try to claim write-back by updating the pending key value
            var script = @"
                local pendingKey = KEYS[1]
                local invalidatedKey = KEYS[2]
                local instanceId = ARGV[1]
                local claimedValue = ARGV[2]

                -- Check if invalidated
                if redis.call('EXISTS', invalidatedKey) == 1 then
                    return 0
                end

                -- Check if we own the pending lock
                local current = redis.call('GET', pendingKey)
                if current == instanceId then
                    redis.call('SET', pendingKey, claimedValue, 'EX', 300)
                    return 1
                end

                return 0
            ";

            var result = (int)_redisDb.ScriptEvaluate(script,
                new RedisKey[] { pendingKey, invalidatedKey },
                new RedisValue[] { _instanceId, _instanceId + ":claimed" });

            if (result == 1)
            {
                _localPendingKeys.TryUpdate(key, 2, 0);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.RedisOperationFailed(_logger, "TryClaimWriteBack", key, ex);
            MarkRedisFailure();
            return _localPendingKeys.TryUpdate(key, 2, 0);
        }
    }

    private bool WasDistributedInvalidated(string key)
    {
        if (_redisDb == null || !ShouldUseRedis())
        {
            return _localPendingKeys.TryGetValue(key, out byte val) && val == 1;
        }

        try
        {
            var invalidatedKey = RedisKeyPrefix + "invalidated:" + key;
            return _redisDb.KeyExists(invalidatedKey);
        }
        catch (Exception ex)
        {
            Log.RedisOperationFailed(_logger, "WasInvalidated", key, ex);
            MarkRedisFailure();
            return _localPendingKeys.TryGetValue(key, out byte val) && val == 1;
        }
    }

    private void NotifyDistributedInvalidation(string key)
    {
        if (_redisDb == null || !ShouldUseRedis())
        {
            NotifyLocalInvalidation(key);
            return;
        }

        try
        {
            var invalidatedKey = RedisKeyPrefix + "invalidated:" + key;
            var pendingKey = RedisKeyPrefix + "pending:" + key;

            // Set invalidation flag and remove pending claim
            var script = @"
                local invalidatedKey = KEYS[1]
                local pendingKey = KEYS[2]

                redis.call('SET', invalidatedKey, '1', 'EX', 300)
                redis.call('DEL', pendingKey)

                return 'OK'
            ";

            _redisDb.ScriptEvaluate(script, new RedisKey[] { invalidatedKey, pendingKey });

            // Also handle local state
            NotifyLocalInvalidation(key);
        }
        catch (Exception ex)
        {
            Log.RedisOperationFailed(_logger, "NotifyInvalidation", key, ex);
            MarkRedisFailure();
            NotifyLocalInvalidation(key);
        }
    }

    private void NotifyLocalInvalidation(string key)
    {
        // Atomically mark the key as invalidated regardless of current state.
        // 0 → 1: marks a pending refresh as invalidated (before write-back claimed it).
        // 2 → 1: marks a write-claimed refresh as invalidated.
        // If the key is not present (no pending refresh), invalidation is a no-op
        // to avoid creating stale markers that would block a later TryEnqueueRefresh.
        _localPendingKeys.TryUpdate(key, 1, 0);
        _localPendingKeys.TryUpdate(key, 1, 2);
    }

    private void ReleaseRefreshClaim(string key)
    {
        if (IsDistributed)
        {
            ReleaseDistributedClaim(key);
        }
        else
        {
            _localPendingKeys.TryRemove(key, out _);
        }
    }

    private void ReleaseDistributedClaim(string key)
    {
        if (_redisDb == null || !ShouldUseRedis())
        {
            _localPendingKeys.TryRemove(key, out _);
            return;
        }

        try
        {
            var pendingKey = RedisKeyPrefix + "pending:" + key;
            var invalidatedKey = RedisKeyPrefix + "invalidated:" + key;

            // Only delete if we own the lock
            var script = @"
                local pendingKey = KEYS[1]
                local invalidatedKey = KEYS[2]
                local instanceId = ARGV[1]

                local current = redis.call('GET', pendingKey)
                if current == instanceId or string.match(current or '', instanceId .. ':') then
                    redis.call('DEL', pendingKey)
                end

                -- Cleanup invalidation flag if it exists (after a delay for safety)
                redis.call('EXPIRE', invalidatedKey, 60)

                return 'OK'
            ";

            _redisDb.ScriptEvaluate(script,
                new RedisKey[] { pendingKey, invalidatedKey },
                new RedisValue[] { _instanceId });
        }
        catch (Exception ex)
        {
            Log.RedisOperationFailed(_logger, "ReleaseClaim", key, ex);
            MarkRedisFailure();
        }
        finally
        {
            _localPendingKeys.TryRemove(key, out _);
        }
    }

    private int GetDistributedQueueDepth()
    {
        if (_redisDb == null || !ShouldUseRedis())
        {
            return _localPendingKeys.Count;
        }

        try
        {
            var pattern = RedisKeyPrefix + "pending:*";
            var server = _redisDb.Multiplexer.GetServer(_redisDb.Multiplexer.GetEndPoints().First());
            return (int)server.Keys(database: _redisDb.Database, pattern: pattern).Count();
        }
        catch
        {
            return _localPendingKeys.Count;
        }
    }

    private bool IsWithinRetryBackoff(string key, long nowTicks)
    {
        if (!_retryAfterUtcTicks.TryGetValue(key, out var retryAfterTicks))
        {
            return false;
        }

        if (retryAfterTicks > nowTicks)
        {
            return true;
        }

        _retryAfterUtcTicks.TryRemove(key, out _);
        return false;
    }

    private void SetRetryBackoff(string key)
    {
        _retryAfterUtcTicks[key] = DateTimeOffset.UtcNow.Add(FailureBackoff).UtcTicks;
    }

    private bool ShouldUseRedis()
    {
        if (!_useRedis || _redisDb?.Multiplexer.IsConnected != true)
        {
            return false;
        }

        // If we've had recent Redis failures, wait before retrying
        if (_lastRedisFailure != DateTime.MinValue &&
            DateTime.UtcNow - _lastRedisFailure < RedisRetryBackoff)
        {
            return false;
        }

        return true;
    }

    private void MarkRedisFailure()
    {
        _lastRedisFailure = DateTime.UtcNow;
    }

    private void OnInvalidationReceived(RedisChannel channel, RedisValue message)
    {
        try
        {
            var key = message.ToString();
            if (!string.IsNullOrEmpty(key))
            {
                NotifyLocalInvalidation(key);
                Log.RemoteInvalidationReceived(_logger, key);
            }
        }
        catch (Exception ex)
        {
            Log.InvalidationProcessingFailed(_logger, message, ex);
        }
    }

    private sealed record CacheRefreshItem(string Key, Func<CancellationToken, Task> RefreshCallback);

    private static partial class Log
    {
        [LoggerMessage(1101, LogLevel.Information, "Background cache refresh started with max {MaxConcurrent} concurrent operations (distributed: {IsDistributed})")]
        public static partial void BackgroundRefreshStarted(ILogger logger, int maxConcurrent, bool isDistributed);

        [LoggerMessage(1102, LogLevel.Information, "Background cache refresh disabled by configuration")]
        public static partial void BackgroundRefreshDisabled(ILogger logger);

        [LoggerMessage(1103, LogLevel.Warning, "Background refresh failed for key {CacheKey}")]
        public static partial void BackgroundRefreshFailed(ILogger logger, string cacheKey, Exception exception);

        [LoggerMessage(1104, LogLevel.Warning, "Background refresh timed out for key {CacheKey}")]
        public static partial void BackgroundRefreshTimeout(ILogger logger, string cacheKey);

        [LoggerMessage(1105, LogLevel.Information, "Distributed cache coordination enabled")]
        public static partial void DistributedModeEnabled(ILogger logger);

        [LoggerMessage(1106, LogLevel.Information, "Cache coordination running in local fallback mode")]
        public static partial void FallbackModeEnabled(ILogger logger);

        [LoggerMessage(1107, LogLevel.Warning, "Redis operation {Operation} failed for key {CacheKey}")]
        public static partial void RedisOperationFailed(ILogger logger, string operation, string cacheKey, Exception exception);

        [LoggerMessage(1108, LogLevel.Debug, "Cluster-wide invalidation sent for key {CacheKey}")]
        public static partial void ClusterInvalidationSent(ILogger logger, string cacheKey);

        [LoggerMessage(1109, LogLevel.Warning, "Cluster-wide invalidation failed for key {CacheKey}")]
        public static partial void ClusterInvalidationFailed(ILogger logger, string cacheKey, Exception exception);

        [LoggerMessage(1110, LogLevel.Debug, "Remote invalidation received for key {CacheKey}")]
        public static partial void RemoteInvalidationReceived(ILogger logger, string cacheKey);

        [LoggerMessage(1111, LogLevel.Information, "Invalidation subscription started")]
        public static partial void InvalidationSubscriptionStarted(ILogger logger);

        [LoggerMessage(1112, LogLevel.Warning, "Invalidation subscription failed")]
        public static partial void InvalidationSubscriptionFailed(ILogger logger, Exception exception);

        [LoggerMessage(1113, LogLevel.Warning, "Failed to process invalidation message {Message}")]
        public static partial void InvalidationProcessingFailed(ILogger logger, RedisValue message, Exception exception);
    }
}
