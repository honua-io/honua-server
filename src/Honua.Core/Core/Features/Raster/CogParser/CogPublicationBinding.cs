// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Raster.CogParser;

public static class CogPublicationBinding
{
    // Prefer the single image publication; when none exists, keep the single publication of any type, and fail closed when that choice is ambiguous.
    public static MetadataV2Layer? Resolve(MetadataV2GraphSnapshot snapshot, int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Index.RoutablePublicationsByLayerIndex.TryGetValue(layerIndex, out var publications))
        {
            return null;
        }

        return SelectPublication(publications);
    }

    public static MetadataV2Layer? Resolve(MetadataV2GraphSnapshot snapshot, int layerIndex, string? tenantId)
    {
        var match = Resolve(snapshot, layerIndex);
        if (match is null)
        {
            return null;
        }

        return IsVisibleToTenant(snapshot, match, tenantId) ? match : null;
    }

    private static MetadataV2Layer? SelectPublication(ImmutableArray<MetadataV2Layer> publications)
    {
        MetadataV2Layer? imageMatch = null;
        MetadataV2Layer? anyMatch = null;
        var imageCount = 0;
        var anyCount = 0;
        for (var i = 0; i < publications.Length; i++)
        {
            var publication = publications[i];
            anyCount++;
            anyMatch = publication;
            if (publication.PublicationType == MetadataV2LayerType.EsriImageLayer)
            {
                imageCount++;
                imageMatch = publication;
            }
        }

        return imageCount > 0
            ? (imageCount == 1 ? imageMatch : null)
            : (anyCount == 1 ? anyMatch : null);
    }

    private static bool IsVisibleToTenant(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Layer publication,
        string? tenantId)
    {
        var service = snapshot.Index.ServicesById.GetValueOrDefault(publication.ServiceId);
        var resource = snapshot.ResolveResource(publication);
        return MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, tenantId);
    }
}
