// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Middleware;

internal static partial class PerformanceMonitoringLog
{
    #region Request Performance (6000-6999)

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "Slow request detected: {Method} {Path} took {ElapsedMs:F2}ms (threshold: {ThresholdMs:F2}ms)")]
    public static partial void SlowRequestDetected(
        ILogger logger, string method, string path, double elapsedMs, double thresholdMs);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Information,
        Message = "Request {Method} {Path} failed with {ExceptionType} after {ElapsedMs:F2}ms")]
    public static partial void RequestExceptionOccurred(
        ILogger logger, string method, string path, string exceptionType, double elapsedMs);

    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Debug,
        Message = "Request metrics: {Method} {Path} -> {StatusCode} in {ElapsedMs:F2}ms")]
    public static partial void RequestMetricsRecorded(
        ILogger logger, string method, string path, int statusCode, double elapsedMs);

    #endregion

    #region Memory Monitoring (6100-6199)

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "High memory pressure detected: {PressurePercentage:F1}% ({AllocatedMB:F0}MB allocated)")]
    public static partial void HighMemoryPressureDetected(
        ILogger logger, double pressurePercentage, long allocatedMB);

    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Warning,
        Message = "Memory monitoring failed: {ErrorMessage}")]
    public static partial void MemoryMonitoringFailed(
        ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 6103,
        Level = LogLevel.Debug,
        Message = "Memory stats: {AllocatedMB:F0}MB allocated, {HeapSizeMB:F0}MB heap, {PressurePercentage:F1}% pressure, {TotalCollections} GC collections")]
    public static partial void MemoryStatisticsRecorded(
        ILogger logger, long allocatedMB, long heapSizeMB, double pressurePercentage, int totalCollections);

    #endregion

    #region Performance Monitoring Infrastructure (6200-6299)

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Information,
        Message = "Performance monitoring initialized: Memory tracking={EnableMemoryTracking}, Slow threshold={SlowRequestThresholdMs:F0}ms, Memory sampling={MemorySamplingInterval}")]
    public static partial void PerformanceMonitoringInitialized(
        ILogger logger, bool enableMemoryTracking, double slowRequestThresholdMs, int memorySamplingInterval);

    [LoggerMessage(
        EventId = 6202,
        Level = LogLevel.Error,
        Message = "Metric export failed for {MetricName}: {ErrorMessage}")]
    public static partial void MetricExportFailed(
        ILogger logger, string metricName, string errorMessage);

    [LoggerMessage(
        EventId = 6203,
        Level = LogLevel.Trace,
        Message = "Custom metric recorded: {MetricName}={MetricValue} with {TagCount} tags")]
    public static partial void CustomMetricRecorded(
        ILogger logger, string metricName, double metricValue, int tagCount);

    #endregion

    #region Performance Alerts (6300-6399)

    [LoggerMessage(
        EventId = 6301,
        Level = LogLevel.Warning,
        Message = "Performance degradation detected in {Component}: {Metric} = {CurrentValue} exceeds threshold of {Threshold}")]
    public static partial void PerformanceDegradationDetected(
        ILogger logger, string component, string metric, double currentValue, double threshold);

    [LoggerMessage(
        EventId = 6302,
        Level = LogLevel.Information,
        Message = "Performance recovered for {Component}: {Metric} = {CurrentValue}")]
    public static partial void PerformanceRecovered(
        ILogger logger, string component, string metric, double currentValue);

    #endregion
}
