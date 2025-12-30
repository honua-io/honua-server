// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// High-performance logging for performance monitoring middleware using source generation for AOT compatibility.
/// </summary>
internal static partial class PerformanceMonitoringLog
{
    #region Request Performance (6000-6999)

    /// <summary>
    /// Logs when a slow request is detected.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="method">HTTP method</param>
    /// <param name="path">Request path</param>
    /// <param name="elapsedMs">Actual request duration in milliseconds</param>
    /// <param name="thresholdMs">Slow request threshold in milliseconds</param>
    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "Slow request detected: {Method} {Path} took {ElapsedMs:F2}ms (threshold: {ThresholdMs:F2}ms)")]
    public static partial void SlowRequestDetected(
        ILogger logger, string method, string path, double elapsedMs, double thresholdMs);

    /// <summary>
    /// Logs when an exception occurs during request processing (performance impact perspective).
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="method">HTTP method</param>
    /// <param name="path">Request path</param>
    /// <param name="exceptionType">Type of exception that occurred</param>
    /// <param name="elapsedMs">Request duration before exception in milliseconds</param>
    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Information,
        Message = "Request {Method} {Path} failed with {ExceptionType} after {ElapsedMs:F2}ms")]
    public static partial void RequestExceptionOccurred(
        ILogger logger, string method, string path, string exceptionType, double elapsedMs);

    /// <summary>
    /// Logs when request metrics are recorded.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="method">HTTP method</param>
    /// <param name="path">Request path</param>
    /// <param name="statusCode">Response status code</param>
    /// <param name="elapsedMs">Request duration in milliseconds</param>
    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Debug,
        Message = "Request metrics: {Method} {Path} -> {StatusCode} in {ElapsedMs:F2}ms")]
    public static partial void RequestMetricsRecorded(
        ILogger logger, string method, string path, int statusCode, double elapsedMs);

    #endregion

    #region Memory Monitoring (6100-6199)

    /// <summary>
    /// Logs when high memory pressure is detected.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="pressurePercentage">Memory pressure as a percentage</param>
    /// <param name="allocatedMB">Currently allocated memory in MB</param>
    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "High memory pressure detected: {PressurePercentage:F1}% ({AllocatedMB:F0}MB allocated)")]
    public static partial void HighMemoryPressureDetected(
        ILogger logger, double pressurePercentage, long allocatedMB);

    /// <summary>
    /// Logs when memory monitoring fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Warning,
        Message = "Memory monitoring failed: {ErrorMessage}")]
    public static partial void MemoryMonitoringFailed(
        ILogger logger, string errorMessage, Exception exception);

    /// <summary>
    /// Logs memory usage statistics.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="allocatedMB">Allocated memory in MB</param>
    /// <param name="heapSizeMB">Heap size in MB</param>
    /// <param name="pressurePercentage">Memory pressure percentage</param>
    /// <param name="totalCollections">Total GC collections across all generations</param>
    [LoggerMessage(
        EventId = 6103,
        Level = LogLevel.Debug,
        Message = "Memory stats: {AllocatedMB:F0}MB allocated, {HeapSizeMB:F0}MB heap, {PressurePercentage:F1}% pressure, {TotalCollections} GC collections")]
    public static partial void MemoryStatisticsRecorded(
        ILogger logger, long allocatedMB, long heapSizeMB, double pressurePercentage, int totalCollections);

    #endregion

    #region Performance Monitoring Infrastructure (6200-6299)

    /// <summary>
    /// Logs when performance monitoring middleware is initialized.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="enableMemoryTracking">Whether memory tracking is enabled</param>
    /// <param name="slowRequestThresholdMs">Slow request threshold in milliseconds</param>
    /// <param name="memorySamplingInterval">Memory sampling interval</param>
    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Information,
        Message = "Performance monitoring initialized: Memory tracking={EnableMemoryTracking}, Slow threshold={SlowRequestThresholdMs:F0}ms, Memory sampling={MemorySamplingInterval}")]
    public static partial void PerformanceMonitoringInitialized(
        ILogger logger, bool enableMemoryTracking, double slowRequestThresholdMs, int memorySamplingInterval);

    /// <summary>
    /// Logs when metrics export fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="metricName">Name of the metric that failed to export</param>
    /// <param name="errorMessage">Error message</param>
    [LoggerMessage(
        EventId = 6202,
        Level = LogLevel.Error,
        Message = "Metric export failed for {MetricName}: {ErrorMessage}")]
    public static partial void MetricExportFailed(
        ILogger logger, string metricName, string errorMessage);

    /// <summary>
    /// Logs when a custom metric is recorded.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="metricName">Name of the metric</param>
    /// <param name="metricValue">Value of the metric</param>
    /// <param name="tagCount">Number of tags associated with the metric</param>
    [LoggerMessage(
        EventId = 6203,
        Level = LogLevel.Trace,
        Message = "Custom metric recorded: {MetricName}={MetricValue} with {TagCount} tags")]
    public static partial void CustomMetricRecorded(
        ILogger logger, string metricName, double metricValue, int tagCount);

    #endregion

    #region Performance Alerts (6300-6399)

    /// <summary>
    /// Logs when performance degradation is detected.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="component">Component experiencing degradation</param>
    /// <param name="metric">Metric that triggered the alert</param>
    /// <param name="currentValue">Current value of the metric</param>
    /// <param name="threshold">Threshold that was exceeded</param>
    [LoggerMessage(
        EventId = 6301,
        Level = LogLevel.Warning,
        Message = "Performance degradation detected in {Component}: {Metric} = {CurrentValue} exceeds threshold of {Threshold}")]
    public static partial void PerformanceDegradationDetected(
        ILogger logger, string component, string metric, double currentValue, double threshold);

    /// <summary>
    /// Logs when performance has recovered.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="component">Component that recovered</param>
    /// <param name="metric">Metric that recovered</param>
    /// <param name="currentValue">Current value of the metric</param>
    [LoggerMessage(
        EventId = 6302,
        Level = LogLevel.Information,
        Message = "Performance recovered for {Component}: {Metric} = {CurrentValue}")]
    public static partial void PerformanceRecovered(
        ILogger logger, string component, string metric, double currentValue);

    #endregion
}
