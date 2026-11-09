// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Infrastructure.Monitoring;

internal static partial class MonitoredCacheLog
{
    #region Cache Get Operations (8000-8099)

    [LoggerMessage(
        EventId = 8001,
        Level = LogLevel.Debug,
        Message = "Cache get {CacheType}[{Key}]: {IsHit} in {ElapsedMs:F2}ms")]
    public static partial void CacheGetCompleted(ILogger logger, string cacheType, string key, bool isHit, double elapsedMs);

    [LoggerMessage(
        EventId = 8002,
        Level = LogLevel.Debug,
        Message = "Cache get-or-create HIT {CacheType}[{Key}] in {ElapsedMs:F2}ms")]
    public static partial void CacheGetOrCreateHit(ILogger logger, string cacheType, string key, double elapsedMs);

    [LoggerMessage(
        EventId = 8003,
        Level = LogLevel.Debug,
        Message = "Cache get-or-create MISS {CacheType}[{Key}]: factory={FactoryMs:F2}ms, total={TotalMs:F2}ms")]
    public static partial void CacheGetOrCreateMiss(ILogger logger, string cacheType, string key, double factoryMs, double totalMs);

    #endregion

    #region Cache Set Operations (8100-8199)

    [LoggerMessage(
        EventId = 8101,
        Level = LogLevel.Debug,
        Message = "Cache set {CacheType}[{Key}]: expires in {ExpirationSeconds:F0}s, took {ElapsedMs:F2}ms")]
    public static partial void CacheSetCompleted(ILogger logger, string cacheType, string key, double expirationSeconds, double elapsedMs);

    #endregion

    #region Cache Remove Operations (8200-8299)

    [LoggerMessage(
        EventId = 8201,
        Level = LogLevel.Debug,
        Message = "Cache remove {CacheType}[{Key}] in {ElapsedMs:F2}ms")]
    public static partial void CacheRemoveCompleted(ILogger logger, string cacheType, string key, double elapsedMs);

    [LoggerMessage(
        EventId = 8202,
        Level = LogLevel.Debug,
        Message = "Cache remove pattern {CacheType}[{Pattern}] in {ElapsedMs:F2}ms")]
    public static partial void CacheRemovePatternCompleted(ILogger logger, string cacheType, string pattern, double elapsedMs);

    #endregion

    #region Cache Errors (8300-8399)

    [LoggerMessage(
        EventId = 8301,
        Level = LogLevel.Error,
        Message = "Cache {Operation} failed for key '{Key}': {ErrorMessage}")]
    public static partial void CacheOperationFailed(ILogger logger, string operation, string key, string errorMessage, Exception exception);

    #endregion

    #region Cache Analytics (8400-8499)

    [LoggerMessage(
        EventId = 8401,
        Level = LogLevel.Information,
        Message = "Cache analytics {CacheType}: {HitRatio:F1}% hit ratio ({Hits} hits, {Misses} misses, {TotalOperations} total)")]
    public static partial void CacheAnalytics(ILogger logger, string cacheType, double hitRatio, long totalOperations, long hits, long misses);

    [LoggerMessage(
        EventId = 8402,
        Level = LogLevel.Warning,
        Message = "Cache effectiveness below threshold for {CacheType}: {HitRatio:F1}% (threshold: {Threshold:F1}%). Recommendation: {Recommendation}")]
    public static partial void LowCacheEffectiveness(ILogger logger, string cacheType, double hitRatio, double threshold, string recommendation);

    [LoggerMessage(
        EventId = 8403,
        Level = LogLevel.Debug,
        Message = "Cache storage {CacheType}: {EntryCount} entries, {AvgEntrySize:F0} bytes avg, {TotalSize:N0} bytes total")]
    public static partial void CacheStorageMetrics(ILogger logger, string cacheType, long entryCount, double avgEntrySize, long totalSize);

    #endregion

    #region Cache Performance Alerts (8500-8599)

    [LoggerMessage(
        EventId = 8501,
        Level = LogLevel.Warning,
        Message = "Slow cache {Operation} for {CacheType}: {ElapsedMs:F2}ms (threshold: {ThresholdMs:F0}ms)")]
    public static partial void SlowCacheOperation(ILogger logger, string operation, string cacheType, double elapsedMs, double thresholdMs);

    [LoggerMessage(
        EventId = 8502,
        Level = LogLevel.Warning,
        Message = "High cache memory usage for {CacheType}: {MemoryUsageMB:F0}MB/{LimitMB:F0}MB ({UtilizationPercent:F1}%)")]
    public static partial void HighCacheMemoryUsage(ILogger logger, string cacheType, double memoryUsageMB, double limitMB, double utilizationPercent);

    #endregion
}
