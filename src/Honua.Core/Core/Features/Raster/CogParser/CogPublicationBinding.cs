// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Raster.CogParser;

public static class CogPublicationBinding
{
    // Resolve by storage layer id first; fall back to layer index only when no storage binding matches.
    // Within each pass prefer the single image publication, else the single publication of any type, failing closed on ambiguity.
    public static MetadataV2Layer? Resolve(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var byStorage = ResolveByPredicate(snapshot, publication => snapshot.ResolveStorageLayerId(publication) == layerId);
        if (byStorage.MatchedAny)
        {
            return byStorage.Publication;
        }

        return ResolveByPredicate(snapshot, publication => publication.LayerIndex == layerId).Publication;
    }

    private static (MetadataV2Layer? Publication, bool MatchedAny) ResolveByPredicate(
        MetadataV2GraphSnapshot snapshot,
        Func<MetadataV2Layer, bool> predicate)
    {
        MetadataV2Layer? imageMatch = null;
        MetadataV2Layer? anyMatch = null;
        var imageCount = 0;
        var anyCount = 0;
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (!snapshot.IsRoutable(publication) || !predicate(publication))
            {
                continue;
            }

            anyCount++;
            anyMatch = publication;
            if (publication.PublicationType == MetadataV2LayerType.EsriImageLayer)
            {
                imageCount++;
                imageMatch = publication;
            }
        }

        if (imageCount > 0)
        {
            return (imageCount == 1 ? imageMatch : null, true);
        }

        return (anyCount == 1 ? anyMatch : null, anyCount > 0);
    }
}
