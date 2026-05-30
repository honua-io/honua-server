// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal static partial class AdminAuthSessionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to project the server-managed admin session into an authenticated principal.")]
    public static partial void ClaimsProjectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin auth session store could not persist distributed cache entry {KeyFamily} {SessionHash}.")]
    public static partial void DistributedCachePersistFailed(ILogger logger, string keyFamily, string sessionHash, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin auth session store could not read distributed cache entry {KeyFamily} {SessionHash}.")]
    public static partial void DistributedCacheReadFailed(ILogger logger, string keyFamily, string sessionHash, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin auth session store could not remove distributed cache entry {KeyFamily} {SessionHash}.")]
    public static partial void DistributedCacheRemoveFailed(ILogger logger, string keyFamily, string sessionHash, Exception exception);
}
