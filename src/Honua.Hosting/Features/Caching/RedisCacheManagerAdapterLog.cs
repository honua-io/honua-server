// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal static partial class RedisCacheManagerAdapterLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to get cached value for key: {Key}")]
    public static partial void GetFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to set cached value for key: {Key}")]
    public static partial void SetFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to remove cached value for key: {Key}")]
    public static partial void RemoveFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pattern-based cache removal is not supported by IDistributedCache: {Pattern}")]
    public static partial void PatternRemovalUnsupported(ILogger logger, string pattern);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to check if key exists: {Key}")]
    public static partial void ExistsCheckFailed(ILogger logger, string key, Exception exception);
}
