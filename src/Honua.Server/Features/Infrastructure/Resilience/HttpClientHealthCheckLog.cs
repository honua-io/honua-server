// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Resilience;

internal static partial class HttpClientHealthCheckLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP health check for {ServiceType} succeeded in {ElapsedMs}ms")]
    public static partial void Succeeded(ILogger logger, string serviceType, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP health check for {ServiceType} was cancelled")]
    public static partial void Cancelled(ILogger logger, string serviceType);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP health check for {ServiceType} failed with exception")]
    public static partial void Failed(ILogger logger, string serviceType, Exception exception);
}
