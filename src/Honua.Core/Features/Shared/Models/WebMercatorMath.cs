// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Shared Web Mercator projection math used by server and provider transform paths.
/// </summary>
public static class WebMercatorMath
{
    private const double EarthRadius = SpatialConstants.EarthRadius;
    private const double MaxLatitude = SpatialConstants.WebMercatorMaxLatitude;

    /// <summary>
    /// Converts longitude/latitude coordinates to Web Mercator meters.
    /// </summary>
    public static (double X, double Y) LonLatToWebMercator(double longitude, double latitude)
    {
        var clampedLatitude = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        var x = longitude * Math.PI / 180.0 * EarthRadius;
        var y = Math.Log(Math.Tan((90.0 + clampedLatitude) * Math.PI / 360.0)) * EarthRadius;
        return (x, y);
    }

    /// <summary>
    /// Converts Web Mercator meters to longitude/latitude coordinates.
    /// </summary>
    public static (double Lon, double Lat) WebMercatorToLonLat(double x, double y)
    {
        var lon = x / EarthRadius * 180.0 / Math.PI;
        var lat = Math.Atan(Math.Exp(y / EarthRadius)) * 360.0 / Math.PI - 90.0;
        return (lon, lat);
    }

    /// <summary>
    /// Transforms an extent by sampling its edges and midpoint through the supplied transform.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) TransformSampledExtent(
        double minX,
        double minY,
        double maxX,
        double maxY,
        Func<double, double, (double X, double Y)> transform,
        int sampleSegmentsPerEdge)
    {
        var transformedMinX = double.PositiveInfinity;
        var transformedMinY = double.PositiveInfinity;
        var transformedMaxX = double.NegativeInfinity;
        var transformedMaxY = double.NegativeInfinity;

        foreach (var (x, y) in EnumerateSampledExtentPoints(minX, minY, maxX, maxY, sampleSegmentsPerEdge))
        {
            var (tx, ty) = transform(x, y);
            transformedMinX = Math.Min(transformedMinX, tx);
            transformedMinY = Math.Min(transformedMinY, ty);
            transformedMaxX = Math.Max(transformedMaxX, tx);
            transformedMaxY = Math.Max(transformedMaxY, ty);
        }

        return (transformedMinX, transformedMinY, transformedMaxX, transformedMaxY);
    }

    /// <summary>
    /// Enumerates sampled extent edge points with antimeridian-aware longitude interpolation.
    /// </summary>
    public static IEnumerable<(double X, double Y)> EnumerateSampledExtentPoints(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int sampleSegmentsPerEdge)
    {
        for (var i = 0; i <= sampleSegmentsPerEdge; i++)
        {
            var t = (double)i / sampleSegmentsPerEdge;
            var x = InterpolateLongitude(minX, maxX, t);
            var y = minY + ((maxY - minY) * t);

            yield return (x, minY);
            yield return (x, maxY);
            yield return (minX, y);
            yield return (maxX, y);
        }

        yield return (InterpolateLongitude(minX, maxX, 0.5), (minY + maxY) / 2.0);
    }

    /// <summary>
    /// Interpolates longitude values while preserving dateline-crossing extents.
    /// </summary>
    public static double InterpolateLongitude(double minX, double maxX, double t)
    {
        if (!IsAntimeridianCrossing(minX, maxX))
        {
            return minX + ((maxX - minX) * t);
        }

        var wrappedMaxX = maxX + 360.0;
        var value = minX + ((wrappedMaxX - minX) * t);
        return value > 180.0 ? value - 360.0 : value;
    }

    /// <summary>
    /// Returns whether the longitude interval represents an antimeridian crossing.
    /// </summary>
    public static bool IsAntimeridianCrossing(double minX, double maxX)
        => minX > maxX && minX >= -180.0 && minX <= 180.0 && maxX >= -180.0 && maxX <= 180.0;
}
