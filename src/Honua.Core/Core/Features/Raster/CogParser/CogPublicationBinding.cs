// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Raster.CogParser;

public static class CogPublicationBinding
{
    // Prefer the single image publication; when none exists, keep the single publication of any type, and fail closed when that choice is ambiguous.
    public static MetadataV2Layer? Resolve(MetadataV2GraphSnapshot snapshot, int layerIndex)
        => ResolveCore(snapshot, layerIndex, applyTenantFilter: false, tenantId: null);

    public static MetadataV2Layer? Resolve(MetadataV2GraphSnapshot snapshot, int layerIndex, string? tenantId)
        => ResolveCore(snapshot, layerIndex, applyTenantFilter: true, tenantId);

    private static MetadataV2Layer? ResolveCore(
        MetadataV2GraphSnapshot snapshot,
        int layerIndex,
        bool applyTenantFilter,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Index.RoutablePublicationsByLayerIndex.TryGetValue(layerIndex, out var publications))
        {
            return null;
        }

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

        var match = imageCount > 0
            ? (imageCount == 1 ? imageMatch : null)
            : (anyCount == 1 ? anyMatch : null);
        if (match is null)
        {
            return null;
        }

        if (applyTenantFilter)
        {
            var service = snapshot.Index.ServicesById.GetValueOrDefault(match.ServiceId);
            var resource = snapshot.ResolveResource(match);
            if (!MetadataV2TenantVisibility.IsVisibleToTenant(match, resource, service, tenantId))
            {
                return null;
            }
        }

        return match;
    }
}
