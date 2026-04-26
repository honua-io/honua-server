// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Advanced query result caching with intelligent cache warming,
/// performance monitoring, and adaptive cache policies.
/// </summary>
public interface IQueryResultCacheManager
{
    /// <summary>
    /// Gets a cached query result or executes the query if not cached.
    /// </summary>
    /// <typeparam name="T">Type of the query result</typeparam>
    /// <param name="cacheKey">Cache key for the query</param>
    /// <param name="queryExecutor">Function to execute the query if not cached</param>
    /// <param name="options">Caching options</param>
    /// <returns>Query result</returns>
    Task<T> GetOrExecuteAsync<T>(
        string cacheKey,
        Func<Task<T>> queryExecutor,
        QueryCacheOptions? options = null);

    /// <summary>
    /// Gets a cached query result or executes the query with context tracking.
    /// </summary>
    /// <typeparam name="T">Type of the query result</typeparam>
    /// <param name="cacheKey">Cache key for the query</param>
    /// <param name="queryExecutor">Function to execute the query with context</param>
    /// <param name="context">Query execution context</param>
    /// <param name="options">Caching options</param>
    /// <returns>Query result</returns>
    Task<T> GetOrExecuteAsync<T>(
        string cacheKey,
        Func<QueryExecutionContext, Task<T>> queryExecutor,
        QueryExecutionContext context,
        QueryCacheOptions? options = null);

    /// <summary>
    /// Invalidates cache entries by pattern or tags.
    /// </summary>
    /// <param name="pattern">Pattern to match cache keys (supports wildcards)</param>
    /// <param name="tags">Tags to match for invalidation</param>
    /// <returns>Number of entries invalidated</returns>
    Task<int> InvalidateAsync(string? pattern = null, IEnumerable<string>? tags = null);

    /// <summary>
    /// Gets cache performance statistics.
    /// </summary>
    QueryCacheStatistics GetStatistics();

    /// <summary>
    /// Gets cache effectiveness metrics for optimization.
    /// </summary>
    CacheEffectivenessMetrics GetEffectivenessMetrics();
}

/// <summary>
/// Options for query result caching.
/// </summary>
public sealed class QueryCacheOptions
{
    /// <summary>
    /// Cache expiration time. If null, uses default from configuration.
    /// </summary>
    public TimeSpan? Expiration { get; set; }

    /// <summary>
    /// Sliding expiration time - resets expiration on cache hit.
    /// </summary>
    public TimeSpan? SlidingExpiration { get; set; }

    /// <summary>
    /// Priority for cache eviction.
    /// </summary>
    public CacheItemPriority Priority { get; set; } = CacheItemPriority.Normal;

    /// <summary>
    /// Tags for cache invalidation grouping.
    /// </summary>
    public IEnumerable<string>? Tags { get; set; }

    /// <summary>
    /// Maximum size of the cached result in bytes.
    /// </summary>
    public long? MaxSizeBytes { get; set; }

    /// <summary>
    /// Whether to compress the cached result.
    /// </summary>
    public bool Compress { get; set; } = true;

    /// <summary>
    /// Whether to cache null/empty results.
    /// </summary>
    public bool CacheEmptyResults { get; set; }

    /// <summary>
    /// Custom cache key suffix for versioning.
    /// </summary>
    public string? KeySuffix { get; set; }
}

/// <summary>
/// Configuration for query result caching.
/// </summary>
public sealed class QueryResultCacheOptions
{
    /// <summary>
    /// Whether generic query-result caching is enabled. Defaults off so deployments do not
    /// retain high-cardinality feature query results unless explicitly opted in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Default cache expiration time.
    /// </summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum cache size in bytes.
    /// </summary>
    public long MaxCacheSizeBytes { get; set; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Maximum number of cached items.
    /// </summary>
    public int MaxCachedItems { get; set; } = 10000;

    /// <summary>
    /// Whether to enable cache compression.
    /// </summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>
    /// Minimum size for compression in bytes.
    /// </summary>
    public int CompressionThresholdBytes { get; set; } = 1024;

    /// <summary>
    /// Whether to enable cache warming on startup.
    /// </summary>
    public bool EnableWarmup { get; set; } = true;

    /// <summary>
    /// Whether to track detailed cache metrics.
    /// </summary>
    public bool EnableDetailedMetrics { get; set; } = true;
}

/// <summary>
/// Production implementation of query result cache manager.
/// </summary>
internal sealed partial class QueryResultCacheManager : IQueryResultCacheManager, IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<QueryResultCacheManager> _logger;
    private readonly QueryResultCacheOptions _options;

