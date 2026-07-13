// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Rendering;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// Transforms extent coordinates to CRS84 for OGC API spec compliance.
/// </summary>
internal static class OgcExtentTransformer
{
    private const double EarthRadius = SpatialConstants.EarthRadius;
    private const double MaxLatitude = SpatialConstants.WebMercatorMaxLatitude;

    /// <summary>
    /// Transforms a coordinate pair to CRS84 (lon/lat in degrees).
    /// Returns <c>false</c> when a reliable in-memory transform is not available.
    /// </summary>
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
            return (transformed.MinX, transformed.MinY, transformed.MaxX, transformed.MaxY);
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
            return transformed.HasValue
                ? (transformed.Value.MinX, transformed.Value.MinY, transformed.Value.MaxX, transformed.Value.MaxY)
                : null;
        }
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
