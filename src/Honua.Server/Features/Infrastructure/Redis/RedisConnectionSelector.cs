// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Redis;

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
