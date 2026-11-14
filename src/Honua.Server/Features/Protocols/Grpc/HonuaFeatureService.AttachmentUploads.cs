// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Authorization.Domain;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private const int AttachmentChunkBytes = 1024 * 1024;
    private static readonly TimeSpan AttachmentSessionLifetime = TimeSpan.FromHours(24);

    public override async Task<Proto.BeginAttachmentUploadResponse> BeginAttachmentUpload(
        Proto.BeginAttachmentUploadRequest request, ServerCallContext context)
        => new() { Session = ToAttachmentSession(await BeginAttachmentSessionAsync(request.Upload, context).ConfigureAwait(false)) };

    public override async Task<Proto.UploadAttachmentResponse> UploadAttachment(
        Proto.UploadAttachmentRequest request, ServerCallContext context)
    {
        if (request.Content.Length > AttachmentChunkBytes || request.Upload is null ||
            request.Upload.SizeBytes != request.Content.Length || request.Upload.Sha256.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(request.Content.Span), request.Upload.Sha256.Span))
        {
            throw AttachmentError(StatusCode.InvalidArgument, "The unary upload exceeds its limit or does not match its metadata.");
        }
        var session = await BeginAttachmentSessionAsync(request.Upload, context).ConfigureAwait(false);
        if (session.State == AttachmentUploadState.Uploading && request.Content.Length > 0)
        {
            session = await AppendAttachmentChunkAsync(session, new Proto.UploadAttachmentChunkRequest
            {
                OffsetBytes = 0, Content = request.Content, Sha256 = request.Upload.Sha256
            }, context).ConfigureAwait(false);
        }
        var attachment = await AttachmentService<IAttachmentUploadPublisher>(context).PublishAsync(session, context.CancellationToken).ConfigureAwait(false);
        return new Proto.UploadAttachmentResponse { Attachment = await DescribeAttachmentAsync(attachment, context).ConfigureAwait(false) };
    }

    public override async Task<Proto.UploadAttachmentChunkResponse> UploadAttachmentChunk(
        Proto.UploadAttachmentChunkRequest request, ServerCallContext context)
    {
        var session = await ResolveAttachmentSessionAsync(request.SessionId, context).ConfigureAwait(false);
        return new Proto.UploadAttachmentChunkResponse
        {
            Session = ToAttachmentSession(await AppendAttachmentChunkAsync(session, request, context).ConfigureAwait(false))
        };
    }

    public override async Task<Proto.CommitAttachmentUploadResponse> CommitAttachmentUpload(
        Proto.CommitAttachmentUploadRequest request, ServerCallContext context)
    {
        var session = await ResolveAttachmentSessionAsync(request.SessionId, context).ConfigureAwait(false);
        var attachment = await AttachmentService<IAttachmentUploadPublisher>(context).PublishAsync(session, context.CancellationToken).ConfigureAwait(false);
        return new Proto.CommitAttachmentUploadResponse { Attachment = await DescribeAttachmentAsync(attachment, context).ConfigureAwait(false) };
    }

    public override async Task<Proto.AbortAttachmentUploadResponse> AbortAttachmentUpload(
        Proto.AbortAttachmentUploadRequest request, ServerCallContext context)
    {
        var session = await ResolveAttachmentSessionAsync(request.SessionId, context).ConfigureAwait(false);
        await AttachmentService<IAttachmentUploadSessionStore>(context).AbortAsync(
            new AttachmentUploadSessionKey(session.Request.OwnerId, session.SessionId), context.CancellationToken).ConfigureAwait(false);
        return new Proto.AbortAttachmentUploadResponse();
    }

    private async Task<AttachmentUploadSession> BeginAttachmentSessionAsync(Proto.AttachmentUpload? upload, ServerCallContext context)
    {
        ValidateAttachmentUpload(upload, context);
        var target = await ResolveAttachmentParentAsync(upload!.Parent, context).ConfigureAwait(false);
        await EnsureAttachmentEditAccessAsync(target.Layer, context, AuthorizationOperation.Update).ConfigureAwait(false);
        return await AttachmentService<IAttachmentUploadSessionStore>(context).BeginAsync(new AttachmentUploadRequest
        {
            OwnerId = AttachmentOwner(context), ServiceId = upload.Parent.ServiceId,
            LayerId = target.Layer.StorageLayerId, PublishedLayerId = upload.Parent.LayerId, FeatureId = target.FeatureId,
            Operation = upload.HasReplaceAttachmentId ? "replace" : "create", IdempotencyKey = upload.IdempotencyKey,
            Filename = upload.Name, ContentType = upload.ContentType, Keywords = upload.Keywords.ToArray(),
            ExpectedSize = upload.SizeBytes, Sha256 = upload.Sha256.ToByteArray(),
            ReplaceAttachmentId = upload.HasReplaceAttachmentId ? upload.ReplaceAttachmentId : null,
            MaxChunkBytes = AttachmentChunkBytes, Lifetime = AttachmentSessionLifetime
        }, context.CancellationToken).ConfigureAwait(false);
    }

    private static void ValidateAttachmentUpload(Proto.AttachmentUpload? upload, ServerCallContext context)
    {
        var limits = context.GetHttpContext().RequestServices.GetService<IOptions<LimitsOptions>>()?.Value ?? new LimitsOptions();
        if (upload is null || upload.Parent is null || string.IsNullOrWhiteSpace(upload.Name) ||
            upload.Name.Length > 1024 || string.IsNullOrWhiteSpace(upload.ContentType) || upload.ContentType.Length > 256 ||
            string.IsNullOrWhiteSpace(upload.IdempotencyKey) || System.Text.Encoding.UTF8.GetByteCount(upload.IdempotencyKey) > 256 ||
            upload.Sha256.Length != SHA256.HashSizeInBytes || upload.SizeBytes < 0 ||
            upload.HasReplaceAttachmentId && upload.ReplaceAttachmentId <= 0 ||
            upload.Keywords.Count > 100 || upload.Keywords.Any(value => value.Length > 1024 || value.Contains('\0')))
        {
            throw AttachmentError(StatusCode.InvalidArgument, "Valid attachment metadata and an idempotency key are required.");
        }
        if (upload.SizeBytes > Math.Min(limits.MaxUploadSizeBytes, limits.Attachments.MaxAttachmentSize))
        {
            throw AttachmentError(StatusCode.ResourceExhausted, "The attachment exceeds the configured upload limit.");
        }
    }

    private static async Task<AttachmentUploadSession> AppendAttachmentChunkAsync(
        AttachmentUploadSession session, Proto.UploadAttachmentChunkRequest request, ServerCallContext context)
    {
        if (request.Content.Length == 0 || request.Content.Length > session.Request.MaxChunkBytes ||
            request.OffsetBytes < 0 || request.Sha256.Length != SHA256.HashSizeInBytes)
        {
            throw AttachmentError(StatusCode.InvalidArgument, "The upload chunk is invalid or exceeds its limit.");
        }
        try
        {
            return await AttachmentService<IAttachmentUploadSessionStore>(context).AppendAsync(new AttachmentUploadChunk
            {
                Key = new AttachmentUploadSessionKey(session.Request.OwnerId, session.SessionId),
                OffsetBytes = request.OffsetBytes, Content = request.Content.ToByteArray(), Sha256 = request.Sha256.ToByteArray()
            }, context.CancellationToken).ConfigureAwait(false);
        }
        catch (AttachmentUploadException exception) when (exception.Failure == AttachmentUploadFailure.Conflict)
        {
            throw AttachmentError(StatusCode.FailedPrecondition, "The upload chunk conflicts with the accepted content.");
        }
    }

    private static Proto.AttachmentUploadSession ToAttachmentSession(AttachmentUploadSession session)
        => new()
        {
            SessionId = session.SessionId.ToString("D"), ExpiresAtUtcMs = session.ExpiresAt.ToUnixTimeMilliseconds(),
            NextOffsetBytes = session.NextOffsetBytes, MaxChunkBytes = session.Request.MaxChunkBytes
        };
}
