// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Infrastructure.Rendering;

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

    // Approximation: all geographic CRSs in the 4000-4999 range are treated
    // as WGS 84 (R = 6 378 137 m) for scale and extent calculations.  SRIDs that
    // use other ellipsoids (e.g. EPSG:4267 / NAD 27 on Clarke 1866) introduce a
    // sub-metre-per-degree error that is negligible for print-service rendering.
    private static bool IsGeographicSrid(int srid)
        => srid is 4326 or 4269 or 4267 or (>= 4000 and <= 4999);

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
    /// Adjusts an extent to match a requested scale denominator, keeping the center point fixed.
    /// This is the inverse of <see cref="CalculateScaleDenominator"/>: given a target scale,
    /// it computes the extent width/height in map units that corresponds to that scale
    /// at the given output dimensions and DPI.
    /// </summary>
    public static SkiaMapRenderer.RenderExtent AdjustExtentForScale(
        SkiaMapRenderer.RenderExtent extent,
        double scaleDenominator,
        int imageWidth,
        int imageHeight,
        int dpi,
        int srid)
    {
        if (scaleDenominator <= 0 || imageWidth <= 0 || imageHeight <= 0 || dpi <= 0)
            return extent;

        var centerX = (extent.MinX + extent.MaxX) / 2.0;
        var centerY = (extent.MinY + extent.MaxY) / 2.0;

        // Inverse of: scaleDenom = (extentWidthMeters / imageWidth) * (dpi / 0.0254)
        var widthMeters = scaleDenominator * imageWidth * 0.0254 / dpi;
        var heightMeters = scaleDenominator * imageHeight * 0.0254 / dpi;

        double halfWidth, halfHeight;

        if (IsGeographicSrid(srid))
        {
            var cosLat = Math.Cos(centerY * Math.PI / 180.0);
            var metersPerDegreeX = Math.PI / 180.0 * EarthRadius * Math.Max(cosLat, 1e-10);
            var metersPerDegreeY = Math.PI / 180.0 * EarthRadius;
            halfWidth = widthMeters / metersPerDegreeX / 2.0;
            halfHeight = heightMeters / metersPerDegreeY / 2.0;
        }
        else
        {
            var unitFactor = LinearUnitToMeters(srid);
            halfWidth = widthMeters / unitFactor / 2.0;
            halfHeight = heightMeters / unitFactor / 2.0;
        }

        return new SkiaMapRenderer.RenderExtent(
            centerX - halfWidth, centerY - halfHeight,
            centerX + halfWidth, centerY + halfHeight);
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

        var extentWidthMeters = extent.Width;

        // If geographic coordinates, convert width to meters at center latitude.
        if (IsGeographicSrid(srid))
        {
            var centerLat = (extent.MinY + extent.MaxY) / 2.0;
            extentWidthMeters = extent.Width * Math.PI / 180.0 * EarthRadius * Math.Cos(centerLat * Math.PI / 180.0);
        }
        else
        {
            // Convert projected units to meters (handles foot-based CRSs)
            extentWidthMeters = extent.Width * LinearUnitToMeters(srid);
        }

        // pixels per meter at the given DPI (1 inch = 0.0254 meters)
        var pixelsPerMeter = dpi / 0.0254;
        var metersPerPixel = extentWidthMeters / imageWidth;

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

    /// <summary>
    /// Returns the meters-per-linear-unit factor for common projected CRS codes.
    /// Falls back to 1.0 (meters) for unrecognized SRIDs.
    /// </summary>
    internal static double LinearUnitToMeters(int srid)
    {
        // US Survey Foot = 1200/3937 meters (used by NAD83 State Plane zones)
        const double usSurveyFoot = 1200.0 / 3937.0;

        return srid switch
        {
            // NAD83 US State Plane zones in US survey feet (EPSG 2222-2281)
            >= 2222 and <= 2281 => usSurveyFoot,
            // NAD83(HARN) US State Plane in US survey feet (EPSG 2867-2885)
            >= 2867 and <= 2885 => usSurveyFoot,
            // NAD83 / Indiana East & West (ftUS)
            2965 or 2966 => usSurveyFoot,
            // NAD83 / Louisiana, Maine, South Dakota (ftUS)
            >= 3433 and <= 3438 => usSurveyFoot,
            // All other projected CRSs assumed meters (UTM, Web Mercator, etc.)
            _ => 1.0
        };
    }
}
