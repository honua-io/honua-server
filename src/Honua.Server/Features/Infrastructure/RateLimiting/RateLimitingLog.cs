// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.RateLimiting;

internal static partial class RateLimitingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check rate limit for {KeyFamily} {KeyHash}, allowing request")]
    public static partial void RateLimitCheckFailed(ILogger logger, string keyFamily, string keyHash, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis rate limit counter failed for {KeyFamily} {KeyHash}; enforcing the process-local limit")]
    public static partial void RedisCounterFallback(ILogger logger, string keyFamily, string keyHash, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded for {KeyFamily} {KeyHash}. Requests: {RequestCount}/{Limit}")]
    public static partial void RateLimitExceeded(ILogger logger, string keyFamily, string keyHash, int requestCount, int limit);

    [LoggerMessage(
        EventId = 2761,
        Level = LogLevel.Warning,
        Message = "Process-local rate-limit table is at capacity ({Capacity}); evicting the least-recently-used counter")]
    public static partial void MemoryCounterCapacityEvicted(ILogger logger, int capacity);
}
