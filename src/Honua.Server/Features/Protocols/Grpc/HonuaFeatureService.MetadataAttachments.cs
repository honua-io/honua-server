// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private static Proto.AttachmentLimits MetadataAttachmentLimits(GrpcLayerContext layer, Grpc.Core.ServerCallContext context)
    {
        var result = new Proto.AttachmentLimits();
        if (layer.Resource.Editing?.SupportsAttachments != true)
        {
            return result;
        }
        var options = context.GetHttpContext().RequestServices.GetService<IOptions<LimitsOptions>>()?.Value ?? new();
        var limits = options.Attachments;
        result.MaxFileBytes = Math.Min(options.MaxUploadSizeBytes, limits.MaxAttachmentSize);
        result.MaxAttachmentsPerFeature = limits.MaxAttachmentsPerFeature;
        result.MaxTotalBytesPerFeature = limits.MaxTotalAttachmentSize;
        result.MaxUnaryUploadBytes = Math.Min(result.MaxFileBytes, 1024 * 1024);
        result.MaxChunkBytes = 1024 * 1024;
        result.AllowedContentTypes.AddRange(limits.AllowedMimeTypes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return result;
    }
}
