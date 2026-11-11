// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Services;

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
        Message = "Temporary file storage capacity check failed in shared {Provider} storage for prefix {Prefix}; continuing upload without quota enforcement for this attempt.")]
    public static partial void SharedStorageCapacityCheckFailed(
        ILogger logger,
        CloudStorageProvider provider,
        string prefix,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Temporary file cleanup failed in shared {Provider} storage for prefix {Prefix}; continuing upload without cleanup for this attempt.")]
    public static partial void SharedStorageCleanupFailed(
        ILogger logger,
        CloudStorageProvider provider,
        string prefix,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting shared temporary file write because the system database is required to coordinate cloud-backed storage quotas.")]
    public static partial void SharedWriteRejectedCoordinationRequired(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting shared temporary file write because its download token could not be recorded.")]
    public static partial void SharedWriteRejectedCoordinationUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting shared temporary file write because the storage quota lease was lost.")]
    public static partial void SharedWriteRejectedCoordinationLost(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to remove temporary file {FileId} from shared {Provider} storage after the storage quota lease was lost.")]
    public static partial void SharedStorageCleanupAfterLeaseLossFailed(
        ILogger logger,
        string fileId,
        CloudStorageProvider provider,
        Exception exception);
}
