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
    private const double GeographicWorldWidth = 360.0;
    private const int ExtentSampleSegmentsPerEdge = 4;
    private static readonly double WebMercatorWorldHalfWidth = Math.PI * EarthRadius;
    private static readonly double WebMercatorWorldWidth = WebMercatorWorldHalfWidth * 2.0;

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
            return TransformSampledExtent(extent, LonLatToWebMercator);
        }

        // web mercator -> geographic
        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            return TransformSampledExtent(extent, WebMercatorToLonLat);
        }

        throw new NotSupportedException(
            $"In-memory coordinate transform from SRID {fromSrid} to {toSrid} is not supported.");
    }

    /// <summary>
    /// Converts a shared render extent from one SRID to another.
    /// </summary>
    public static global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent TransformExtent(
        global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent extent,
        int fromSrid,
        int toSrid)
    {
        var transformed = TransformExtent(ToRendererExtent(extent), fromSrid, toSrid);
        return ToSharedExtent(transformed);
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

    internal static bool IsWrappedGeographicExtent(SkiaMapRenderer.RenderExtent extent)
    {
        return extent.MinX > extent.MaxX &&
            extent.MinX >= -180.0 && extent.MinX <= 180.0 &&
            extent.MaxX >= -180.0 && extent.MaxX <= 180.0 &&
            extent.MinY >= -90.0 && extent.MinY <= 90.0 &&
            extent.MaxY >= -90.0 && extent.MaxY <= 90.0;
    }

    internal static bool IsWrappedWebMercatorExtent(SkiaMapRenderer.RenderExtent extent)
    {
        return extent.MinX > extent.MaxX &&
            extent.MinX >= -WebMercatorWorldHalfWidth && extent.MinX <= WebMercatorWorldHalfWidth &&
            extent.MaxX >= -WebMercatorWorldHalfWidth && extent.MaxX <= WebMercatorWorldHalfWidth &&
            extent.MinY >= -WebMercatorWorldHalfWidth && extent.MinY <= WebMercatorWorldHalfWidth &&
            extent.MaxY >= -WebMercatorWorldHalfWidth && extent.MaxY <= WebMercatorWorldHalfWidth;
    }

    private static bool TryGetWrappedWorldWidth(SkiaMapRenderer.RenderExtent extent, out double worldWidth)
    {
        if (IsWrappedGeographicExtent(extent))
        {
            worldWidth = GeographicWorldWidth;
            return true;
        }

        if (IsWrappedWebMercatorExtent(extent))
        {
            worldWidth = WebMercatorWorldWidth;
            return true;
        }

        worldWidth = 0;
        return false;
    }

    internal static double GetEffectiveWidth(SkiaMapRenderer.RenderExtent extent)
    {
        if (!TryGetWrappedWorldWidth(extent, out var worldWidth))
        {
            return extent.Width;
        }

        return worldWidth - extent.MinX + extent.MaxX;
    }

    internal static double NormalizeLongitude(double longitude, SkiaMapRenderer.RenderExtent extent)
    {
        if (!TryGetWrappedWorldWidth(extent, out var worldWidth))
        {
            return longitude;
        }

        var normalized = longitude;
        if (normalized < extent.MinX)
        {
            var wraps = Math.Ceiling((extent.MinX - normalized) / worldWidth);
            normalized += wraps * worldWidth;
        }

        return normalized;
    }

    // Defer to the shared SRID classifier so scale math stays aligned with the
    // rest of the server and geocentric CRS codes such as EPSG:4978 are not
    // misclassified as lat/lon.
    private static bool IsGeographicSrid(int srid)
        => SpatialReference.Create(srid).IsGeographic;

    private static SkiaMapRenderer.RenderExtent TransformSampledExtent(
        SkiaMapRenderer.RenderExtent extent,
        Func<double, double, (double X, double Y)> transform)
    {
        var wrapped = TryGetWrappedWorldWidth(extent, out _);
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var (x, y) in EnumerateSampledExtentPoints(extent))
        {
            var (tx, ty) = transform(x, y);
            minY = Math.Min(minY, ty);
            maxY = Math.Max(maxY, ty);
            if (!wrapped)
            {
                minX = Math.Min(minX, tx);
                maxX = Math.Max(maxX, tx);
            }
        }

        if (wrapped)
        {
            var (wrappedMinX, _) = transform(extent.MinX, extent.MinY);
            var (wrappedMaxX, _) = transform(extent.MaxX, extent.MinY);
            minX = wrappedMinX;
            maxX = wrappedMaxX;
        }

        return new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
    }

    private static IEnumerable<(double X, double Y)> EnumerateSampledExtentPoints(SkiaMapRenderer.RenderExtent extent)
    {
        if (TryGetWrappedWorldBounds(extent, out var worldMinX, out var worldMaxX))
        {
            foreach (var point in EnumerateSampledExtentPointsForRange(extent.MinX, worldMaxX, extent.MinY, extent.MaxY))
            {
                yield return point;
            }

            foreach (var point in EnumerateSampledExtentPointsForRange(worldMinX, extent.MaxX, extent.MinY, extent.MaxY))
            {
                yield return point;
            }

            yield break;
        }

        foreach (var point in EnumerateSampledExtentPointsForRange(extent.MinX, extent.MaxX, extent.MinY, extent.MaxY))
        {
            yield return point;
        }
    }

    private static bool TryGetWrappedWorldBounds(SkiaMapRenderer.RenderExtent extent, out double worldMinX, out double worldMaxX)
    {
        if (IsWrappedGeographicExtent(extent))
        {
            worldMinX = -180.0;
            worldMaxX = 180.0;
            return true;
        }

        if (IsWrappedWebMercatorExtent(extent))
        {
            worldMinX = -WebMercatorWorldHalfWidth;
            worldMaxX = WebMercatorWorldHalfWidth;
            return true;
        }

        worldMinX = 0;
        worldMaxX = 0;
        return false;
    }

    private static IEnumerable<(double X, double Y)> EnumerateSampledExtentPointsForRange(
        double minX,
        double maxX,
        double minY,
        double maxY)
    {
        for (var i = 0; i <= ExtentSampleSegmentsPerEdge; i++)
        {
            var t = (double)i / ExtentSampleSegmentsPerEdge;
            var x = minX + ((maxX - minX) * t);
            var y = minY + ((maxY - minY) * t);

            yield return (x, minY);
            yield return (x, maxY);
            yield return (minX, y);
            yield return (maxX, y);
        }

        yield return ((minX + maxX) / 2.0, (minY + maxY) / 2.0);
    }

    /// <summary>
    /// Converts longitude/latitude (EPSG:4326) to Web Mercator (EPSG:3857).
    /// </summary>
    public static (double X, double Y) LonLatToWebMercator(double longitude, double latitude)
        => WebMercatorMath.LonLatToWebMercator(longitude, latitude);

    /// <summary>
    /// Converts Web Mercator (EPSG:3857) to longitude/latitude (EPSG:4326).
    /// </summary>
    public static (double Lon, double Lat) WebMercatorToLonLat(double x, double y)
        => WebMercatorMath.WebMercatorToLonLat(x, y);

    /// <summary>
    /// Adjusts an extent to match a requested scale denominator, keeping the center point fixed.
    /// This is the inverse of <see cref="CalculateScaleDenominator(SkiaMapRenderer.RenderExtent, int, int, int)"/>: given a target scale,
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

        var centerX = TryGetWrappedWorldWidth(extent, out _)
            ? extent.MinX + GetEffectiveWidth(extent) / 2.0
            : (extent.MinX + extent.MaxX) / 2.0;
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

        var adjusted = new SkiaMapRenderer.RenderExtent(
            centerX - halfWidth, centerY - halfHeight,
            centerX + halfWidth, centerY + halfHeight);

        return IsGeographicSrid(srid)
            ? NormalizeGeographicExtent(adjusted)
            : adjusted;
    }

    /// <summary>
    /// Adjusts a shared render extent to match a requested scale denominator.
    /// </summary>
    public static global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent AdjustExtentForScale(
        global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent extent,
        double scaleDenominator,
        int imageWidth,
        int imageHeight,
        int dpi,
        int srid)
    {
        var adjusted = AdjustExtentForScale(ToRendererExtent(extent), scaleDenominator, imageWidth, imageHeight, dpi, srid);
        return ToSharedExtent(adjusted);
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
            extentWidthMeters = GetEffectiveWidth(extent) * Math.PI / 180.0 * EarthRadius * Math.Cos(centerLat * Math.PI / 180.0);
        }
        else
        {
            // Convert projected units to meters (handles foot-based CRSs)
            extentWidthMeters = GetEffectiveWidth(extent) * LinearUnitToMeters(srid);
        }

        // pixels per meter at the given DPI (1 inch = 0.0254 meters)
        var pixelsPerMeter = dpi / 0.0254;
        var metersPerPixel = extentWidthMeters / imageWidth;

        return metersPerPixel * pixelsPerMeter;
    }

    /// <summary>
    /// Calculates the approximate scale denominator for a shared render extent.
    /// </summary>
    public static double CalculateScaleDenominator(
        global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent extent,
        int imageWidth,
        int dpi,
        int srid)
        => CalculateScaleDenominator(ToRendererExtent(extent), imageWidth, dpi, srid);

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

        return pixelTolerance * GetEffectiveWidth(mapExtent) / imageWidth;
    }

    /// <summary>
    /// Converts a pixel tolerance to map units for identify operations on a shared render extent.
    /// </summary>
    public static double PixelToMapUnits(
        int pixelTolerance,
        global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent mapExtent,
        int imageWidth)
        => PixelToMapUnits(pixelTolerance, ToRendererExtent(mapExtent), imageWidth);

    /// <summary>
    /// Returns the meters-per-linear-unit factor for common projected CRS codes.
    /// Falls back to 1.0 (meters) for unrecognized SRIDs.
    /// </summary>
    /// <remarks>
    /// This is a best-effort static lookup covering common US State Plane foot zones.
    /// The canonical source is the EPSG registry (<c>spatial_ref_sys</c> in PostGIS).
    /// A future enhancement could resolve the unit from the database when available;
    /// this static fallback keeps the rendering path free of I/O.
    /// </remarks>
    internal static double LinearUnitToMeters(int srid)
    {
        // US Survey Foot = 1200/3937 meters (used by NAD83 State Plane zones)
        const double usSurveyFoot = 1200.0 / 3937.0;

        return srid switch
        {
            // NAD83 US State Plane zones in US survey feet (EPSG 2222-2281)
            >= 2222 and <= 2281 => usSurveyFoot,
            // NAD83(HARN) US State Plane in US survey feet (EPSG 2867-2885)
            // Note: HARN foot zones extend beyond this sub-range within 2759-2983;
            // additional HARN foot codes outside this window fall back to 1.0 (meters).
            >= 2867 and <= 2885 => usSurveyFoot,
            // NAD83 / Indiana East & West (ftUS)
            2965 or 2966 => usSurveyFoot,
            // NAD83 / Arkansas, Illinois, New Hampshire, Rhode Island (ftUS)
            >= 3433 and <= 3438 => usSurveyFoot,
            // NAD83(NSRS2007) State Plane in US survey feet (EPSG 3451-3466)
            >= 3451 and <= 3466 => usSurveyFoot,
            // NAD83(2011) State Plane in US survey feet (EPSG 6425-6506)
            >= 6425 and <= 6506 => usSurveyFoot,
            // All other projected CRSs assumed meters (UTM, Web Mercator, etc.)
            _ => 1.0
        };
    }

    private static SkiaMapRenderer.RenderExtent ToRendererExtent(
        global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent extent)
        => new(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);

    private static global::Honua.Server.Features.Infrastructure.Rendering.RenderExtent ToSharedExtent(
        SkiaMapRenderer.RenderExtent extent)
        => new(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);

    private static SkiaMapRenderer.RenderExtent NormalizeGeographicExtent(SkiaMapRenderer.RenderExtent extent)
    {
        var minX = extent.MinX;
        var maxX = extent.MaxX;

        if (maxX > 180.0)
        {
            maxX -= 360.0;
        }
        else if (minX < -180.0)
        {
            minX += 360.0;
        }

        return new SkiaMapRenderer.RenderExtent(minX, extent.MinY, maxX, extent.MaxY);
    }
}
