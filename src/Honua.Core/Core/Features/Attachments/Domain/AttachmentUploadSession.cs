// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

public sealed record AttachmentUploadRequest
{
    public required string OwnerId { get; init; }
    public required string ServiceId { get; init; }
    public required int LayerId { get; init; }
    public int? PublishedLayerId { get; init; }
    public required long FeatureId { get; init; }
    public required string Operation { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Filename { get; init; }
    public required string ContentType { get; init; }
    public string[] Keywords { get; init; } = [];
    public required long ExpectedSize { get; init; }
    public required byte[] Sha256 { get; init; }
    public long? ReplaceAttachmentId { get; init; }
    public required int MaxChunkBytes { get; init; }
    public required TimeSpan Lifetime { get; init; }
}

public readonly record struct AttachmentUploadSessionKey(string OwnerId, Guid SessionId);

public sealed record AttachmentUploadChunk
{
    public required AttachmentUploadSessionKey Key { get; init; }
    public required long OffsetBytes { get; init; }
    public required byte[] Content { get; init; }
    public required byte[] Sha256 { get; init; }
}

public sealed record AttachmentUploadSession
{
    public required Guid SessionId { get; init; }
    public required AttachmentUploadRequest Request { get; init; }
    public required long NextOffsetBytes { get; init; }
    public required AttachmentUploadState State { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public enum AttachmentUploadState { Uploading, Committed, Aborted }

public enum AttachmentUploadFailure { NotFound, Conflict, InvalidState, InvalidContent, Expired }

public sealed class AttachmentUploadException(AttachmentUploadFailure failure, string message) : Exception(message)
{
    public AttachmentUploadFailure Failure { get; } = failure;
}
