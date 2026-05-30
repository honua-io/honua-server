// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Redis;

internal static partial class RedisHealthCheckLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Redis health check failed")]
    public static partial void HealthCheckFailed(ILogger logger, Exception exception);
}
