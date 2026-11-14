// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskAttachmentContent(IWorkTaskAttachmentStore attachments, ICloudFileStorage storage, WorkTaskAttachmentPolicy policy)
{
    public WorkTaskAttachmentPolicy Policy => policy;

    public async Task<bool> UploadAsync((WorkTaskAttachment Attachment, WorkTaskAttachmentLimits Limits, IFormFile File) upload,
        CancellationToken cancellationToken)
    {
        var (attachment, limits, file) = upload;
        if (!await attachments.ReserveAsync(attachment, limits, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        await using var stream = file.OpenReadStream();
        var result = await storage.UploadIfMatchAsync(new FileUploadRequest
        {
            Content = stream, FileName = attachment.Filename, ContentType = attachment.ContentType,
            SizeBytes = attachment.SizeBytes, ObjectKeyOverride = attachment.BlobKey,
            EnableChunkedUpload = false,
        }, null, cancellationToken).ConfigureAwait(false);
        return result.Success && await attachments.MarkReadyAsync(attachment.AttachmentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(WorkTaskAttachment Attachment, Stream Bytes)?> DownloadAsync(
        (Guid TaskId, Guid AttachmentId, string Owner) lookup, CancellationToken cancellationToken)
    {
        var attachment = await attachments.GetAsync(lookup.AttachmentId, cancellationToken).ConfigureAwait(false);
        if (attachment is null || attachment.TaskId != lookup.TaskId ||
            (attachment.Status != "claimed" && (attachment.Status != "ready" ||
                attachment.ExpiresAt <= DateTimeOffset.UtcNow || attachment.OwnerSubject != lookup.Owner)))
        {
            return null;
        }
        var stream = await storage.DownloadAsync(attachment.BlobKey, cancellationToken).ConfigureAwait(false);
        return stream is null ? null : (attachment, stream);
    }
}
