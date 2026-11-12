// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis multiplexer initialized without an active connection. The client will continue retrying in the background.")]
    public static partial void RedisStartupConnectionInactive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to Redis at startup.")]
    public static partial void RedisStartupConnectionFailed(ILogger logger, Exception exception);
}
