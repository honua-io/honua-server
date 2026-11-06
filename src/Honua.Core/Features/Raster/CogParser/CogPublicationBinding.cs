// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Raster.CogParser;

public static class CogPublicationBinding
{
    // Prefer the single image publication; when none exists, keep the single publication of any type, and fail closed when that choice is ambiguous.
    public static MetadataV2Publication? Resolve(MetadataV2GraphSnapshot snapshot, int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        MetadataV2Publication? imageMatch = null;
        MetadataV2Publication? anyMatch = null;
        var imageCount = 0;
        var anyCount = 0;
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex != layerIndex || !snapshot.IsRoutable(publication))
            {
                continue;
            }

            anyCount++;
            anyMatch = publication;
            if (publication.PublicationType == MetadataV2PublicationType.EsriImageLayer)
            {
                imageCount++;
                imageMatch = publication;
            }
        }

        if (imageCount > 0)
        {
            return imageCount == 1 ? imageMatch : null;
        }

        return anyCount == 1 ? anyMatch : null;
    }
}
