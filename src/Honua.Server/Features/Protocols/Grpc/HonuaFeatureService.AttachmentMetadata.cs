// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Grpc.AspNetCore.Server;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    // Reserve enough room for the largest accepted upload metadata and the protobuf envelope.
    private const int AttachmentUnaryEnvelopeBytes = 512 * 1024;
    private const int AttachmentChunkEnvelopeBytes = 128;

    private static int AttachmentPayloadLimit(ServerCallContext context, int envelopeBytes)
    {
        var receiveBytes = context.GetHttpContext().RequestServices
            .GetService<IOptions<GrpcServiceOptions>>()?.Value.MaxReceiveMessageSize;
        return receiveBytes is { } maximum
            ? (int)Math.Clamp(maximum - (long)envelopeBytes, 0, AttachmentChunkBytes)
            : AttachmentChunkBytes;
    }

    private static LimitsOptions AttachmentUploadLimits(ServerCallContext context)
        => context.GetHttpContext().RequestServices.GetService<IOptions<LimitsOptions>>()?.Value ?? new LimitsOptions();

    private static Proto.AttachmentLimits MetadataAttachmentLimits(ServerCallContext context)
    {
        var limits = AttachmentUploadLimits(context);
        var fileBytes = Math.Min(limits.MaxUploadSizeBytes, limits.Attachments.MaxAttachmentSize);
        var result = new Proto.AttachmentLimits
        {
            MaxFileBytes = fileBytes,
            MaxUnaryUploadBytes = Math.Min(fileBytes, AttachmentPayloadLimit(context, AttachmentUnaryEnvelopeBytes)),
            MaxChunkBytes = AttachmentPayloadLimit(context, AttachmentChunkEnvelopeBytes),
            MaxAttachmentsPerFeature = limits.Attachments.MaxAttachmentsPerFeature,
            MaxTotalBytesPerFeature = limits.Attachments.MaxTotalAttachmentSize
        };
        result.AllowedContentTypes.Add(limits.Attachments.AllowedMimeTypes.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return result;
    }
}
