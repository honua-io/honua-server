// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication;

internal static partial class AdminAuthSessionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to project the server-managed admin session into an authenticated principal.")]
    public static partial void ClaimsProjectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin auth session store could not persist distributed cache key {CacheKey}.")]
    public static partial void DistributedCachePersistFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin auth session store could not read distributed cache key {CacheKey}.")]
    public static partial void DistributedCacheReadFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin auth session store could not remove distributed cache key {CacheKey}.")]
    public static partial void DistributedCacheRemoveFailed(ILogger logger, string cacheKey, Exception exception);
}
