namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// High-performance logging for monitored cache operations using source generation for AOT compatibility.
/// </summary>
internal static partial class MonitoredCacheLog
{
    #region Cache Get Operations (8000-8099)

    /// <summary>
    /// Logs when a cache get operation completes.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="key">Cache key</param>
    /// <param name="isHit">Whether the operation was a cache hit</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 8001,
        Level = LogLevel.Debug,
        Message = "Cache get {CacheType}[{Key}]: {IsHit} in {ElapsedMs:F2}ms")]
    public static partial void CacheGetCompleted(ILogger logger, string cacheType, string key, bool isHit, double elapsedMs);

    /// <summary>
    /// Logs when a cache get-or-create operation results in a hit.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="key">Cache key</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 8002,
        Level = LogLevel.Debug,
        Message = "Cache get-or-create HIT {CacheType}[{Key}] in {ElapsedMs:F2}ms")]
    public static partial void CacheGetOrCreateHit(ILogger logger, string cacheType, string key, double elapsedMs);

    /// <summary>
    /// Logs when a cache get-or-create operation results in a miss and factory execution.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="key">Cache key</param>
    /// <param name="factoryMs">Factory execution time in milliseconds</param>
    /// <param name="totalMs">Total operation time in milliseconds</param>
    [LoggerMessage(
        EventId = 8003,
        Level = LogLevel.Debug,
        Message = "Cache get-or-create MISS {CacheType}[{Key}]: factory={FactoryMs:F2}ms, total={TotalMs:F2}ms")]
    public static partial void CacheGetOrCreateMiss(ILogger logger, string cacheType, string key, double factoryMs, double totalMs);

    #endregion

    #region Cache Set Operations (8100-8199)

    /// <summary>
    /// Logs when a cache set operation completes.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="key">Cache key</param>
    /// <param name="expirationSeconds">Expiration time in seconds</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 8101,
        Level = LogLevel.Debug,
        Message = "Cache set {CacheType}[{Key}]: expires in {ExpirationSeconds:F0}s, took {ElapsedMs:F2}ms")]
    public static partial void CacheSetCompleted(ILogger logger, string cacheType, string key, double expirationSeconds, double elapsedMs);

    #endregion

    #region Cache Remove Operations (8200-8299)

    /// <summary>
    /// Logs when a cache remove operation completes.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="key">Cache key</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 8201,
        Level = LogLevel.Debug,
        Message = "Cache remove {CacheType}[{Key}] in {ElapsedMs:F2}ms")]
    public static partial void CacheRemoveCompleted(ILogger logger, string cacheType, string key, double elapsedMs);

    /// <summary>
    /// Logs when a cache remove by pattern operation completes.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="pattern">Cache key pattern</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 8202,
        Level = LogLevel.Debug,
        Message = "Cache remove pattern {CacheType}[{Pattern}] in {ElapsedMs:F2}ms")]
    public static partial void CacheRemovePatternCompleted(ILogger logger, string cacheType, string pattern, double elapsedMs);

    #endregion

    #region Cache Errors (8300-8399)

    /// <summary>
    /// Logs when a cache operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="operation">Cache operation that failed</param>
    /// <param name="key">Cache key or pattern</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 8301,
        Level = LogLevel.Error,
        Message = "Cache {Operation} failed for key '{Key}': {ErrorMessage}")]
    public static partial void CacheOperationFailed(ILogger logger, string operation, string key, string errorMessage, Exception exception);

    #endregion

    #region Cache Analytics (8400-8499)

    /// <summary>
    /// Logs cache hit ratio statistics.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="hitRatio">Hit ratio percentage</param>
    /// <param name="totalOperations">Total cache operations</param>
    /// <param name="hits">Number of cache hits</param>
    /// <param name="misses">Number of cache misses</param>
    [LoggerMessage(
        EventId = 8401,
        Level = LogLevel.Information,
        Message = "Cache analytics {CacheType}: {HitRatio:F1}% hit ratio ({Hits} hits, {Misses} misses, {TotalOperations} total)")]
    public static partial void CacheAnalytics(ILogger logger, string cacheType, double hitRatio, long totalOperations, long hits, long misses);

    /// <summary>
    /// Logs when cache effectiveness is below optimal thresholds.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="hitRatio">Current hit ratio percentage</param>
    /// <param name="threshold">Expected hit ratio threshold</param>
    /// <param name="recommendation">Recommendation for improvement</param>
    [LoggerMessage(
        EventId = 8402,
        Level = LogLevel.Warning,
        Message = "Cache effectiveness below threshold for {CacheType}: {HitRatio:F1}% (threshold: {Threshold:F1}%). Recommendation: {Recommendation}")]
    public static partial void LowCacheEffectiveness(ILogger logger, string cacheType, double hitRatio, double threshold, string recommendation);

    /// <summary>
    /// Logs cache storage utilization metrics.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="entryCount">Number of cached entries</param>
    /// <param name="avgEntrySize">Average entry size in bytes</param>
    /// <param name="totalSize">Total cache size in bytes</param>
    [LoggerMessage(
        EventId = 8403,
        Level = LogLevel.Debug,
        Message = "Cache storage {CacheType}: {EntryCount} entries, {AvgEntrySize:F0} bytes avg, {TotalSize:N0} bytes total")]
    public static partial void CacheStorageMetrics(ILogger logger, string cacheType, long entryCount, double avgEntrySize, long totalSize);

    #endregion

    #region Cache Performance Alerts (8500-8599)

    /// <summary>
    /// Logs when cache operation performance degrades.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="operation">Cache operation</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="elapsedMs">Operation time in milliseconds</param>
    /// <param name="thresholdMs">Performance threshold in milliseconds</param>
    [LoggerMessage(
        EventId = 8501,
        Level = LogLevel.Warning,
        Message = "Slow cache {Operation} for {CacheType}: {ElapsedMs:F2}ms (threshold: {ThresholdMs:F0}ms)")]
    public static partial void SlowCacheOperation(ILogger logger, string operation, string cacheType, double elapsedMs, double thresholdMs);

    /// <summary>
    /// Logs when cache memory usage is high.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="memoryUsageMB">Current memory usage in MB</param>
    /// <param name="limitMB">Memory limit in MB</param>
    /// <param name="utilizationPercent">Memory utilization percentage</param>
    [LoggerMessage(
        EventId = 8502,
        Level = LogLevel.Warning,
        Message = "High cache memory usage for {CacheType}: {MemoryUsageMB:F0}MB/{LimitMB:F0}MB ({UtilizationPercent:F1}%)")]
    public static partial void HighCacheMemoryUsage(ILogger logger, string cacheType, double memoryUsageMB, double limitMB, double utilizationPercent);

    #endregion
}