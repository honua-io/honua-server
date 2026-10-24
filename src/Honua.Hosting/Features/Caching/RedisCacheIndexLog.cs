// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal static partial class RedisCacheIndexLog
{
    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "Failed to track cache entry {KeyFamily} {KeyHash} in Redis index.")]
    public static partial void RedisIndexTrackFailed(ILogger logger, string keyFamily, string keyHash, Exception exception);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "Failed to remove cache entry {KeyFamily} {KeyHash} from Redis index.")]
    public static partial void RedisIndexRemoveFailed(ILogger logger, string keyFamily, string keyHash, Exception exception);
}
