// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis multiplexer initialized without an active connection. The client will continue retrying in the background.")]
    public static partial void RedisStartupConnectionInactive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to Redis at startup. RedisCacheService will operate in fallback mode.")]
    public static partial void RedisStartupConnectionFailed(ILogger logger, Exception exception);
}
