// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Configuration;

internal static partial class SecretProviderLog
{
    [LoggerMessage(EventId = 9644, Level = LogLevel.Debug, Message = "Attempting to retrieve secret with reference: {SecretRef}")]
    public static partial void AttemptingSecretRetrieval(ILogger logger, string secretRef);

    [LoggerMessage(EventId = 9645, Level = LogLevel.Debug, Message = "Secret retrieved from cache: {SecretRef}")]
    public static partial void SecretRetrievedFromCache(ILogger logger, string secretRef);

    [LoggerMessage(EventId = 9646, Level = LogLevel.Debug, Message = "Secret successfully retrieved: {SecretRef}")]
    public static partial void SecretRetrieved(ILogger logger, string secretRef);

    [LoggerMessage(EventId = 9647, Level = LogLevel.Error, Message = "Failed to retrieve secret: {SecretRef}")]
    public static partial void SecretRetrievalFailed(ILogger logger, string secretRef, Exception exception);

    [LoggerMessage(EventId = 9648, Level = LogLevel.Debug, Message = "Secret not found, using default value: {SecretRef}")]
    public static partial void SecretNotFoundUsingDefault(ILogger logger, string secretRef);

    [LoggerMessage(EventId = 9649, Level = LogLevel.Debug, Message = "Failed to test secret resolution: {SecretRef}")]
    public static partial void SecretResolutionTestFailed(ILogger logger, string secretRef, Exception exception);

    [LoggerMessage(EventId = 9650, Level = LogLevel.Debug, Message = "Cleaned up {Count} expired secret cache entries")]
    public static partial void ExpiredSecretCacheEntriesCleanedUp(ILogger logger, int count);
}
