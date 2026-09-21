// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// Cohesive canonical raster backend for WCS coverage metadata and pixel reads.
/// </summary>
internal sealed class Wcs20CoverageBackend(
    IRasterStore rasterStore,
    IZarrStore zarrStore,
    IZarrRasterSliceReader zarrRasterSliceReader,
    ICoordinateTransformService coordinateTransformService)
{
    internal Task<RasterInfo?> GetPrimaryRasterInfoAsync(int layerId, CancellationToken cancellationToken)
        => rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);

    internal Task<RasterExtent?> GetExtentAsync(int layerId, long rasterId, CancellationToken cancellationToken)
        => rasterStore.GetExtentAsync(layerId, rasterId, cancellationToken);

    internal async ValueTask<Geometry> TransformClipRegionAsync(
        RasterClipRegion clip,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        // Transform the same rectangle vertices used by the raster backend's
        // ST_Transform/ST_Clip, not its enclosing (potentially larger) envelope.
        var polygon = (Polygon)new WKBReader().Read(clip.Geometry);
        var coordinates = polygon.ExteriorRing.CoordinateSequence;
        var xs = new double[coordinates.Count];
        var ys = new double[coordinates.Count];
        for (var index = 0; index < coordinates.Count; index++)
        {
            xs[index] = coordinates.GetX(index);
            ys[index] = coordinates.GetY(index);
        }

        var sourceSrid = clip.Srid ?? throw new InvalidOperationException("Spatial subset has no CRS.");
        if (!await coordinateTransformService.TransformPointsAsync(xs, ys, sourceSrid, targetSrid, cancellationToken)
                .ConfigureAwait(false))
        {
            // A service failure does not prove that the user's subset is invalid.
            throw new InvalidOperationException("Could not transform the coverage subset.");
        }

        for (var index = 0; index < coordinates.Count; index++)
        {
            if (!double.IsFinite(xs[index]) || !double.IsFinite(ys[index]))
            {
                throw new InvalidOperationException("Coverage subset transform returned non-finite coordinates.");
            }

            coordinates.SetX(index, xs[index]);
            coordinates.SetY(index, ys[index]);
        }

        polygon.SRID = targetSrid;
        polygon.GeometryChanged();
        return polygon;
    }

    internal Task<RasterResult> ExportImageAsync(
        int layerId,
        long rasterId,
        RasterQuery query,
        CancellationToken cancellationToken)
        => rasterStore.ExportImageAsync(layerId, rasterId, query, cancellationToken);

    internal Task<ZarrRegistration[]> ListZarrRegistrationsAsync(
        int layerId,
        CancellationToken cancellationToken)
        => zarrStore.ListByLayerAsync(layerId, cancellationToken);

    internal Task<ZarrRasterSliceReadResult> ReadZarrSliceAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        CancellationToken cancellationToken)
        // The classic WCS GetCoverage path serves native-CRS slices only (transformed
        // WCS Zarr output is tracked as a follow-up under #2717); no transform service is
        // threaded here.
        => zarrRasterSliceReader.ReadAsync(layerId, request, cancellationToken: cancellationToken);
}
