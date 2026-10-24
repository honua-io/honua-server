// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Redis;

internal static class RedisConnectionSelector
{
    internal static string? SelectInfrastructureConnectionString(
        string? redisConnectionString,
        bool redisCacheEntitled,
        bool requiresDurableDistributedEvents)
    {
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            return null;
        }

        return redisCacheEntitled || requiresDurableDistributedEvents
            ? redisConnectionString
            : null;
    }
}
