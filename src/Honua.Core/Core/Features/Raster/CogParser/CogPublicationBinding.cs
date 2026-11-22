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
        var byStorage = CollectMatches(snapshot, publication => snapshot.ResolveStorageLayerId(publication) == layerId);
        if (byStorage.AnyCount > 0)
        {
            return SelectUnique(byStorage);
        }

        var byIndex = CollectMatches(snapshot, publication => publication.LayerIndex == layerId);
        return SelectUnique(byIndex);
    }

    private readonly record struct BindingMatches(
        MetadataV2Layer? ImageMatch,
        int ImageCount,
        MetadataV2Layer? AnyMatch,
        int AnyCount);

    private static BindingMatches CollectMatches(
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

        return new BindingMatches(imageMatch, imageCount, anyMatch, anyCount);
    }

    private static MetadataV2Layer? SelectUnique(BindingMatches matches)
        => matches.ImageCount > 0
            ? (matches.ImageCount == 1 ? matches.ImageMatch : null)
            : (matches.AnyCount == 1 ? matches.AnyMatch : null);
}
