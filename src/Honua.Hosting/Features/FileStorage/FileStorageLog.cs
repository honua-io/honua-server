// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.FileStorage;

internal static partial class FileStorageLog
{
    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Information,
        Message = "Automatic file cleanup is disabled")]
    public static partial void CleanupDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Information,
        Message = "File storage cleanup service started with interval of {Interval}")]
    public static partial void CleanupServiceStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Error,
        Message = "Error during file storage cleanup")]
    public static partial void CleanupError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5404,
        Level = LogLevel.Information,
        Message = "File storage cleanup service stopped")]
    public static partial void CleanupServiceStopped(ILogger logger);

    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Information,
        Message = "Cleanup completed: removed {Count} expired files")]
    public static partial void CleanupCompleted(ILogger logger, int count);

    [LoggerMessage(
        EventId = 5410,
        Level = LogLevel.Information,
        Message = "Uploaded file {FileName} ({SizeBytes} bytes) as {FileId} in {DurationMs}ms")]
    public static partial void FileUploaded(
        ILogger logger,
        string fileName,
        long sizeBytes,
        string fileId,
        long durationMs);

    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Error,
        Message = "Failed to upload file {FileName}")]
    public static partial void FileUploadFailed(ILogger logger, Exception exception, string fileName);

    [LoggerMessage(
        EventId = 5412,
        Level = LogLevel.Warning,
        Message = "File metadata exists but file not found on disk: {FileId}")]
    public static partial void FileMissingOnDisk(ILogger logger, string fileId);

    [LoggerMessage(
        EventId = 5413,
        Level = LogLevel.Information,
        Message = "Deleted file {FileId}")]
    public static partial void FileDeleted(ILogger logger, string fileId);

    [LoggerMessage(
        EventId = 5414,
        Level = LogLevel.Error,
        Message = "Failed to delete file {FileId}")]
    public static partial void FileDeleteFailed(ILogger logger, Exception exception, string fileId);

    [LoggerMessage(
        EventId = 5415,
        Level = LogLevel.Information,
        Message = "Deleted batch {BatchId} with {DeletedCount} files")]
    public static partial void BatchDeleted(ILogger logger, string batchId, int deletedCount);

    [LoggerMessage(
        EventId = 5416,
        Level = LogLevel.Information,
        Message = "Cleaned up {Count} expired files")]
    public static partial void ExpiredFilesCleaned(ILogger logger, int count);

    [LoggerMessage(
        EventId = 5417,
        Level = LogLevel.Information,
        Message = "Created storage directory: {BasePath}")]
    public static partial void StorageDirectoryCreated(ILogger logger, string basePath);

    [LoggerMessage(
        EventId = 5418,
        Level = LogLevel.Warning,
        Message = "Failed to load metadata from {File}")]
    public static partial void MetadataLoadFailed(ILogger logger, Exception exception, string file);

    [LoggerMessage(
        EventId = 5419,
        Level = LogLevel.Information,
        Message = "Loaded {Count} files from existing metadata")]
    public static partial void MetadataLoaded(ILogger logger, int count);

    [LoggerMessage(
        EventId = 5420,
        Level = LogLevel.Warning,
        Message = "Failed to report upload progress for {UploadId}")]
    public static partial void ProgressUpdateFailed(ILogger logger, Exception exception, string uploadId);

    [LoggerMessage(
        EventId = 5421,
        Level = LogLevel.Information,
        Message = "Cleaned up cancelled upload {UploadId}, removed temp file {TempFileId}")]
    public static partial void CleanupCancelledUpload(ILogger logger, string uploadId, string tempFileId);

    [LoggerMessage(
        EventId = 5422,
        Level = LogLevel.Warning,
        Message = "Failed to cleanup cancelled upload {UploadId}")]
    public static partial void CleanupCancelledUploadFailed(ILogger logger, string uploadId, Exception exception);
}
