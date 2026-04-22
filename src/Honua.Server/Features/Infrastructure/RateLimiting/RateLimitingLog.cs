// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.RateLimiting;

internal static partial class RateLimitingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check rate limit for key {RateLimitKey}, allowing request")]
    public static partial void RateLimitCheckFailed(ILogger logger, string? rateLimitKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded for {RateLimitKey}. Requests: {RequestCount}/{Limit}")]
    public static partial void RateLimitExceeded(ILogger logger, string? rateLimitKey, int requestCount, int limit);
}