    private readonly ConcurrentDictionary<string, CacheKeyStats> _keyStatistics = new();
    private readonly ConcurrentDictionary<string, QueryTypeStats> _queryTypeStatistics = new();
    private readonly Timer? _statisticsTimer;

    private long _totalRequests;
    private long _cacheHits;
    private long _cacheMisses;
    private long _totalTimeSavedMs;
    private long _totalExecutionTimeMs;
    private long _totalEvictions;
    private volatile bool _disposed;

    public QueryResultCacheManager(
        IMemoryCache cache,
        ILogger<QueryResultCacheManager> logger,
        IOptions<QueryResultCacheOptions> options)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.EnableDetailedMetrics)
        {
            _statisticsTimer = new Timer(UpdateStatistics, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    public async Task<T> GetOrExecuteAsync<T>(
        string cacheKey,
        Func<Task<T>> queryExecutor,
        QueryCacheOptions? options = null)
    {
        var context = new QueryExecutionContext
        {
            CorrelationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..8],
            QueryType = "Unknown"
        };

        return await GetOrExecuteAsync(cacheKey, _ => queryExecutor(), context, options);
    }

    public async Task<T> GetOrExecuteAsync<T>(
        string cacheKey,
        Func<QueryExecutionContext, Task<T>> queryExecutor,
        QueryExecutionContext context,
        QueryCacheOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(queryExecutor);
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
        {
            return await queryExecutor(context);
        }

        Interlocked.Increment(ref _totalRequests);

        var finalCacheKey = BuildFinalCacheKey(cacheKey, options?.KeySuffix);
        var stopwatch = Stopwatch.StartNew();

        // Try to get from cache
        if (_cache.TryGetValue(finalCacheKey, out var cachedResult))
        {
            stopwatch.Stop();
            RecordCacheHit(context.QueryType, stopwatch.Elapsed, finalCacheKey);

            CacheLog.CacheHit(_logger, finalCacheKey, stopwatch.ElapsedMilliseconds, context.CorrelationId);

            return (T)cachedResult!;
        }

        // Cache miss - execute query
        RecordCacheMiss(context.QueryType, finalCacheKey);

        var executionStopwatch = Stopwatch.StartNew();
        T result;

        try
        {
            result = await queryExecutor(context);
            executionStopwatch.Stop();

            RecordQueryExecution(context.QueryType, executionStopwatch.Elapsed);

            CacheLog.CacheMiss(_logger, finalCacheKey, executionStopwatch.ElapsedMilliseconds, context.CorrelationId);
        }
        catch (Exception ex)
        {
            executionStopwatch.Stop();
            CacheLog.QueryExecutionFailed(_logger, finalCacheKey, ex, context.CorrelationId);
            throw;
        }

        // Cache the result if appropriate
        if (ShouldCacheResult(result, options))
        {
            await CacheResultAsync(finalCacheKey, result, options, context);
        }

        stopwatch.Stop();
        return result;
    }

    public async Task<int> InvalidateAsync(string? pattern = null, IEnumerable<string>? tags = null)
    {
        if (_disposed) return 0;

        var invalidatedCount = 0;

        if (!string.IsNullOrEmpty(pattern))
        {
            var keysToRemove = _keyStatistics.Keys
                .Where(key => IsPatternMatch(key, pattern))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _cache.Remove(key);
                _keyStatistics.TryRemove(key, out _);
                invalidatedCount++;
            }

            CacheLog.CacheInvalidated(_logger, pattern, invalidatedCount);
        }

        await Task.CompletedTask;
        return invalidatedCount;
    }

    public QueryCacheStatistics GetStatistics()
    {
        if (_disposed) return new QueryCacheStatistics();

        var totalRequests = Interlocked.Read(ref _totalRequests);
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);

        var hitRatio = totalRequests > 0 ? (double)hits / totalRequests : 0;

        var byQueryType = _queryTypeStatistics.ToDictionary(
            kvp => kvp.Key,
            kvp => new QueryTypeCacheStatistics
            {
                Requests = kvp.Value.Requests,
                Hits = kvp.Value.Hits,
                AvgExecutionTimeMs = kvp.Value.TotalExecutionTime > 0 && kvp.Value.ExecutionCount > 0
                    ? (double)kvp.Value.TotalExecutionTime / kvp.Value.ExecutionCount
                    : 0
            });

        return new QueryCacheStatistics
        {
            TotalRequests = totalRequests,
            CacheHits = hits,
            CacheMisses = misses,
            HitRatio = hitRatio,
            TotalTimeSavedMs = Interlocked.Read(ref _totalTimeSavedMs),
            AvgMissExecutionTimeMs = misses > 0
                ? (double)Interlocked.Read(ref _totalExecutionTimeMs) / misses
                : 0,
            CurrentCacheSize = _keyStatistics.Count,
            ByQueryType = byQueryType
        };
    }

    public CacheEffectivenessMetrics GetEffectivenessMetrics()
    {
        if (_disposed) return new CacheEffectivenessMetrics();

        var keyMetrics = _keyStatistics.Values.Select(stats => new CacheKeyMetrics
        {
            Key = stats.Key,
            AccessCount = stats.AccessCount,
            HitRatio = stats.AccessCount > 0 ? (double)stats.Hits / stats.AccessCount : 0,
            MemoryBytes = stats.EstimatedSizeBytes,
            LastAccessed = stats.LastAccessed,
            AvgExecutionTimeMs = stats.ExecutionCount > 0
                ? (double)stats.TotalExecutionTime / stats.ExecutionCount
                : 0
        }).ToList();

        var topAccessed = keyMetrics.OrderByDescending(m => m.AccessCount).Take(20).ToList();
        var lowEfficiency = keyMetrics.Where(m => m.AccessCount > 5 && m.HitRatio < 0.3).Take(20).ToList();
        var largest = keyMetrics.OrderByDescending(m => m.MemoryBytes).Take(20).ToList();

        return new CacheEffectivenessMetrics
        {
            TopAccessedKeys = topAccessed,
            LowEfficiencyKeys = lowEfficiency,
            LargestItems = largest,
            EvictionMetrics = new CacheEvictionMetrics
            {
                TotalEvictions = Interlocked.Read(ref _totalEvictions),
                MemoryPressureEvictions = 0, // Would need integration with MemoryCache internals
                ExpirationEvictions = 0,
                ManualEvictions = 0
            }
        };
    }

    private void RecordCacheHit(string queryType, TimeSpan lookupTime, string cacheKey)
    {
        Interlocked.Increment(ref _cacheHits);
        Interlocked.Add(ref _totalTimeSavedMs, (long)lookupTime.TotalMilliseconds);

        UpdateQueryTypeStats(queryType, true);
        UpdateKeyStats(cacheKey, true, 0, 0);
    }

    private void RecordCacheMiss(string queryType, string cacheKey)
    {
        Interlocked.Increment(ref _cacheMisses);
        UpdateQueryTypeStats(queryType, false);
        UpdateKeyStats(cacheKey, false, 0, 0);
    }

    private void RecordQueryExecution(string queryType, TimeSpan executionTime)
    {
        var executionMs = (long)executionTime.TotalMilliseconds;
        Interlocked.Add(ref _totalExecutionTimeMs, executionMs);

        if (_queryTypeStatistics.TryGetValue(queryType, out var stats))
        {
            Interlocked.Add(ref stats.TotalExecutionTime, executionMs);
            Interlocked.Increment(ref stats.ExecutionCount);
        }
    }

    private void UpdateQueryTypeStats(string queryType, bool hit)
    {
        var stats = _queryTypeStatistics.GetOrAdd(queryType, _ => new QueryTypeStats());
        Interlocked.Increment(ref stats.Requests);
        if (hit)
        {
            Interlocked.Increment(ref stats.Hits);
        }
    }

    private void UpdateKeyStats(string cacheKey, bool hit, long executionTimeMs, long estimatedSizeBytes)
    {
        var stats = _keyStatistics.GetOrAdd(cacheKey, key => new CacheKeyStats { Key = key });

        Interlocked.Increment(ref stats.AccessCount);
        if (hit)
        {
            Interlocked.Increment(ref stats.Hits);
        }
        else if (executionTimeMs > 0)
        {
            Interlocked.Add(ref stats.TotalExecutionTime, executionTimeMs);
            Interlocked.Increment(ref stats.ExecutionCount);
        }

        if (estimatedSizeBytes > 0)
        {
            Interlocked.Exchange(ref stats.EstimatedSizeBytes, estimatedSizeBytes);
        }

        stats.LastAccessed = DateTime.UtcNow;
    }

    private async Task CacheResultAsync<T>(string cacheKey, T result, QueryCacheOptions? options, QueryExecutionContext context)
    {
        try
        {
            var cacheOptions = CreateMemoryCacheEntryOptions(options);
            var hasEstimatedSize = TryEstimateObjectSize(result, out var estimatedSize);

            if (!hasEstimatedSize && options?.MaxSizeBytes.HasValue == true)
            {
                CacheLog.ResultSizeUnknown(_logger, cacheKey, options.MaxSizeBytes.Value);
                return;
            }

            if (options?.MaxSizeBytes.HasValue == true && estimatedSize > options.MaxSizeBytes.Value)
            {
                CacheLog.ResultTooLarge(_logger, cacheKey, estimatedSize, options.MaxSizeBytes.Value);
                return;
            }

            _cache.Set(cacheKey, result, cacheOptions);

            UpdateKeyStats(cacheKey, false, 0, estimatedSize);

            CacheLog.ResultCached(_logger, cacheKey, estimatedSize, context.CorrelationId);
        }
        catch (Exception ex)
        {
            CacheLog.CachingFailed(_logger, cacheKey, ex, context.CorrelationId);
        }

        await Task.CompletedTask;
    }

    private static bool ShouldCacheResult<T>(T result, QueryCacheOptions? options)
    {
        if (result == null)
            return options?.CacheEmptyResults == true;

        if (result is IEnumerable<object> enumerable)
        {
            return enumerable.Any() || options?.CacheEmptyResults == true;
        }

        return true;
    }

    private MemoryCacheEntryOptions CreateMemoryCacheEntryOptions(QueryCacheOptions? options)
    {
        var cacheOptions = new MemoryCacheEntryOptions
        {
            Priority = options?.Priority ?? CacheItemPriority.Normal,
            AbsoluteExpirationRelativeToNow = options?.Expiration ?? _options.DefaultExpiration
        };

        if (options?.SlidingExpiration.HasValue == true)
        {
            cacheOptions.SlidingExpiration = options.SlidingExpiration;
        }

        // Add eviction callback to track statistics
        cacheOptions.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            Interlocked.Increment(ref _totalEvictions);
            if (key is string keyString && _keyStatistics.TryRemove(keyString, out _))
            {
                CacheLog.ItemEvicted(_logger, keyString, reason);
            }
        });

        return cacheOptions;
    }

    private static string BuildFinalCacheKey(string baseKey, string? suffix)
    {
        if (string.IsNullOrEmpty(suffix))
            return baseKey;

        return $"{baseKey}:{suffix}";
    }

    private static bool TryEstimateObjectSize<T>(T obj, out long sizeBytes)
    {
        sizeBytes = 0;
        if (obj == null)
        {
            return true;
        }

        switch (obj)
        {
            case string text:
                sizeBytes = Encoding.UTF8.GetByteCount(text);
                return true;
            case byte[] bytes:
                sizeBytes = bytes.LongLength;
                return true;
            case CachedResponse response:
                sizeBytes = response.Payload.LongLength +
                    Encoding.UTF8.GetByteCount(response.ContentType) +
                    (response.ETag == null ? 0 : Encoding.UTF8.GetByteCount(response.ETag));
                return true;
            case JsonElement element:
                sizeBytes = Encoding.UTF8.GetByteCount(element.GetRawText());
                return true;
            case bool:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
            case DateTime:
            case DateTimeOffset:
            case TimeSpan:
                sizeBytes = Encoding.UTF8.GetByteCount(FormattableString.Invariant($"{obj}"));
                return true;
            default:
                return false;
        }
    }

    private static bool IsPatternMatch(string value, string pattern)
    {
        // Simple wildcard matching - in production use a proper pattern matcher
        if (pattern.EndsWith('*'))
        {
            return value.StartsWith(pattern[..^1]);
        }
        if (pattern.StartsWith('*'))
        {
            return value.EndsWith(pattern[1..]);
        }
        return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatistics(object? state)
    {
        if (_disposed) return;

        try
        {
            // Clean up old statistics entries
            var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(1));
            var expiredKeys = _keyStatistics
                .Where(kvp => kvp.Value.LastAccessed < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _keyStatistics.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            CacheLog.StatisticsUpdateFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _statisticsTimer?.Dispose();
        _keyStatistics.Clear();
        _queryTypeStatistics.Clear();
    }

    private sealed class CacheKeyStats
    {
        public string Key = string.Empty;
        public long AccessCount;
        public long Hits;
        public long TotalExecutionTime;
        public long ExecutionCount;
        public long EstimatedSizeBytes;
        public DateTime LastAccessed = DateTime.UtcNow;
    }

    private sealed class QueryTypeStats
    {
        public long Requests;
        public long Hits;
        public long TotalExecutionTime;
        public long ExecutionCount;
    }

    private static partial class CacheLog
    {
        [LoggerMessage(
            EventId = 9301,
            Level = LogLevel.Debug,
            Message = "Cache hit: {CacheKey} (lookup: {LookupMs}ms) [CorrelationId={CorrelationId}]")]
        public static partial void CacheHit(ILogger logger, string cacheKey, long lookupMs, string correlationId);

        [LoggerMessage(
            EventId = 9302,
            Level = LogLevel.Debug,
            Message = "Cache miss: {CacheKey} (execution: {ExecutionMs}ms) [CorrelationId={CorrelationId}]")]
        public static partial void CacheMiss(ILogger logger, string cacheKey, long executionMs, string correlationId);

        [LoggerMessage(
            EventId = 9303,
            Level = LogLevel.Warning,
            Message = "Query execution failed for cache key: {CacheKey} [CorrelationId={CorrelationId}]")]
        public static partial void QueryExecutionFailed(ILogger logger, string cacheKey, Exception exception, string correlationId);

        [LoggerMessage(
            EventId = 9304,
            Level = LogLevel.Information,
            Message = "Cache invalidated: {Pattern} ({Count} entries)")]
        public static partial void CacheInvalidated(ILogger logger, string pattern, int count);

        [LoggerMessage(
            EventId = 9307,
            Level = LogLevel.Debug,
            Message = "Result cached: {CacheKey} ({SizeBytes} bytes) [CorrelationId={CorrelationId}]")]
        public static partial void ResultCached(ILogger logger, string cacheKey, long sizeBytes, string correlationId);

        [LoggerMessage(
            EventId = 9308,
            Level = LogLevel.Warning,
            Message = "Result too large to cache: {CacheKey} ({SizeBytes} bytes > {MaxSizeBytes} limit)")]
        public static partial void ResultTooLarge(ILogger logger, string cacheKey, long sizeBytes, long maxSizeBytes);

        [LoggerMessage(
            EventId = 9312,
            Level = LogLevel.Warning,
            Message = "Result size could not be estimated for cache key: {CacheKey}; skipping cache entry with {MaxSizeBytes} byte limit")]
        public static partial void ResultSizeUnknown(ILogger logger, string cacheKey, long maxSizeBytes);

        [LoggerMessage(
            EventId = 9309,
            Level = LogLevel.Warning,
            Message = "Failed to cache result: {CacheKey} [CorrelationId={CorrelationId}]")]
        public static partial void CachingFailed(ILogger logger, string cacheKey, Exception exception, string correlationId);

        [LoggerMessage(
            EventId = 9310,
            Level = LogLevel.Debug,
            Message = "Cache item evicted: {CacheKey} (reason: {Reason})")]
        public static partial void ItemEvicted(ILogger logger, string cacheKey, EvictionReason reason);

        [LoggerMessage(
            EventId = 9311,
            Level = LogLevel.Warning,
            Message = "Failed to update cache statistics")]
        public static partial void StatisticsUpdateFailed(ILogger logger, Exception exception);
    }
}
