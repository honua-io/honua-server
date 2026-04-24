// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Infrastructure.Services;

internal static partial class CloudBackedTemporaryFileLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Stored temporary file {FileId} in shared {Provider} storage ({Size} bytes, expires {ExpiresAt})")]
    public static partial void SharedStorageWriteCompleted(
        ILogger logger,
        string fileId,
        CloudStorageProvider provider,
        long size,
        DateTimeOffset? expiresAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to retrieve temporary file {FileId} from shared {Provider} storage")]
    public static partial void SharedStorageReadFailed(
        ILogger logger,
        string fileId,
        CloudStorageProvider provider,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Temporary file storage file-count limit reached in shared {Provider} storage: {CurrentFileCount} >= {MaxFileCount}")]
    public static partial void SharedStorageFileCountLimitReached(
        ILogger logger,
        CloudStorageProvider provider,
        int currentFileCount,
        int maxFileCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Temporary file storage capacity exceeded in shared {Provider} storage: {ProjectedBytes} > {MaxBytes}")]
    public static partial void SharedStorageCapacityExceeded(
        ILogger logger,
        CloudStorageProvider provider,
        long projectedBytes,
        long maxBytes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting shared temporary file write because Redis coordination is required for cloud-backed storage quotas.")]
    public static partial void SharedWriteRejectedRedisRequired(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting shared temporary file write because Redis coordination is unavailable for cloud-backed storage.")]
    public static partial void SharedWriteRejectedRedisUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting shared temporary file write because Redis coordination was lost while enforcing cloud-backed storage quotas.")]
    public static partial void SharedWriteRejectedRedisLost(ILogger logger);
}
