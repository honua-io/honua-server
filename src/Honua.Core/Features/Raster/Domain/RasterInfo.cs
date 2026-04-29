// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Represents metadata for a raster dataset.
/// </summary>
public readonly record struct RasterInfo
{
    /// <summary>
    /// Unique identifier for the raster.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Layer ID that owns this raster.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Name or description of the raster.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Width of the raster in pixels.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Height of the raster in pixels.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// Number of bands in the raster.
    /// </summary>
    public required int BandCount { get; init; }

    /// <summary>
    /// Spatial reference system identifier (SRID).
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Pixel data type (e.g., 8BUI, 16BSI, 32BF).
    /// </summary>
    public required string PixelType { get; init; }

    /// <summary>
    /// NoData value for the raster.
    /// </summary>
    public double? NoDataValue { get; init; }

    /// <summary>
    /// Geotransform coefficients for pixel-to-world coordinate transformation.
    /// Array of 6 values: [upperleftx, scalex, skewx, upperlefty, skewy, scaley].
    /// </summary>
    public double[]? GeoTransform { get; init; }

    /// <summary>
    /// Spatial extent of the raster as bounding box.
    /// </summary>
    public RasterExtent? Extent { get; init; }

    /// <summary>
    /// Acquisition timestamp associated with the raster content.
    /// Falls back to <see cref="CreatedAt"/> when the source dataset does not declare one.
    /// </summary>
    public DateTimeOffset? AcquisitionDate { get; init; }

    /// <summary>
    /// Creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last modification timestamp.
    /// </summary>
    public DateTimeOffset? ModifiedAt { get; init; }
}

/// <summary>
/// Represents the spatial extent (bounding box) of a raster.
/// </summary>
public readonly record struct RasterExtent
{
    /// <summary>
    /// Minimum X coordinate.
    /// </summary>
    public required double XMin { get; init; }

    /// <summary>
    /// Minimum Y coordinate.
    /// </summary>
    public required double YMin { get; init; }

    /// <summary>
    /// Maximum X coordinate.
    /// </summary>
    public required double XMax { get; init; }

    /// <summary>
    /// Maximum Y coordinate.
    /// </summary>
    public required double YMax { get; init; }

    /// <summary>
    /// Spatial reference system identifier.
    /// </summary>
    public int? Srid { get; init; }
}
