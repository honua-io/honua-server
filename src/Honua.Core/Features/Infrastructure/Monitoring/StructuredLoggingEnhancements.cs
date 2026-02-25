// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Provides enhanced structured logging capabilities for production observability.
/// </summary>
/// <remarks>
/// This class enriches log entries with contextual information that aids in production debugging
/// and troubleshooting, particularly for geospatial operations.
/// </remarks>
public static partial class StructuredLoggingEnhancements
{
    /// <summary>
    /// Creates a logging scope with spatial query context.
    /// </summary>
    /// <param name="logger">The logger to create scope for</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="spatialFilter">Spatial filter type</param>
    /// <param name="boundingBox">Query bounding box</param>
    /// <param name="limit">Query limit</param>
    /// <param name="protocol">Request protocol (WFS, OGC API, etc.)</param>
    /// <returns>Disposable scope for structured logging</returns>
    public static IDisposable BeginSpatialQueryScope(
        this ILogger logger,
        int layerId,
        string? spatialFilter = null,
        string? boundingBox = null,
        int? limit = null,
        string? protocol = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["layer_id"] = layerId,
            ["spatial_filter"] = spatialFilter,
            ["bounding_box"] = boundingBox,
            ["query_limit"] = limit,
            ["protocol"] = protocol,
            ["request_id"] = Activity.Current?.Id,
            ["trace_id"] = Activity.Current?.TraceId.ToString(),
            ["span_id"] = Activity.Current?.SpanId.ToString()
        };

        // Remove null values
        var filteredContext = context.Where(kvp => kvp.Value != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return logger.BeginScope(filteredContext)!;
    }

    /// <summary>
    /// Creates a logging scope with cache operation context.
    /// </summary>
    /// <param name="logger">The logger to create scope for</param>
    /// <param name="cacheType">Type of cache</param>
    /// <param name="operation">Cache operation</param>
    /// <param name="key">Cache key</param>
    /// <param name="ttl">Time to live</param>
    /// <returns>Disposable scope for structured logging</returns>
    public static IDisposable BeginCacheOperationScope(
        this ILogger logger,
        string cacheType,
        string operation,
        string? key = null,
        TimeSpan? ttl = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["cache_type"] = cacheType,
            ["cache_operation"] = operation,
            ["cache_key"] = key,
            ["cache_ttl_seconds"] = ttl?.TotalSeconds,
            ["request_id"] = Activity.Current?.Id
        };

        var filteredContext = context.Where(kvp => kvp.Value != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return logger.BeginScope(filteredContext)!;
    }

    /// <summary>
    /// Creates a logging scope with coordinate transformation context.
    /// </summary>
    /// <param name="logger">The logger to create scope for</param>
    /// <param name="fromSrid">Source spatial reference system</param>
    /// <param name="toSrid">Target spatial reference system</param>
    /// <param name="coordinateCount">Number of coordinates being transformed</param>
    /// <param name="operationType">Type of transformation operation</param>
    /// <returns>Disposable scope for structured logging</returns>
    public static IDisposable BeginCoordinateTransformScope(
        this ILogger logger,
        int fromSrid,
        int toSrid,
        int coordinateCount,
        string operationType = "transform")
    {
        var context = new Dictionary<string, object?>
        {
            ["transform_from_srid"] = fromSrid,
            ["transform_to_srid"] = toSrid,
            ["coordinate_count"] = coordinateCount,
            ["transform_operation"] = operationType,
            ["request_id"] = Activity.Current?.Id
        };

        return logger.BeginScope(context)!;
    }

