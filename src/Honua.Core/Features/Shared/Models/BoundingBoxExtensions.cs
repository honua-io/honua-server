// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Extension methods for BoundingBox conversions and utilities
/// </summary>
public static class BoundingBoxExtensions
{
    /// <summary>
    /// Converts BoundingBox to FeatureServer ExtentInfo format (protocol-specific names)
    /// </summary>
    /// <param name="boundingBox">Unified bounding box</param>
    /// <param name="spatialReference">Spatial reference for the extent</param>
    /// <returns>Object with FeatureServer-specific property names</returns>
    public static object ToFeatureServerExtent(this BoundingBox boundingBox, SpatialReference? spatialReference = null)
        => new
        {
            Xmin = boundingBox.MinX,
            Ymin = boundingBox.MinY,
            Xmax = boundingBox.MaxX,
            Ymax = boundingBox.MaxY,
            SpatialReference = spatialReference ?? (boundingBox.SpatialReferenceId.HasValue
                ? SpatialReference.Create(boundingBox.SpatialReferenceId.Value)
                : SpatialReference.WGS84)
        };

    /// <summary>
    /// Creates BoundingBox from FeatureServer ExtentInfo format
    /// </summary>
    /// <param name="xmin">Minimum X coordinate</param>
    /// <param name="ymin">Minimum Y coordinate</param>
    /// <param name="xmax">Maximum X coordinate</param>
    /// <param name="ymax">Maximum Y coordinate</param>
    /// <param name="spatialReference">Optional spatial reference</param>
    /// <returns>Unified BoundingBox</returns>
    public static BoundingBox FromFeatureServerExtent(double xmin, double ymin, double xmax, double ymax, SpatialReference? spatialReference = null)
        => BoundingBox.Create(xmin, ymin, xmax, ymax, spatialReference?.Wkid);

    /// <summary>
    /// Converts BoundingBox to OGC spatial extent format (nested arrays)
    /// </summary>
    /// <param name="boundingBox">Unified bounding box</param>
    /// <param name="crs">Optional CRS identifier, defaults to CRS84</param>
    /// <returns>Object with OGC-specific format</returns>
    public static object ToOgcSpatialExtent(this BoundingBox boundingBox, string? crs = null)
    {
        var bbox = ImmutableArray.Create(ImmutableArray.Create(
            boundingBox.MinX, boundingBox.MinY, boundingBox.MaxX, boundingBox.MaxY));

        var resolvedCrs = crs ?? (boundingBox.SpatialReferenceId.HasValue
            ? SpatialReference.Create(boundingBox.SpatialReferenceId.Value).ToOgcCrsUri()
            : "http://www.opengis.net/def/crs/OGC/1.3/CRS84");

        return new
        {
            BoundingBox = bbox,
            Crs = resolvedCrs
        };
    }

    /// <summary>
    /// Creates BoundingBox from OGC spatial extent format
    /// </summary>
    /// <param name="bboxArray">Nested array in format [[minx, miny, maxx, maxy]]</param>
    /// <param name="crs">Optional CRS identifier</param>
    /// <returns>Unified BoundingBox</returns>
    public static BoundingBox FromOgcSpatialExtent(ImmutableArray<ImmutableArray<double>> bboxArray, string? crs = null)
    {
        if (bboxArray.IsEmpty || bboxArray[0].Length != 4)
            throw new ArgumentException("Bounding box array must contain exactly one array with 4 coordinates", nameof(bboxArray));

        var coords = bboxArray[0];
        var spatialRef = !string.IsNullOrWhiteSpace(crs) ? SpatialReferenceExtensions.FromOgcCrsUri(crs) : null;

        return BoundingBox.Create(coords[0], coords[1], coords[2], coords[3], spatialRef?.Wkid);
    }

    /// <summary>
    /// Converts BoundingBox to Core FeatureExtent format
    /// </summary>
    /// <param name="boundingBox">Unified bounding box</param>
    /// <param name="spatialReference">Spatial reference ID, uses boundingBox.SpatialReferenceId if not provided</param>
    /// <returns>Object with FeatureExtent-specific property names</returns>
    public static object ToFeatureExtent(this BoundingBox boundingBox, int? spatialReference = null)
        => new
        {
            MinX = boundingBox.MinX,
            MinY = boundingBox.MinY,
            MaxX = boundingBox.MaxX,
            MaxY = boundingBox.MaxY,
            SpatialReference = spatialReference ?? boundingBox.SpatialReferenceId ?? 4326
        };

