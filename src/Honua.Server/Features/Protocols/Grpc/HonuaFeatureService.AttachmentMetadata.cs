// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private static LimitsOptions AttachmentUploadLimits(ServerCallContext context)
        => context.GetHttpContext().RequestServices.GetService<IOptions<LimitsOptions>>()?.Value ?? new LimitsOptions();

    private static Proto.AttachmentLimits MetadataAttachmentLimits(ServerCallContext context)
    {
        var limits = AttachmentUploadLimits(context);
        var fileBytes = Math.Min(limits.MaxUploadSizeBytes, limits.Attachments.MaxAttachmentSize);
        var result = new Proto.AttachmentLimits
        {
            MaxFileBytes = fileBytes,
            MaxUnaryUploadBytes = Math.Min(fileBytes, AttachmentChunkBytes),
            MaxChunkBytes = AttachmentChunkBytes,
            MaxAttachmentsPerFeature = limits.Attachments.MaxAttachmentsPerFeature,
            MaxTotalBytesPerFeature = limits.Attachments.MaxTotalAttachmentSize
        };
        result.AllowedContentTypes.Add(limits.Attachments.AllowedMimeTypes.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return result;
    }
}
