// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.MapServer.Rendering;

/// <summary>
/// Wrapper around CoordinateTransformer that adds performance monitoring.
/// </summary>
/// <remarks>
/// This class provides comprehensive monitoring of coordinate transformation operations
/// to help identify performance bottlenecks in geospatial processing.
/// </remarks>
internal static class MonitoredCoordinateTransformer
{
    /// <summary>
    /// Converts a bounding box from one SRID to another with performance monitoring.
    /// </summary>
    public static SkiaMapRenderer.RenderExtent TransformExtent(
        SkiaMapRenderer.RenderExtent extent,
        int fromSrid,
        int toSrid,
        IPerformanceMonitor? performanceMonitor = null)
    {
        if (fromSrid == toSrid)
        {
            return extent;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = CoordinateTransformer.TransformExtent(extent, fromSrid, toSrid);

            // Record successful transformation
            stopwatch.Stop();
            performanceMonitor?.RecordGeospatialOperation(
                "coordinate_transform",
                stopwatch.Elapsed,
                4, // Bounding box has 4 coordinates (min/max X/Y)
                fromSrid,
                toSrid);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            performanceMonitor?.RecordErrorWithContext(
                "coordinate_transform_error",
                "transform_extent",
                new Dictionary<string, object>
                {
                    ["from_srid"] = fromSrid,
                    ["to_srid"] = toSrid,
                    ["extent"] = extent.ToString()
                },
                ex);

            throw;
        }
    }

    /// <summary>
    /// Converts a single point from one SRID to another with performance monitoring.
    /// </summary>
    public static (double X, double Y) TransformPoint(
        double x, double y,
        int fromSrid,
        int toSrid,
        IPerformanceMonitor? performanceMonitor = null)
    {
        if (fromSrid == toSrid)
        {
            return (x, y);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = CoordinateTransformer.TransformPoint(x, y, fromSrid, toSrid);

            // Record successful transformation
            stopwatch.Stop();
            performanceMonitor?.RecordGeospatialOperation(
                "coordinate_transform",
                stopwatch.Elapsed,
                1, // Single point has 1 coordinate pair
                fromSrid,
                toSrid);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            performanceMonitor?.RecordErrorWithContext(
                "coordinate_transform_error",
                "transform_point",
                new Dictionary<string, object>
                {
                    ["from_srid"] = fromSrid,
                    ["to_srid"] = toSrid,
                    ["point"] = $"({x}, {y})"
                },
                ex);

            throw;
        }
    }

    /// <summary>
    /// Converts longitude/latitude to Web Mercator with monitoring.
    /// </summary>
    public static (double X, double Y) LonLatToWebMercator(
        double longitude, double latitude,
        IPerformanceMonitor? performanceMonitor = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = CoordinateTransformer.LonLatToWebMercator(longitude, latitude);

            stopwatch.Stop();
            performanceMonitor?.RecordGeospatialOperation(
                "coordinate_transform",
                stopwatch.Elapsed,
                1,
                4326, // WGS84
                3857  // Web Mercator
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            performanceMonitor?.RecordErrorWithContext(
                "coordinate_transform_error",
                "lonlat_to_webmercator",
                new Dictionary<string, object>
                {
                    ["longitude"] = longitude,
                    ["latitude"] = latitude
                },
                ex);

            throw;
        }
    }

    /// <summary>
    /// Converts Web Mercator to longitude/latitude with monitoring.
    /// </summary>
    public static (double Lon, double Lat) WebMercatorToLonLat(
        double x, double y,
        IPerformanceMonitor? performanceMonitor = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = CoordinateTransformer.WebMercatorToLonLat(x, y);

            stopwatch.Stop();
            performanceMonitor?.RecordGeospatialOperation(
                "coordinate_transform",
                stopwatch.Elapsed,
                1,
                3857, // Web Mercator
                4326  // WGS84
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            performanceMonitor?.RecordErrorWithContext(
                "coordinate_transform_error",
                "webmercator_to_lonlat",
                new Dictionary<string, object>
                {
                    ["x"] = x,
                    ["y"] = y
                },
                ex);

            throw;
        }
    }

    /// <summary>
    /// Calculates scale denominator with monitoring.
    /// </summary>
    public static double CalculateScaleDenominator(
        SkiaMapRenderer.RenderExtent extent,
        int imageWidth,
        int dpi,
        int srid,
        IPerformanceMonitor? performanceMonitor = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = CoordinateTransformer.CalculateScaleDenominator(extent, imageWidth, dpi, srid);

            stopwatch.Stop();
            performanceMonitor?.RecordGeospatialOperation(
                "spatial_calculation",
                stopwatch.Elapsed,
                1,
                srid);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            performanceMonitor?.RecordErrorWithContext(
                "spatial_calculation_error",
                "calculate_scale_denominator",
                new Dictionary<string, object>
                {
                    ["extent"] = extent.ToString(),
                    ["image_width"] = imageWidth,
                    ["dpi"] = dpi,
                    ["srid"] = srid
                },
                ex);

            throw;
        }
    }

    /// <summary>
    /// Converts pixel tolerance to map units with monitoring.
    /// </summary>
    public static double PixelToMapUnits(
        int pixelTolerance,
        SkiaMapRenderer.RenderExtent mapExtent,
        int imageWidth,
        IPerformanceMonitor? performanceMonitor = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = CoordinateTransformer.PixelToMapUnits(pixelTolerance, mapExtent, imageWidth);

            stopwatch.Stop();
            performanceMonitor?.RecordGeospatialOperation(
                "spatial_calculation",
                stopwatch.Elapsed,
                1);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            performanceMonitor?.RecordErrorWithContext(
                "spatial_calculation_error",
                "pixel_to_map_units",
                new Dictionary<string, object>
                {
                    ["pixel_tolerance"] = pixelTolerance,
                    ["map_extent"] = mapExtent.ToString(),
                    ["image_width"] = imageWidth
                },
                ex);

            throw;
        }
    }
}
