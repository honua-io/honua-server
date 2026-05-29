// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Source-generated logging for monitoring and metrics endpoints.
/// </summary>
internal static partial class MonitoringLog
{
    /// <summary>
    /// Logs when health metrics retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4300,
        Level = LogLevel.Error,
        Message = "Failed to retrieve health metrics")]
    public static partial void HealthMetricsFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Logs when performance metrics retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4301,
        Level = LogLevel.Error,
        Message = "Failed to retrieve performance metrics")]
    public static partial void PerformanceMetricsFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Logs when database metrics retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4302,
        Level = LogLevel.Error,
        Message = "Failed to retrieve database metrics")]
    public static partial void DatabaseMetricsFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Logs when cache metrics retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4303,
        Level = LogLevel.Error,
        Message = "Failed to retrieve cache metrics")]
    public static partial void CacheMetricsFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Logs when memory metrics retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4304,
        Level = LogLevel.Error,
        Message = "Failed to retrieve memory metrics")]
    public static partial void MemoryMetricsFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Logs when query cache statistics retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4305,
        Level = LogLevel.Error,
        Message = "Failed to retrieve query cache statistics")]
    public static partial void QueryCacheStatisticsFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Logs when streaming metrics retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4306,
        Level = LogLevel.Error,
        Message = "Failed to retrieve streaming metrics")]
    public static partial void StreamingMetricsFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4307,
        Level = LogLevel.Warning,
        Message = "Failed to get upload queue depth from IUploadQueueMetricsProvider")]
    public static partial void UploadQueueDepthRetrievalFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4308,
        Level = LogLevel.Error,
        Message = "Comprehensive health endpoint failed.")]
    public static partial void ComprehensiveHealthEndpointFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4309,
        Level = LogLevel.Error,
        Message = "Database resilience metrics endpoint failed.")]
    public static partial void DatabaseResilienceMetricsEndpointFailed(ILogger logger, Exception exception);
}
