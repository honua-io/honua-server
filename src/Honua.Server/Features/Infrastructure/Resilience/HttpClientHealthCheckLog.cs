// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Resilience;

internal static partial class HttpClientHealthCheckLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP health check for {ServiceType} skipped due to open circuit breaker")]
    public static partial void SkippedDueToOpenCircuitBreaker(ILogger logger, string serviceType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP health check for {ServiceType} succeeded in {ElapsedMs}ms")]
    public static partial void Succeeded(ILogger logger, string serviceType, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP health check for {ServiceType} returned {StatusCode} in {ElapsedMs}ms")]
    public static partial void ReturnedStatusCode(ILogger logger, string serviceType, int statusCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP health check for {ServiceType} was cancelled")]
    public static partial void Cancelled(ILogger logger, string serviceType);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP health check for {ServiceType} failed with exception")]
    public static partial void Failed(ILogger logger, string serviceType, Exception exception);
}
