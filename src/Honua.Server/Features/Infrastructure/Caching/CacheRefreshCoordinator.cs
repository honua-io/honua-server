// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Coordinates background cache refresh operations with per-key deduplication and bounded concurrency.
/// </summary>
/// <remarks>
/// Uses a channel-backed processing loop to decouple refresh requests from the request path.
/// Each unique cache key can only have one pending refresh at a time. Concurrent requests
/// for the same near-expiry key produce a single background refresh. Keys that recently
/// failed are temporarily backed off to avoid immediate retry storms on hot keys.
///
/// NOTE: This implementation uses local state only. For distributed coordination across
/// multiple instances, use DistributedCacheRefreshCoordinator instead.
/// </remarks>
[Obsolete("Use DistributedCacheRefreshCoordinator for production deployments to support horizontal scaling")]
internal sealed partial class CacheRefreshCoordinator : BackgroundService, ICacheRefreshCoordinator
{
    private const string MetricsCacheType = "background-refresh";
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(1);
    private readonly Channel<CacheRefreshItem> _channel;
    // Value semantics: 0 = pending, 1 = invalidated, 2 = write-claimed.
    // Embedding the state flag in _pendingKeys (instead of a separate dictionary)
    // eliminates the TOCTOU race that previously existed between ContainsKey and TryAdd
    // across two dictionaries. TryUpdate is atomic, so NotifyInvalidation either sees
    // the key and marks it, or the key was already claimed/cleaned up — no stale flag can leak.
    private readonly ConcurrentDictionary<string, byte> _pendingKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _retryAfterUtcTicks = new(StringComparer.Ordinal);
    private readonly CacheOptions _options;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<CacheRefreshCoordinator> _logger;
    private long _successCount;
    private long _failureCount;
    private long _skippedCount;

    public CacheRefreshCoordinator(
        IOptions<CacheOptions> options,
        IPerformanceMonitor performanceMonitor,
        ILogger<CacheRefreshCoordinator> logger)
    {
        _options = options.Value;
        _performanceMonitor = performanceMonitor;
        _logger = logger;

        // Bounded channel prevents unbounded memory growth if refresh callbacks are slow.
        // DropWrite rejects new items when full (instead of DropOldest which silently drops
        // items whose keys would leak in _pendingKeys). The TryWrite failure branch cleans up.
        _channel = Channel.CreateBounded<CacheRefreshItem>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <inheritdoc />
    public int QueueDepth => _pendingKeys.Count;

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

        // Atomically mark the key as invalidated regardless of current state.
        // 0 → 1: marks a pending refresh as invalidated (before write-back claimed it).
        // 2 → 1: marks a write-claimed refresh as invalidated (invalidation arrived
        //         after TryClaimWriteBack but before the write completed — the post-write
        //         check in the callback will detect this and remove the entry).
        // Fails silently if the key doesn't exist or is already 1.
        _pendingKeys.TryUpdate(key, 1, 0);
        _pendingKeys.TryUpdate(key, 1, 2);
    }

    /// <inheritdoc />
    public bool WasInvalidated(string key)
    {
        return _pendingKeys.TryGetValue(key, out byte val) && val == 1;
    }

    /// <inheritdoc />
    public bool TryClaimWriteBack(string key)
    {
        // Atomically transition 0 → 2 (write-claimed). Fails if the key was already
        // invalidated (1) or doesn't exist — in both cases the caller must skip the write.
        return _pendingKeys.TryUpdate(key, 2, 0);
    }

    /// <inheritdoc />
    public bool TryEnqueueRefresh(string key, Func<CancellationToken, Task> refreshCallback)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (IsWithinRetryBackoff(key, nowTicks))
        {
            return false;
        }

        // Per-key deduplication: only enqueue if this key isn't already pending
        if (!_pendingKeys.TryAdd(key, 0))
        {
            return false;
        }

        // Re-check after the pending claim to close the race with a recently failed
        // refresh that may have published its retry backoff concurrently.
        if (IsWithinRetryBackoff(key, DateTimeOffset.UtcNow.UtcTicks))
        {
            _pendingKeys.TryRemove(key, out _);
            return false;
        }

        // Try to write to channel; with DropWrite, this returns false when the channel is full
        if (!_channel.Writer.TryWrite(new CacheRefreshItem(key, refreshCallback)))
        {
            _pendingKeys.TryRemove(key, out _);
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

        Log.BackgroundRefreshStarted(_logger, _options.MaxConcurrentRefreshes);

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
                .WithTag("key_family", CacheKeyUtilities.GetCacheKeyFamily(item.Key));

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
            _pendingKeys.TryRemove(item.Key, out _);

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

    private sealed record CacheRefreshItem(string Key, Func<CancellationToken, Task> RefreshCallback);

    private static partial class Log
    {
        [LoggerMessage(1101, LogLevel.Information, "Background cache refresh started with max {MaxConcurrent} concurrent operations")]
        public static partial void BackgroundRefreshStarted(ILogger logger, int maxConcurrent);

        [LoggerMessage(1102, LogLevel.Information, "Background cache refresh disabled by configuration")]
        public static partial void BackgroundRefreshDisabled(ILogger logger);

        [LoggerMessage(1103, LogLevel.Warning, "Background refresh failed for key {CacheKey}")]
        public static partial void BackgroundRefreshFailed(ILogger logger, string cacheKey, Exception exception);

        [LoggerMessage(1104, LogLevel.Warning, "Background refresh timed out for key {CacheKey}")]
        public static partial void BackgroundRefreshTimeout(ILogger logger, string cacheKey);
    }
}