    /// <summary>
    /// Creates a logging scope with memory pressure context.
    /// </summary>
    /// <param name="logger">The logger to create scope for</param>
    /// <param name="pressurePercent">Memory pressure percentage</param>
    /// <param name="allocatedMB">Allocated memory in MB</param>
    /// <param name="gcCounts">Garbage collection counts by generation</param>
    /// <returns>Disposable scope for structured logging</returns>
    public static IDisposable BeginMemoryPressureScope(
        this ILogger logger,
        double pressurePercent,
        long allocatedMB,
        (int Gen0, int Gen1, int Gen2)? gcCounts = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["memory_pressure_percent"] = pressurePercent,
            ["memory_allocated_mb"] = allocatedMB,
            ["gc_gen0_count"] = gcCounts?.Gen0,
            ["gc_gen1_count"] = gcCounts?.Gen1,
            ["gc_gen2_count"] = gcCounts?.Gen2,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        var filteredContext = context.Where(kvp => kvp.Value != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return logger.BeginScope(filteredContext)!;
    }

    /// <summary>
    /// Creates a logging scope with database operation context.
    /// </summary>
    /// <param name="logger">The logger to create scope for</param>
    /// <param name="queryType">Type of database query</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="expectedRows">Expected number of rows</param>
    /// <param name="timeout">Query timeout</param>
    /// <returns>Disposable scope for structured logging</returns>
    public static IDisposable BeginDatabaseOperationScope(
        this ILogger logger,
        string queryType,
        int? layerId = null,
        int? expectedRows = null,
        TimeSpan? timeout = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["db_query_type"] = queryType,
            ["layer_id"] = layerId,
            ["expected_rows"] = expectedRows,
            ["query_timeout_seconds"] = timeout?.TotalSeconds,
            ["request_id"] = Activity.Current?.Id
        };

        var filteredContext = context.Where(kvp => kvp.Value != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return logger.BeginScope(filteredContext)!;
    }

    /// <summary>
    /// Logs performance metrics with structured context.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="operationType">Type of operation</param>
    /// <param name="duration">Operation duration</param>
    /// <param name="additionalContext">Additional context information</param>
    public static void LogPerformanceMetric(
        this ILogger logger,
        string operationType,
        TimeSpan duration,
        IDictionary<string, object>? additionalContext = null)
    {
        var context = new Dictionary<string, object>
        {
            ["operation_type"] = operationType,
            ["duration_ms"] = duration.TotalMilliseconds,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        if (additionalContext != null)
        {
            foreach (var kvp in additionalContext)
            {
                context[kvp.Key] = kvp.Value;
            }
        }

        using var scope = logger.BeginScope(context);

        if (duration.TotalMilliseconds > 1000)
        {
            StructuredLoggingLog.SlowOperation(logger, operationType, duration.TotalMilliseconds);
        }
        else
        {
            StructuredLoggingLog.OperationCompleted(logger, operationType, duration.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Logs an error with enhanced contextual information.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="exception">The exception that occurred</param>
    /// <param name="operationType">Type of operation that failed</param>
    /// <param name="context">Additional context information</param>
    public static void LogErrorWithContext(
        this ILogger logger,
        Exception exception,
        string operationType,
        IDictionary<string, object>? context = null)
    {
        var errorContext = new Dictionary<string, object>
        {
            ["operation_type"] = operationType,
            ["error_type"] = exception.GetType().Name,
            ["error_message"] = exception.Message,
            ["stack_trace"] = exception.StackTrace ?? string.Empty,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        if (context != null)
        {
            foreach (var kvp in context)
            {
                errorContext[$"context_{kvp.Key}"] = kvp.Value;
            }
        }

        using var scope = logger.BeginScope(errorContext);
        StructuredLoggingLog.OperationFailed(logger, operationType, exception);
    }

    private static partial class StructuredLoggingLog
    {
        [LoggerMessage(9001, LogLevel.Information,
            "Operation {OperationType} completed in {DurationMs:F2}ms")]
        public static partial void OperationCompleted(ILogger logger, string operationType, double durationMs);

        [LoggerMessage(9002, LogLevel.Warning,
            "Slow operation {OperationType} took {DurationMs:F2}ms")]
        public static partial void SlowOperation(ILogger logger, string operationType, double durationMs);

        [LoggerMessage(9003, LogLevel.Error,
            "Operation {OperationType} failed")]
        public static partial void OperationFailed(ILogger logger, string operationType, Exception exception);
    }
}
