// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.VectorTileServer.Models;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

/// <summary>
/// Resolves the VectorTileServer <c>fullExtent</c>/<c>initialExtent</c> in the spatial
/// reference of the tiling scheme the service advertises in <c>tileInfo</c>.
/// </summary>
/// <remarks>
/// The tiling scheme is always Web Mercator (<see cref="VectorTileServerTileInfoBuilder"/>),
/// while each published resource declares its bbox in its own spatial reference. Clients
/// such as ArcGIS Pro discard an extent whose spatial reference differs from the tiling
/// scheme's, so every resource bbox is projected through the shared
/// <see cref="ICoordinateTransformService"/> before the union is taken. A bbox that cannot be
/// projected is left out rather than reported with a spatial reference its numbers are not in
/// (honua-server#5015).
/// </remarks>
internal static class VectorTileServerExtentResolver
{
    private const int DefaultSrid = 4326;

    /// <summary>
    /// Unions the resource bboxes, projected into <paramref name="tilingSpatialReference"/>.
    /// </summary>
    /// <param name="resources">The visible resources published by the service.</param>
    /// <param name="serviceSpatialReference">
    /// The service's declared spatial reference, used for a resource that declares a bbox but
    /// no spatial reference of its own.
    /// </param>
    /// <param name="tilingSpatialReference">The tiling scheme's spatial reference.</param>
    /// <param name="transformService">The shared coordinate transform service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extent in the tiling scheme's spatial reference, or <see langword="null"/>
    /// when no resource bbox could be expressed in it.</returns>
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
