// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Attachments;

internal sealed class AttachmentUploadPublisher(
    IAttachmentUploadSessionStore sessions,
    AttachmentUploadContentVerifier verifier,
    ICloudFileStorage storage) : IAttachmentUploadPublisher
{
    public async Task<Attachment> PublishAsync(AttachmentUploadSession session, CancellationToken cancellationToken = default)
    {
        var key = new AttachmentUploadSessionKey(session.Request.OwnerId, session.SessionId);
        if (session.State == AttachmentUploadState.Committed)
        {
            return await sessions.CommitAsync(key, (_, _) => throw new InvalidOperationException("The committed receipt is unavailable."), cancellationToken).ConfigureAwait(false);
        }
        await using var content = await verifier.VerifyAsync(session, cancellationToken).ConfigureAwait(false);
        return await sessions.CommitAsync(key, (lockedSession, token) =>
            PublishContentAsync(lockedSession, content, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PublishContentAsync(AttachmentUploadSession session, FileStream content, CancellationToken cancellationToken)
    {
        var objectKey = AttachmentUploadObjectKey.ForSession(session.SessionId);
        content.Position = 0;
        var uploaded = await storage.UploadIfMatchAsync(new FileUploadRequest
        {
            Content = content,
            FileName = session.Request.Filename,
            ContentType = session.Request.ContentType,
            SizeBytes = session.Request.ExpectedSize,
            ObjectKeyOverride = objectKey,
            EnableChunkedUpload = false
        }, null, cancellationToken).ConfigureAwait(false);
        if (uploaded.Success && uploaded.File?.FileId == objectKey)
        {
            return objectKey;
        }
        await using var existing = await storage.DownloadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            throw new IOException("Attachment storage is unavailable.");
        }
        await VerifyExistingContentAsync(existing, session.Request, cancellationToken).ConfigureAwait(false);
        return objectKey;
    }

    private static async Task VerifyExistingContentAsync(Stream content, AttachmentUploadRequest request, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long remaining = request.ExpectedSize;
        while (remaining > 0)
        {
            var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw InvalidContent();
            }
            digest.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (await content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0
            || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), request.Sha256))
        {
            throw InvalidContent();
        }
    }

    private static AttachmentUploadException InvalidContent()
        => new(AttachmentUploadFailure.InvalidContent, "Stored attachment content failed integrity validation.");
}
