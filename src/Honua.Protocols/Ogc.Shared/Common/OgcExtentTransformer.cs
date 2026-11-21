// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Rendering;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcExtentTransformer
{
    private const double EarthRadius = SpatialConstants.EarthRadius;
    private const double MaxLatitude = SpatialConstants.WebMercatorMaxLatitude;

    // Feature coordinates are rounded to the server's coordinate precision (8 decimal
    // degrees by default) and a projected box is reprojected again when a client sends
    // the advertised extent back as bbox. One 1e-7 degree (~1 cm) outward step covers
    // that rounding and the forward/inverse disagreement, so bbox=<advertised extent>
    // still contains every feature. Identity CRS84 extents are left exact.
    internal const double ReprojectedExtentMarginDegrees = 1e-7;

    public static bool TryTransformToCrs84(double x, double y, int fromSrid, out (double Lon, double Lat) coordinate)
    {
        if (fromSrid == 4326)
        {
            coordinate = (x, y);
            return true;
        }

        if (IsWebMercatorSrid(fromSrid))
        {
            coordinate = WebMercatorToLonLat(x, y);
            return true;
        }

        coordinate = default;
        return false;
    }

    public static async Task<(double MinLon, double MinLat, double MaxLon, double MaxLat)?> TryTransformExtentToCrs84Async(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int fromSrid,
        ICoordinateTransformService? transformService,
        CancellationToken cancellationToken = default)
    {
        if (fromSrid == 4326)
        {
            return (minX, minY, maxX, maxY);
        }

        try
        {
            var transformed = CoordinateTransformer.TransformExtent(
                new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY),
                fromSrid,
                4326);
            return ExpandReprojectedCrs84(transformed.MinX, transformed.MinY, transformed.MaxX, transformed.MaxY);
        }
        catch (NotSupportedException)
        {
            if (transformService == null)
            {
                return null;
            }

            var transformed = await transformService
                .TransformExtentAsync(minX, minY, maxX, maxY, fromSrid, 4326, cancellationToken)
                .ConfigureAwait(false);
            return transformed is { } box
                ? ExpandReprojectedCrs84(box.MinX, box.MinY, box.MaxX, box.MaxY)
                : null;
        }
    }

    // Grows a geographic box that was produced by reprojecting a storage extent.
    // A dateline-crossing box (minLon > maxLon) keeps its longitude edges; widening
    // those would either unwrap it or swallow the complementary hemisphere.
    internal static (double MinLon, double MinLat, double MaxLon, double MaxLat) ExpandReprojectedCrs84(
        double minLon,
        double minLat,
        double maxLon,
        double maxLat)
    {
        var margin = ReprojectedExtentMarginDegrees;
        var crossesAntimeridian = minLon > maxLon;
        var west = crossesAntimeridian ? minLon : Math.Max(-180d, minLon - margin);
        var east = crossesAntimeridian ? maxLon : Math.Min(180d, maxLon + margin);
        var south = Math.Max(-90d, minLat - margin);
        var north = Math.Min(90d, maxLat + margin);
        return (west, south, east, north);
    }

    private static bool IsWebMercatorSrid(int srid)
        => Honua.Core.Features.Shared.Models.SpatialReferenceExtensions.IsWebMercatorSrid(srid);

    private static (double Lon, double Lat) WebMercatorToLonLat(double x, double y)
    {
        y = Math.Clamp(y, -EarthRadius * Math.PI, EarthRadius * Math.PI);
        var lon = x / EarthRadius * 180.0 / Math.PI;
        var lat = Math.Atan(Math.Exp(y / EarthRadius)) * 360.0 / Math.PI - 90.0;
        lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        return (lon, lat);
    }
}
