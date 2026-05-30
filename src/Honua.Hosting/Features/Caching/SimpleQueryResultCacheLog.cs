// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal static partial class SimpleQueryResultCacheLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to get cached result for key: {CacheKey}")]
    public static partial void GetFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to set cache key: {CacheKey}")]
    public static partial void SetFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to remove cache key: {CacheKey}")]
    public static partial void RemoveFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to invalidate cache entries with pattern: {Pattern}")]
    public static partial void InvalidateFailed(ILogger logger, string pattern, Exception exception);
}
