// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Infrastructure.Caching;

/// <summary>
/// High-performance logging for query cache operations using source generators
/// </summary>
/// <remarks>
/// Uses compile-time source generation for optimal logging performance in hot paths.
/// These log messages are designed for monitoring cache effectiveness and debugging
/// performance issues without impacting application throughput.
/// </remarks>
internal static partial class QueryCachePerformanceLog
{
    /// <summary>
    /// Logs successful cache hit with performance context
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Query cache HIT: {StatementHash} (hit #{HitCount}, saved {ParseTimeMs}ms parsing)")]
    public static partial void LogCacheHit(
        this ILogger logger,
        string statementHash,
        int hitCount,
        double parseTimeMs);

    /// <summary>
    /// Logs cache miss with execution count context
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Query cache MISS: {StatementHash} (execution #{ExecutionCount}, threshold: {Threshold})")]
    public static partial void LogCacheMiss(
        this ILogger logger,
        string statementHash,
        int executionCount,
        int threshold);

    /// <summary>
    /// Logs successful statement preparation with timing
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Prepared statement created: {StatementName} in {PrepareTimeMs}ms")]
    public static partial void LogStatementPrepared(
        this ILogger logger,
        string statementName,
        double prepareTimeMs);

    /// <summary>
    /// Logs cache eviction for memory management
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Cache eviction: {StatementName} (age: {AgeMinutes}min, hits: {HitCount})")]
    public static partial void LogCacheEviction(
        this ILogger logger,
        string statementName,
        double ageMinutes,
        int hitCount);

    /// <summary>
    /// Logs periodic cache statistics for monitoring
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Query cache stats: {PreparedCount} prepared, {HitRatio:P1} hit ratio ({Hits}/{Total} requests)")]
    public static partial void LogCacheStatistics(
        this ILogger logger,
        int preparedCount,
        double hitRatio,
        int hits,
        int total);

    /// <summary>
    /// Logs cache cleanup operations
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Cache cleanup: removed {ExpiredCount} expired statements, {MetricsCount} old metrics")]
    public static partial void LogCacheCleanup(
        this ILogger logger,
        int expiredCount,
        int metricsCount);

    /// <summary>
    /// Logs errors in cache operations (non-blocking)
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Query cache error for {StatementHash}: {ErrorMessage} (falling back to regular execution)")]
    public static partial void LogCacheError(
        this ILogger logger,
        Exception ex,
        string statementHash,
        string errorMessage);

    /// <summary>
    /// Logs cache capacity warnings
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Query cache at {UsagePercentage:P0} capacity ({Used}/{Max} statements). Consider tuning MaxCachedStatements.")]
    public static partial void LogCacheCapacityWarning(
        this ILogger logger,
        double usagePercentage,
        int used,
        int max);

    /// <summary>
    /// Logs connection cache clearing
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Cleared {Count} cached statements for connection {ConnectionId}")]
    public static partial void LogConnectionCacheCleared(
        this ILogger logger,
        int count,
        string connectionId);
}
