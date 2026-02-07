// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.MapServer.Rendering;

/// <summary>
/// Handles coordinate transformations between common spatial reference systems.
/// Supports EPSG:4326 (WGS84) and EPSG:3857 (Web Mercator) conversions.
/// </summary>
internal static class CoordinateTransformer
{
    private const double EarthRadius = 6378137.0;
    private const double MaxLatitude = 85.06;

    /// <summary>
    /// Converts a bounding box from one SRID to another.
    /// </summary>
    public static SkiaMapRenderer.RenderExtent TransformExtent(
        SkiaMapRenderer.RenderExtent extent,
        int fromSrid,
        int toSrid)
    {
        if (fromSrid == toSrid)
        {
            return extent;
        }

        // 4326 -> 3857
        if (fromSrid == 4326 && toSrid == 3857)
        {
            var (minX, minY) = LonLatToWebMercator(extent.MinX, extent.MinY);
            var (maxX, maxY) = LonLatToWebMercator(extent.MaxX, extent.MaxY);
            return new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
        }

        // 3857 -> 4326
        if (fromSrid == 3857 && toSrid == 4326)
        {
            var (minLon, minLat) = WebMercatorToLonLat(extent.MinX, extent.MinY);
            var (maxLon, maxLat) = WebMercatorToLonLat(extent.MaxX, extent.MaxY);
            return new SkiaMapRenderer.RenderExtent(minLon, minLat, maxLon, maxLat);
        }

        // Unsupported transform - return as-is
        return extent;
    }

    /// <summary>
    /// Converts a single point from one SRID to another.
    /// </summary>
    public static (double X, double Y) TransformPoint(double x, double y, int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid)
        {
            return (x, y);
        }

        if (fromSrid == 4326 && toSrid == 3857)
        {
            return LonLatToWebMercator(x, y);
        }

        if (fromSrid == 3857 && toSrid == 4326)
        {
            return WebMercatorToLonLat(x, y);
        }

        return (x, y);
    }

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

        // If geographic coordinates, convert width to meters at center latitude
        if (srid == 4326)
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
