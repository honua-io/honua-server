// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

internal static partial class ProductionHealthChecksLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Database health check failed")]
    public static partial void DatabaseHealthCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Redis health check failed")]
    public static partial void RedisHealthCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "File upload health check failed")]
    public static partial void FileUploadHealthCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "External service health check failed")]
    public static partial void ExternalServiceHealthCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Production metrics health check failed")]
    public static partial void ProductionMetricsHealthCheckFailed(ILogger logger, Exception exception);
}