    /// <summary>
    /// Creates BoundingBox from Core FeatureExtent format
    /// </summary>
    /// <param name="minX">Minimum X coordinate</param>
    /// <param name="minY">Minimum Y coordinate</param>
    /// <param name="maxX">Maximum X coordinate</param>
    /// <param name="maxY">Maximum Y coordinate</param>
    /// <param name="spatialReference">Spatial reference ID</param>
    /// <returns>Unified BoundingBox</returns>
    public static BoundingBox FromFeatureExtent(double minX, double minY, double maxX, double maxY, int spatialReference)
        => BoundingBox.Create(minX, minY, maxX, maxY, spatialReference);

    /// <summary>
    /// Converts BoundingBox to a simple coordinate array for serialization
    /// </summary>
    /// <param name="boundingBox">Unified bounding box</param>
    /// <returns>Array in format [minX, minY, maxX, maxY]</returns>
    public static double[] ToCoordinateArray(this BoundingBox boundingBox)
        => boundingBox.ToArray();

    /// <summary>
    /// Creates BoundingBox from a coordinate array
    /// </summary>
    /// <param name="coordinates">Array in format [minX, minY, maxX, maxY]</param>
    /// <param name="spatialReferenceId">Optional spatial reference ID</param>
    /// <returns>Unified BoundingBox</returns>
    public static BoundingBox FromCoordinateArray(double[] coordinates, int? spatialReferenceId = null)
        => BoundingBox.FromArray(coordinates, spatialReferenceId);

    /// <summary>
    /// Validates that the bounding box coordinates are in the correct order
    /// </summary>
    /// <param name="boundingBox">Bounding box to validate</param>
    /// <returns>True if valid (MinX ≤ MaxX and MinY ≤ MaxY), false otherwise</returns>
    public static bool IsValidBounds(this BoundingBox boundingBox)
        => boundingBox.IsValid;

    /// <summary>
    /// Normalizes longitude values to [-180, 180] range for geographic coordinate systems
    /// </summary>
    /// <param name="boundingBox">Bounding box to normalize</param>
    /// <param name="isGeographic">Whether the coordinate system is geographic (lat/lon)</param>
    /// <returns>Normalized bounding box</returns>
    public static BoundingBox NormalizeLongitude(this BoundingBox boundingBox, bool isGeographic = true)
    {
        if (!isGeographic)
            return boundingBox;

        static double NormalizeLon(double lon)
        {
            while (lon > 180)
                lon -= 360;
            while (lon < -180)
                lon += 360;
            return lon;
        }

        return BoundingBox.Create(
            NormalizeLon(boundingBox.MinX),
            Math.Max(-90, Math.Min(90, boundingBox.MinY)),  // Clamp latitude
            NormalizeLon(boundingBox.MaxX),
            Math.Max(-90, Math.Min(90, boundingBox.MaxY)),  // Clamp latitude
            boundingBox.SpatialReferenceId
        );
    }

    /// <summary>
    /// Expands the bounding box by a specified buffer distance
    /// </summary>
    /// <param name="boundingBox">Bounding box to expand</param>
    /// <param name="bufferDistance">Distance to expand in coordinate units</param>
    /// <returns>Expanded bounding box</returns>
    public static BoundingBox Expand(this BoundingBox boundingBox, double bufferDistance)
        => BoundingBox.Create(
            boundingBox.MinX - bufferDistance,
            boundingBox.MinY - bufferDistance,
            boundingBox.MaxX + bufferDistance,
            boundingBox.MaxY + bufferDistance,
            boundingBox.SpatialReferenceId);

    /// <summary>
    /// Converts to a Web Mercator bounding box (rough approximation for display purposes)
    /// Note: This is a simple approximation, use proper coordinate transformation for precision
    /// </summary>
    /// <param name="boundingBox">WGS84 bounding box</param>
    /// <returns>Approximate Web Mercator bounding box</returns>
    public static BoundingBox ToWebMercatorApproximate(this BoundingBox boundingBox)
    {
        // Simple Web Mercator approximation (for display only, not precision mapping)
        const double EarthRadius = 6378137; // WGS84 semi-major axis

        static double LonToWebMercatorX(double lon) => lon * Math.PI / 180.0 * EarthRadius;
        static double LatToWebMercatorY(double lat) => Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360.0)) * EarthRadius;

        return BoundingBox.Create(
            LonToWebMercatorX(boundingBox.MinX),
            LatToWebMercatorY(boundingBox.MinY),
            LonToWebMercatorX(boundingBox.MaxX),
            LatToWebMercatorY(boundingBox.MaxY),
            3857); // Web Mercator EPSG code
    }
}
