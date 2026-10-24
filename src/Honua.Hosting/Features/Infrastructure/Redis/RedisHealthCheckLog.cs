// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Redis;

internal static partial class RedisHealthCheckLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Redis health check failed")]
    public static partial void HealthCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis service diagnostics check failed")]
    public static partial void ServiceCheckFailed(ILogger logger, Exception exception);
}
