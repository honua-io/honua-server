// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Domain.Features;

/// <summary>
/// Represents the spatial extent (bounding box) of features
/// </summary>
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
    /// Gets the width of the extent
    /// </summary>
    public double Width => MaxX - MinX;

    /// <summary>
    /// Gets the height of the extent
    /// </summary>
    public double Height => MaxY - MinY;

    /// <summary>
    /// Gets the center X coordinate
    /// </summary>
    public double CenterX => (MinX + MaxX) / 2.0;

    /// <summary>
    /// Gets the center Y coordinate
    /// </summary>
    public double CenterY => (MinY + MaxY) / 2.0;
}
