// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.VectorTileServer.Models;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

// Every resource bbox is projected into the Web Mercator tiling scheme, and a bbox that cannot be projected is left out, because ArcGIS Pro discards an extent in any other spatial reference.
internal static class VectorTileServerExtentResolver
{
    private const int DefaultSrid = 4326;

    public static async Task<VectorTileExtent?> ResolveAsync(
        IEnumerable<MetadataV2Resource> resources,
        MetadataV2SpatialReference? serviceSpatialReference,
        VectorTileSpatialReference tilingSpatialReference,
        ICoordinateTransformService transformService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(tilingSpatialReference);
        ArgumentNullException.ThrowIfNull(transformService);

        var tilingSrid = tilingSpatialReference.LatestWkid ?? tilingSpatialReference.Wkid;
        var fallbackSrid = serviceSpatialReference?.ResolveSrid() ?? DefaultSrid;

        double? xmin = null;
        double? ymin = null;
        double? xmax = null;
        double? ymax = null;

        foreach (var resource in resources)
        {
            var bbox = resource.ReadBbox();
            if (bbox is null)
            {
                continue;
            }

            var projected = await transformService.TransformExtentAsync(
                bbox.West,
                bbox.South,
                bbox.East,
                bbox.North,
                resource.ReadSrid() ?? fallbackSrid,
                tilingSrid,
                cancellationToken).ConfigureAwait(false);
            if (projected is not { } extent)
            {
                continue;
            }

            xmin = xmin.HasValue ? Math.Min(xmin.Value, extent.MinX) : extent.MinX;
            ymin = ymin.HasValue ? Math.Min(ymin.Value, extent.MinY) : extent.MinY;
            xmax = xmax.HasValue ? Math.Max(xmax.Value, extent.MaxX) : extent.MaxX;
            ymax = ymax.HasValue ? Math.Max(ymax.Value, extent.MaxY) : extent.MaxY;
        }

        if (!(xmin.HasValue && ymin.HasValue && xmax.HasValue && ymax.HasValue))
        {
            return null;
        }

        return new VectorTileExtent
        {
            Xmin = xmin.Value,
            Ymin = ymin.Value,
            Xmax = xmax.Value,
            Ymax = ymax.Value,
            SpatialReference = new VectorTileSpatialReference
            {
                Wkid = tilingSpatialReference.Wkid,
                LatestWkid = tilingSpatialReference.LatestWkid
            }
        };
    }
}
