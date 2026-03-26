// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.MapServer.Rendering;

/// <summary>
/// Handles coordinate transformations between common spatial reference systems.
/// Supports WGS84 (EPSG:4326) and Web Mercator aliases for in-memory transforms.
/// </summary>
internal static class CoordinateTransformer
{
    private const double EarthRadius = SpatialConstants.EarthRadius;
    private const double MaxLatitude = SpatialConstants.WebMercatorMaxLatitude;

    /// <summary>
    /// Converts a bounding box from one SRID to another.
    /// </summary>
    public static SkiaMapRenderer.RenderExtent TransformExtent(
        SkiaMapRenderer.RenderExtent extent,
        int fromSrid,
        int toSrid)
    {
        if (fromSrid == toSrid ||
            (IsWebMercatorSrid(fromSrid) && IsWebMercatorSrid(toSrid)))
        {
            return extent;
        }

        // geographic -> web mercator
        if (IsWgs84Srid(fromSrid) && IsWebMercatorSrid(toSrid))
        {
            var (minX, minY) = LonLatToWebMercator(extent.MinX, extent.MinY);
            var (maxX, maxY) = LonLatToWebMercator(extent.MaxX, extent.MaxY);
            return new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
        }

        // web mercator -> geographic
        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            var (minLon, minLat) = WebMercatorToLonLat(extent.MinX, extent.MinY);
            var (maxLon, maxLat) = WebMercatorToLonLat(extent.MaxX, extent.MaxY);
            return new SkiaMapRenderer.RenderExtent(minLon, minLat, maxLon, maxLat);
        }

        throw new NotSupportedException(
            $"In-memory coordinate transform from SRID {fromSrid} to {toSrid} is not supported.");
    }

    /// <summary>
    /// Converts a single point from one SRID to another.
    /// </summary>
    public static (double X, double Y) TransformPoint(double x, double y, int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid ||
            (IsWebMercatorSrid(fromSrid) && IsWebMercatorSrid(toSrid)))
        {
            return (x, y);
        }

        if (IsWgs84Srid(fromSrid) && IsWebMercatorSrid(toSrid))
        {
            return LonLatToWebMercator(x, y);
        }

        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            return WebMercatorToLonLat(x, y);
        }

        throw new NotSupportedException(
            $"In-memory coordinate transform from SRID {fromSrid} to {toSrid} is not supported.");
    }

    private static bool IsWebMercatorSrid(int srid)
        => srid is 3857 or 900913 or 102100 or 102113 or 3785;

    private static bool IsWgs84Srid(int srid)
        => srid == 4326;

    /// <summary>
    /// Converts longitude/latitude (EPSG:4326) to Web Mercator (EPSG:3857).
    /// </summary>
    public static (double X, double Y) LonLatToWebMercator(double longitude, double latitude)
    {
        var clampedLat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        var x = longitude * Math.PI / 180.0 * EarthRadius;
        var y = Math.Log(Math.Tan((90.0 + clampedLat) * Math.PI / 360.0)) * EarthRadius;
        return (x, y);
    }

    /// <summary>
    /// Converts Web Mercator (EPSG:3857) to longitude/latitude (EPSG:4326).
    /// </summary>
    public static (double Lon, double Lat) WebMercatorToLonLat(double x, double y)
    {
        var lon = x / EarthRadius * 180.0 / Math.PI;
        var lat = Math.Atan(Math.Exp(y / EarthRadius)) * 360.0 / Math.PI - 90.0;
        return (lon, lat);
    }

    /// <summary>
    /// Calculates the approximate scale denominator for a given extent and image size.
    /// </summary>
    public static double CalculateScaleDenominator(
        SkiaMapRenderer.RenderExtent extent,
        int imageWidth,
        int dpi,
        int srid)
    {
        if (imageWidth <= 0 || dpi <= 0)
        {
            return 0;
        }

        var extentWidth = extent.Width;

        // If geographic coordinates, convert width to meters at center latitude.
        // Most EPSG codes in 4000-4999 are geographic CRSs (lat/lon in degrees).
        if (srid is 4326 or 4269 or 4267 or (>= 4000 and <= 4999))
        {
            var centerLat = (extent.MinY + extent.MaxY) / 2.0;
            extentWidth = extentWidth * Math.PI / 180.0 * EarthRadius * Math.Cos(centerLat * Math.PI / 180.0);
        }

        // pixels per meter at the given DPI (1 inch = 0.0254 meters)
        var pixelsPerMeter = dpi / 0.0254;
        var metersPerPixel = extentWidth / imageWidth;

        return metersPerPixel * pixelsPerMeter;
    }

    /// <summary>
    /// Converts a pixel tolerance to map units for identify operations.
    /// </summary>
    public static double PixelToMapUnits(
        int pixelTolerance,
        SkiaMapRenderer.RenderExtent mapExtent,
        int imageWidth)
    {
        if (imageWidth <= 0)
        {
            return 0;
        }

        return pixelTolerance * mapExtent.Width / imageWidth;
    }
}
