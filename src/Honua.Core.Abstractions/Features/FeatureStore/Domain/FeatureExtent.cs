// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents the spatial extent (bounding box) of features.
/// This is now an alias for the unified BoundingBox type.
/// </summary>
/// <remarks>
/// This type alias is maintained for backward compatibility.
/// New code should use BoundingBox directly from Honua.Core.Features.Shared.Models.
/// </remarks>
public readonly record struct FeatureExtent
{
    /// <summary>
    /// Minimum X coordinate
    /// </summary>
    public required double MinX { get; init; }

    /// <summary>
    /// Minimum Y coordinate
    /// </summary>
    public required double MinY { get; init; }

    /// <summary>
    /// Maximum X coordinate
    /// </summary>
    public required double MaxX { get; init; }

    /// <summary>
    /// Maximum Y coordinate
    /// </summary>
    public required double MaxY { get; init; }

    /// <summary>
    /// Spatial Reference System Identifier (SRID)
    /// </summary>
    public required int SpatialReference { get; init; }

    /// <summary>
    /// Creates a feature extent
    /// </summary>
    /// <param name="minX">Minimum X coordinate</param>
    /// <param name="minY">Minimum Y coordinate</param>
    /// <param name="maxX">Maximum X coordinate</param>
    /// <param name="maxY">Maximum Y coordinate</param>
    /// <param name="spatialReference">Spatial Reference System ID</param>
    /// <returns>Feature extent instance</returns>
    public static FeatureExtent Create(double minX, double minY, double maxX, double maxY, int spatialReference)
        => new()
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            SpatialReference = spatialReference
        };

    /// <summary>
    /// Creates a FeatureExtent from a unified BoundingBox
    /// </summary>
    /// <param name="boundingBox">Unified bounding box</param>
    /// <returns>FeatureExtent instance</returns>
    public static FeatureExtent FromBoundingBox(BoundingBox boundingBox)
        => new()
        {
            MinX = boundingBox.MinX,
            MinY = boundingBox.MinY,
            MaxX = boundingBox.MaxX,
            MaxY = boundingBox.MaxY,
            SpatialReference = boundingBox.SpatialReferenceId ?? 4326 // Default to WGS84
        };

    /// <summary>
    /// Converts this FeatureExtent to a unified BoundingBox
    /// </summary>
    /// <returns>Unified BoundingBox</returns>
    public readonly BoundingBox ToBoundingBox()
        => BoundingBox.Create(MinX, MinY, MaxX, MaxY, SpatialReference);

    /// <summary>
    /// Gets the width of the extent
    /// </summary>
    public readonly double Width => MaxX - MinX;

    /// <summary>
    /// Gets the height of the extent
    /// </summary>
    public readonly double Height => MaxY - MinY;

    /// <summary>
    /// Gets the center X coordinate
    /// </summary>
    public readonly double CenterX => (MinX + MaxX) / 2.0;

    /// <summary>
    /// Gets the center Y coordinate
    /// </summary>
    public readonly double CenterY => (MinY + MaxY) / 2.0;
}
