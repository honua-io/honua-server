// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Import;

internal static partial class StreamingFileUploadLog
{
    [LoggerMessage(EventId = 9658, Level = LogLevel.Warning, Message = "Upload queue is full, rejecting upload for {FileName}")]
    public static partial void UploadQueueFull(ILogger logger, string fileName);

    [LoggerMessage(EventId = 9659, Level = LogLevel.Warning, Message = "Timeout waiting for upload processing slot for {FileName}")]
    public static partial void UploadProcessingSlotTimeout(ILogger logger, string fileName);

    [LoggerMessage(EventId = 9660, Level = LogLevel.Information, Message = "File upload cancelled for {FileName}")]
    public static partial void FileUploadCancelled(ILogger logger, string fileName);

    [LoggerMessage(EventId = 9661, Level = LogLevel.Error, Message = "Failed to process file upload for {FileName}")]
    public static partial void FileUploadProcessingFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 9662, Level = LogLevel.Information, Message = "Starting streaming upload for {FileName} ({FileSize} bytes)")]
    public static partial void StreamingUploadStarted(ILogger logger, string fileName, long fileSize);

    [LoggerMessage(EventId = 9663, Level = LogLevel.Information, Message = "File upload completed for {FileName} in {ProcessingTime}ms")]
    public static partial void FileUploadCompleted(ILogger logger, string fileName, double processingTime);

    [LoggerMessage(EventId = 9664, Level = LogLevel.Warning, Message = "Failed to delete temp file {TempFilePath}")]
    public static partial void TempFileDeleteFailed(ILogger logger, string tempFilePath, Exception exception);
}
