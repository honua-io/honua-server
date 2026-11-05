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
