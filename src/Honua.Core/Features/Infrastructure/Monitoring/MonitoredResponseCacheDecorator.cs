using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Caching;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Decorator for IResponseCache that adds comprehensive performance monitoring and cache metrics tracking.
/// </summary>
/// <remarks>
/// This decorator wraps any IResponseCache implementation to provide detailed cache performance metrics
/// including hit/miss ratios, operation timing, cache effectiveness, and storage analytics.
/// </remarks>
internal sealed class MonitoredResponseCacheDecorator : IResponseCache
{
    private readonly IResponseCache _innerCache;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<MonitoredResponseCacheDecorator> _logger;

    public MonitoredResponseCacheDecorator(
        IResponseCache innerCache,
        IPerformanceMonitor performanceMonitor,
        ILogger<MonitoredResponseCacheDecorator> logger)
    {
        _innerCache = innerCache ?? throw new ArgumentNullException(nameof(innerCache));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        using var scope = _performanceMonitor.StartOperation("cache_get")
            .WithTag("cache_type", GetCacheType(key))
            .WithTag("operation", "get");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerCache.GetAsync<T>(key, cancellationToken);

            var isHit = result != null;
            var cacheType = GetCacheType(key);

            // Record hit/miss metrics
            _performanceMonitor.RecordCacheMetrics(cacheType, isHit ? "hit" : "miss");

            // Record operation timing
            scope.WithTag("result", isHit ? "hit" : "miss");

            MonitoredCacheLog.CacheGetCompleted(_logger, cacheType, key, isHit, stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredCacheLog.CacheOperationFailed(_logger, "get", key, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
    {
        using var scope = _performanceMonitor.StartOperation("cache_set")
            .WithTag("cache_type", GetCacheType(key))
            .WithTag("operation", "set")
            .WithTag("expiration_seconds", expiration.TotalSeconds.ToString());

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _innerCache.SetAsync(key, value, expiration, cancellationToken);

            var cacheType = GetCacheType(key);

            // Record set operation
            _performanceMonitor.RecordCacheMetrics(cacheType, "set");

            MonitoredCacheLog.CacheSetCompleted(_logger, cacheType, key, expiration.TotalSeconds, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredCacheLog.CacheOperationFailed(_logger, "set", key, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
    {
        using var scope = _performanceMonitor.StartOperation("cache_get_or_create")
            .WithTag("cache_type", GetCacheType(key))
            .WithTag("operation", "get_or_create");

        var stopwatch = Stopwatch.StartNew();
        var factoryExecuted = false;

        try
        {
            // First, try to get from cache
            var cached = await _innerCache.GetAsync<T>(key, cancellationToken);
            var cacheType = GetCacheType(key);

            if (cached != null)
            {
                // Cache hit
                _performanceMonitor.RecordCacheMetrics(cacheType, "hit");
                scope.WithTag("result", "hit");

                MonitoredCacheLog.CacheGetOrCreateHit(_logger, cacheType, key, stopwatch.Elapsed.TotalMilliseconds);
                return cached;
            }

            // Cache miss - execute factory
            factoryExecuted = true;
            var factoryStopwatch = Stopwatch.StartNew();

            var result = await factory();

            factoryStopwatch.Stop();

            // Set in cache
            await _innerCache.SetAsync(key, result, expiration, cancellationToken);

            _performanceMonitor.RecordCacheMetrics(cacheType, "miss");
            scope.WithTag("result", "miss")
                 .WithTag("factory_time_ms", factoryStopwatch.Elapsed.TotalMilliseconds.ToString());

            MonitoredCacheLog.CacheGetOrCreateMiss(_logger,
                cacheType, key,
                factoryStopwatch.Elapsed.TotalMilliseconds,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name)
                 .WithTag("factory_executed", factoryExecuted.ToString());

            MonitoredCacheLog.CacheOperationFailed(_logger, "get_or_create", key, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("cache_remove")
            .WithTag("cache_type", GetCacheType(key))
            .WithTag("operation", "remove");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _innerCache.RemoveAsync(key, cancellationToken);

            var cacheType = GetCacheType(key);

            // Record eviction
            _performanceMonitor.RecordCacheMetrics(cacheType, "eviction");

            MonitoredCacheLog.CacheRemoveCompleted(_logger, cacheType, key, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredCacheLog.CacheOperationFailed(_logger, "remove", key, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("cache_remove_pattern")
            .WithTag("cache_type", GetCacheType(pattern))
            .WithTag("operation", "remove_pattern");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _innerCache.RemoveByPatternAsync(pattern, cancellationToken);

            var cacheType = GetCacheType(pattern);

            // Record pattern eviction
            _performanceMonitor.RecordCacheMetrics(cacheType, "pattern_eviction");

            MonitoredCacheLog.CacheRemovePatternCompleted(_logger, cacheType, pattern, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredCacheLog.CacheOperationFailed(_logger, "remove_pattern", pattern, ex.Message, ex);
            throw;
        }
    }

    /// <summary>
    /// Determines cache type from cache key for metrics categorization.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <returns>Cache type category</returns>
    private static string GetCacheType(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "unknown";

        // Extract cache type from key patterns
        var lowerKey = key.ToLowerInvariant();

        return lowerKey switch
        {
            _ when lowerKey.Contains("layer") => "layer_metadata",
            _ when lowerKey.Contains("query") => "query_results",
            _ when lowerKey.Contains("tile") || lowerKey.Contains("mvt") => "tile_data",
            _ when lowerKey.Contains("feature") => "feature_data",
            _ when lowerKey.Contains("catalog") => "catalog_data",
            _ when lowerKey.Contains("auth") => "auth_data",
            _ when lowerKey.Contains("config") => "config_data",
            _ => "general"
        };
    }
}