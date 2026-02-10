// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Result of a raster query operation.
/// </summary>
public readonly record struct RasterResult
{
    /// <summary>
    /// Raster data as byte array in the requested format.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// MIME content type for the raster data.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Width of the returned raster in pixels.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Height of the returned raster in pixels.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// Spatial reference system identifier of the result.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Geotransform coefficients for the result raster.
    /// Array of 6 values: [upperleftx, scalex, skewx, upperlefty, skewy, scaley].
    /// </summary>
    public double[]? GeoTransform { get; init; }

    /// <summary>
    /// Spatial extent of the result raster.
    /// </summary>
    public RasterExtent? Extent { get; init; }

    /// <summary>
    /// Number of bands in the result.
    /// </summary>
    public int BandCount { get; init; }

    /// <summary>
    /// Pixel type of the result data.
    /// </summary>
    public string? PixelType { get; init; }
}

/// <summary>
/// Result of pixel value identification at a point.
/// </summary>
public readonly record struct PixelValueResult
{
    /// <summary>
    /// X coordinate of the query point.
    /// </summary>
    public required double X { get; init; }

    /// <summary>
    /// Y coordinate of the query point.
    /// </summary>
    public required double Y { get; init; }

    /// <summary>
    /// Spatial reference system identifier.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Pixel values for each band at the query point.
    /// Key is band number (1-based), value is pixel value.
    /// </summary>
    public required Dictionary<int, object?> BandValues { get; init; }

    /// <summary>
    /// Whether the query point contains valid data (not NoData).
    /// </summary>
    public bool HasData { get; init; }
}

/// <summary>
/// Statistical information about raster data.
/// </summary>
public readonly record struct RasterStatistics
{
    /// <summary>
    /// Band number (1-based).
    /// </summary>
    public required int Band { get; init; }

    /// <summary>
    /// Minimum pixel value.
    /// </summary>
    public double? MinValue { get; init; }

    /// <summary>
    /// Maximum pixel value.
    /// </summary>
    public double? MaxValue { get; init; }

    /// <summary>
    /// Mean pixel value.
    /// </summary>
    public double? MeanValue { get; init; }

    /// <summary>
    /// Standard deviation of pixel values.
    /// </summary>
    public double? StandardDeviation { get; init; }

    /// <summary>
    /// Number of valid (non-NoData) pixels.
    /// </summary>
    public long ValidPixelCount { get; init; }

    /// <summary>
    /// Number of NoData pixels.
    /// </summary>
    public long NoDataPixelCount { get; init; }
}
