// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.HealthChecks;

internal static partial class BasicHealthCheckServiceLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting health check")]
    public static partial void HealthCheckStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Health check completed with status {Status} in {DurationMs}ms")]
    public static partial void HealthCheckCompleted(ILogger logger, HealthStatus status, long durationMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Health check failed")]
    public static partial void HealthCheckFailed(ILogger logger, Exception exception);
}
