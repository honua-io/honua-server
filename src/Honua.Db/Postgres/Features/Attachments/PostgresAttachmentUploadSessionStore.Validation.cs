// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Attachments.Domain;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentUploadSessionStore
{
    private static void ValidateRequest(AttachmentUploadRequest request)
    {
        if (!HasByteLength(request.OwnerId, 256) || !HasByteLength(request.ServiceId, 256)
            || !HasByteLength(request.IdempotencyKey, 256) || !HasByteLength(request.Filename, 1024)
            || !HasByteLength(request.ContentType, 256) || request.LayerId < 0 || request.PublishedLayerId is < 0
            || request.Operation is not ("unary" or "chunked") || request.ExpectedSize < 0
            || request.Sha256 is not { Length: 32 } || request.ReplaceAttachmentId is <= 0
            || request.MaxChunkBytes <= 0 || request.Lifetime <= TimeSpan.Zero
            || request.Lifetime > TimeSpan.FromDays(7) || request.Keywords is null
            || request.Keywords.Any(keyword => keyword is null || keyword.Contains('\0')))
        {
            throw Failure(AttachmentUploadFailure.InvalidContent, "Upload request contains invalid metadata or bounds.");
        }
    }

    private static bool HasByteLength(string value, int maximum)
        => !string.IsNullOrEmpty(value) && !value.Contains('\0') && Encoding.UTF8.GetByteCount(value) <= maximum;

    private static byte[] Fingerprint(AttachmentUploadRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(request.PublishedLayerId.HasValue ? 2 : 1); // Version the canonical, length-prefixed request encoding.
            writer.Write(request.OwnerId);
            writer.Write(request.ServiceId);
            writer.Write(request.LayerId);
            if (request.PublishedLayerId is int publishedLayerId)
            {
                writer.Write(publishedLayerId);
            }
            writer.Write(request.FeatureId);
            writer.Write(request.Operation);
            writer.Write(request.IdempotencyKey);
            writer.Write(request.Filename);
            writer.Write(request.ContentType);
            writer.Write(request.Keywords.Length);
            foreach (var keyword in request.Keywords)
            {
                writer.Write(keyword);
            }
            writer.Write(request.ExpectedSize);
            writer.Write(request.Sha256);
            writer.Write(request.ReplaceAttachmentId.HasValue);
            if (request.ReplaceAttachmentId is long replacement)
            {
                writer.Write(replacement);
            }
        }
        // Negotiated limits and expiry are server policy, not a different client request.
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    private static void ValidateChunk(AttachmentUploadChunk chunk)
    {
        if (chunk.OffsetBytes < 0 || chunk.Content is not { Length: > 0 } || chunk.Sha256 is not { Length: 32 }
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(chunk.Content), chunk.Sha256))
        {
            throw Failure(AttachmentUploadFailure.InvalidContent, "Chunk bytes do not match their digest or bounds.");
        }
    }
}
