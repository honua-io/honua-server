// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    public override async Task<Proto.ListAttachmentsResponse> ListAttachments(
        Proto.ListAttachmentsRequest request, ServerCallContext context)
    {
        if (request.PageSize < 0 || request.PageSize > 500 || request.ContentTypes.Count > 100 ||
            request.Keywords.Count > 100 || request.PageToken.Length > 256 ||
            request.ContentTypes.Concat(request.Keywords).Any(value => value.Length > 1024))
        {
            throw AttachmentError(StatusCode.InvalidArgument, "The attachment page or filters exceed their limits.");
        }
        var target = await ResolveAttachmentParentAsync(request.Parent, context).ConfigureAwait(false);
        var scope = AttachmentPageScope(request);
        var after = ReadAttachmentCursor(request.PageToken, scope);
        var size = request.PageSize == 0 ? 100 : request.PageSize;
        var attachments = await AttachmentService<IAttachmentNativeMetadataStore>(context).QueryPageAsync(
            new AttachmentPageQuery
            {
                LayerId = target.Layer.StorageLayerId, FeatureId = target.FeatureId,
                AfterId = after, Limit = size + 1,
                ContentTypes = request.ContentTypes.ToArray(), Keywords = request.Keywords.ToArray()
            }, context.CancellationToken).ConfigureAwait(false);
        var page = attachments.Select(value => MapAttachment(value.Attachment, value.Metadata)).ToArray();
        var response = new Proto.ListAttachmentsResponse();
        response.Attachments.AddRange(page.Take(size));
        if (page.Length > size)
        {
            response.NextPageToken = $"{scope}:{page[size - 1].AttachmentId.ToString(CultureInfo.InvariantCulture)}";
        }
        return response;
    }

    public override async Task<Proto.GetAttachmentUrlResponse> GetAttachmentUrl(
        Proto.GetAttachmentUrlRequest request, ServerCallContext context)
    {
        if (request.AttachmentId <= 0)
        {
            throw AttachmentError(StatusCode.InvalidArgument, "A valid attachment identifier is required.");
        }
        var target = await ResolveAttachmentParentAsync(request.Parent, context).ConfigureAwait(false);
        var attachment = await AttachmentService<IAttachmentStore>(context).GetAsync(
            target.Layer.StorageLayerId, target.FeatureId, request.AttachmentId, context.CancellationToken).ConfigureAwait(false)
            ?? throw AttachmentError(StatusCode.NotFound, "The attachment was not found.");
        var lifetime = TimeSpan.FromMinutes(5);
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        var url = await AttachmentService<ICloudFileStorage>(context).GetPresignedUrlAsync(
            attachment.StoragePath, lifetime, context.CancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw AttachmentError(StatusCode.FailedPrecondition, "Secure attachment downloads are not configured.");
        }
        return new Proto.GetAttachmentUrlResponse
        {
            Url = url, ExpiresAtUtcMs = expires.ToUnixTimeMilliseconds(),
            Attachment = await DescribeAttachmentAsync(attachment, context).ConfigureAwait(false)
        };
    }

    public override async Task<Proto.DeleteAttachmentResponse> DeleteAttachment(
        Proto.DeleteAttachmentRequest request, ServerCallContext context)
    {
        if (request.AttachmentId <= 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            Encoding.UTF8.GetByteCount(request.IdempotencyKey) > 256)
        {
            throw AttachmentError(StatusCode.InvalidArgument, "An attachment identifier and idempotency key are required.");
        }
        var target = await ResolveAttachmentParentAsync(request.Parent, context).ConfigureAwait(false);
        await EnsureAttachmentEditAccessAsync(target.Layer, context, AuthorizationOperation.Delete).ConfigureAwait(false);
        await AttachmentService<IAttachmentMutationStore>(context).DeleteAsync(new AttachmentDeleteRequest
        {
            OwnerId = AttachmentOwner(context), ServiceId = request.Parent.ServiceId,
            LayerId = target.Layer.StorageLayerId, FeatureId = target.FeatureId,
            AttachmentId = request.AttachmentId, IdempotencyKey = request.IdempotencyKey, Lifetime = AttachmentSessionLifetime
        }, context.CancellationToken).ConfigureAwait(false);
        return new Proto.DeleteAttachmentResponse { AttachmentId = request.AttachmentId };
    }

    private static async Task<Proto.AttachmentDescriptor> DescribeAttachmentAsync(Attachment attachment, ServerCallContext context)
    {
        var metadata = await AttachmentService<IAttachmentNativeMetadataStore>(context).GetNativeMetadataAsync(
            new AttachmentNativeMetadataQuery(attachment.LayerId, attachment.FeatureId, [attachment.Id]),
            context.CancellationToken).ConfigureAwait(false);
        return MapAttachment(attachment, metadata.GetValueOrDefault(attachment.Id));
    }

    private static Proto.AttachmentDescriptor MapAttachment(Attachment value, AttachmentNativeMetadata? metadata)
    {
        var descriptor = new Proto.AttachmentDescriptor
        {
            AttachmentId = value.Id, Name = value.Filename, ContentType = value.ContentType, SizeBytes = value.Size,
            GlobalId = metadata?.GlobalId.ToString("D") ?? string.Empty,
            Sha256 = metadata is null ? ByteString.Empty : ByteString.CopyFrom(metadata.Sha256)
        };
        descriptor.Keywords.AddRange(metadata?.Keywords ?? (string.IsNullOrEmpty(value.Keywords) ? [] : new[] { value.Keywords }));
        return descriptor;
    }

    private static string AttachmentPageScope(Proto.ListAttachmentsRequest request)
    {
        var scope = request.Clone();
        scope.PageToken = string.Empty;
        scope.PageSize = 0;
        return Convert.ToHexString(SHA256.HashData(scope.ToByteArray()));
    }

    private static long ReadAttachmentCursor(string token, string scope)
    {
        if (token.Length == 0)
        {
            return 0;
        }
        if (token.Length <= scope.Length + 1 || !token.StartsWith(scope + ":", StringComparison.Ordinal) ||
            !long.TryParse(token.AsSpan(scope.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw AttachmentError(StatusCode.InvalidArgument, "The attachment page token does not match this request.");
        }
        return id;
    }
}
