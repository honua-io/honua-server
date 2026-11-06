// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Raster.CogParser;

public static class CogPublicationBinding
{
    // Only an image publication can back a COG, and two image publications on the same index still fail closed.
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
