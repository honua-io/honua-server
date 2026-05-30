// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.RateLimiting;

internal static partial class RateLimitingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check rate limit for {KeyFamily} {KeyHash}, allowing request")]
    public static partial void RateLimitCheckFailed(ILogger logger, string keyFamily, string keyHash, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded for {KeyFamily} {KeyHash}. Requests: {RequestCount}/{Limit}")]
    public static partial void RateLimitExceeded(ILogger logger, string keyFamily, string keyHash, int requestCount, int limit);
}
