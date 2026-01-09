// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Represents a spatial bounding box (extent) with minimum and maximum coordinates
/// </summary>
public readonly record struct BoundingBox
{
    /// <summary>
    /// Minimum X coordinate (westernmost longitude or leftmost easting)
    /// </summary>
    public required double MinX { get; init; }

    /// <summary>
    /// Minimum Y coordinate (southernmost latitude or bottommost northing)
    /// </summary>
    public required double MinY { get; init; }

    /// <summary>
    /// Maximum X coordinate (easternmost longitude or rightmost easting)
    /// </summary>
    public required double MaxX { get; init; }

    /// <summary>
    /// Maximum Y coordinate (northernmost latitude or topmost northing)
    /// </summary>
    public required double MaxY { get; init; }

    /// <summary>
    /// Spatial reference system identifier (SRID/EPSG code)
    /// </summary>
    public int? SpatialReferenceId { get; init; }

    /// <summary>
    /// Creates a bounding box with the specified coordinates
    /// </summary>
    /// <param name="minX">Minimum X coordinate</param>
    /// <param name="minY">Minimum Y coordinate</param>
    /// <param name="maxX">Maximum X coordinate</param>
    /// <param name="maxY">Maximum Y coordinate</param>
    /// <param name="spatialReferenceId">Optional spatial reference system ID</param>
    /// <returns>BoundingBox instance</returns>
    public static BoundingBox Create(double minX, double minY, double maxX, double maxY, int? spatialReferenceId = null)
        => new()
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            SpatialReferenceId = spatialReferenceId
        };

    /// <summary>
    /// Creates a bounding box from an array of coordinates [minX, minY, maxX, maxY]
    /// </summary>
    /// <param name="coordinates">Array of coordinates in order: minX, minY, maxX, maxY</param>
    /// <param name="spatialReferenceId">Optional spatial reference system ID</param>
    /// <returns>BoundingBox instance</returns>
    /// <exception cref="ArgumentException">Thrown when coordinates array doesn't have exactly 4 elements</exception>
    public static BoundingBox FromArray(double[] coordinates, int? spatialReferenceId = null)
    {
        if (coordinates.Length != 4)
            throw new ArgumentException("Coordinates array must contain exactly 4 elements [minX, minY, maxX, maxY]", nameof(coordinates));

        return Create(coordinates[0], coordinates[1], coordinates[2], coordinates[3], spatialReferenceId);
    }

    /// <summary>
    /// Gets the width (difference between MaxX and MinX)
    /// </summary>
    public readonly double Width => MaxX - MinX;

    /// <summary>
    /// Gets the height (difference between MaxY and MinY)
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

    /// <summary>
    /// Gets the area of the bounding box
    /// </summary>
    public readonly double Area => Width * Height;

    /// <summary>
    /// Converts the bounding box to a coordinate array [minX, minY, maxX, maxY]
    /// </summary>
    /// <returns>Array of coordinates</returns>
    public readonly double[] ToArray() => [MinX, MinY, MaxX, MaxY];

    /// <summary>
    /// Checks if this bounding box intersects with another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to test intersection with</param>
    /// <returns>True if the bounding boxes intersect, false otherwise</returns>
    public readonly bool Intersects(BoundingBox other)
        => MinX <= other.MaxX && MaxX >= other.MinX && MinY <= other.MaxY && MaxY >= other.MinY;

    /// <summary>
    /// Checks if this bounding box contains another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to test containment</param>
    /// <returns>True if this bounding box contains the other, false otherwise</returns>
    public readonly bool Contains(BoundingBox other)
        => MinX <= other.MinX && MinY <= other.MinY && MaxX >= other.MaxX && MaxY >= other.MaxY;

    /// <summary>
    /// Expands this bounding box to include another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to include</param>
    /// <returns>New bounding box that encompasses both</returns>
    public readonly BoundingBox Union(BoundingBox other)
        => Create(
            Math.Min(MinX, other.MinX),
            Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX),
            Math.Max(MaxY, other.MaxY),
            SpatialReferenceId ?? other.SpatialReferenceId);

    /// <summary>
    /// Gets the intersection of this bounding box with another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to intersect with</param>
    /// <returns>Intersection bounding box, or null if they don't intersect</returns>
    public readonly BoundingBox? Intersection(BoundingBox other)
    {
        if (!Intersects(other))
            return null;

        return Create(
            Math.Max(MinX, other.MinX),
            Math.Max(MinY, other.MinY),
            Math.Min(MaxX, other.MaxX),
            Math.Min(MaxY, other.MaxY),
            SpatialReferenceId ?? other.SpatialReferenceId);
    }

    /// <summary>
    /// Validates that the bounding box has valid coordinates (MinX ≤ MaxX, MinY ≤ MaxY)
    /// </summary>
    /// <returns>True if the bounding box is valid, false otherwise</returns>
    public readonly bool IsValid => MinX <= MaxX && MinY <= MaxY;

    /// <summary>
    /// Returns a string representation of the bounding box
    /// </summary>
    /// <returns>String in format "BoundingBox[minX, minY, maxX, maxY] SRID:xxxx"</returns>
    public override readonly string ToString()
        => SpatialReferenceId.HasValue
            ? $"BoundingBox[{MinX}, {MinY}, {MaxX}, {MaxY}] SRID:{SpatialReferenceId}"
            : $"BoundingBox[{MinX}, {MinY}, {MaxX}, {MaxY}]";
}
