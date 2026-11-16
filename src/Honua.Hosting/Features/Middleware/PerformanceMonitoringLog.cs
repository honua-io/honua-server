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

    #endregion

    #region Performance Monitoring Infrastructure (6200-6299)

    #endregion

    #region Performance Alerts (6300-6399)

    #endregion
}
