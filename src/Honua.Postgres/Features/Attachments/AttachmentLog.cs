// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Attachments;

internal static partial class AttachmentLog
{
    [LoggerMessage(
        EventId = 5501,
        Level = LogLevel.Warning,
        Message = "Attachment file {FileId} not found during deletion for layer {LayerId} feature {FeatureId}")]
    public static partial void AttachmentFileMissing(
        ILogger logger,
        string fileId,
        int layerId,
        long featureId);

    [LoggerMessage(
        EventId = 5502,
        Level = LogLevel.Warning,
        Message = "Failed to delete attachment file {FileId} from storage")]
    public static partial void AttachmentFileDeleteFailed(
        ILogger logger,
        Exception exception,
        string fileId);

    [LoggerMessage(
        EventId = 5503,
        Level = LogLevel.Warning,
        Message = "Attachment upload failed for layer {LayerId} feature {FeatureId}: {Message}")]
    public static partial void AttachmentUploadFailed(
        ILogger logger,
        int layerId,
        long featureId,
        string message);

    [LoggerMessage(
        EventId = 5504,
        Level = LogLevel.Warning,
        Message = "Failed to cleanup attachment file {FileId} after database error")]
    public static partial void AttachmentCleanupFailed(
        ILogger logger,
        Exception exception,
        string fileId);
}
