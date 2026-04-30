// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Constants and conversion helpers for Mapbox-compatible Terrain-RGB elevation encoding.
/// </summary>
public static class TerrainRgbEncoding
{
    /// <summary>
    /// Width and height, in pixels, of v1 terrain tiles.
    /// </summary>
    public const int TileSize = 256;

    /// <summary>
    /// Terrain-RGB elevation value emitted for source no-data and uncovered pixels.
    /// </summary>
    public const double NoDataSentinelMeters = -10000.0;

    /// <summary>
    /// Maximum representable Terrain-RGB elevation in meters.
    /// </summary>
    public const double MaxElevationMeters = 1667721.5;

    /// <summary>
    /// Converts an elevation value in meters to a Terrain-RGB pixel.
    /// </summary>
    /// <param name="elevationMeters">Elevation in meters.</param>
    /// <returns>Terrain-RGB pixel.</returns>
    public static TerrainRgbPixel Encode(double? elevationMeters)
    {
        if (!elevationMeters.HasValue ||
            double.IsNaN(elevationMeters.Value) ||
            double.IsInfinity(elevationMeters.Value))
        {
            return new TerrainRgbPixel(0, 0, 0);
        }

        var clamped = Math.Clamp(elevationMeters.Value, NoDataSentinelMeters, MaxElevationMeters);
        var encoded = (int)Math.Round(
            (clamped - NoDataSentinelMeters) * 10.0,
            MidpointRounding.AwayFromZero);

        encoded = Math.Clamp(encoded, 0, 0xFFFFFF);
        return new TerrainRgbPixel(
            (byte)((encoded >> 16) & 0xFF),
            (byte)((encoded >> 8) & 0xFF),
            (byte)(encoded & 0xFF));
    }

    /// <summary>
    /// Decodes a Terrain-RGB pixel to elevation in meters.
    /// </summary>
    /// <param name="pixel">Terrain-RGB pixel.</param>
    /// <returns>Elevation in meters.</returns>
    public static double Decode(TerrainRgbPixel pixel)
    {
        var encoded = pixel.R * 256 * 256 + pixel.G * 256 + pixel.B;
        return NoDataSentinelMeters + encoded * 0.1;
    }
}

/// <summary>
/// A single Terrain-RGB pixel.
/// </summary>
/// <param name="R">Red channel.</param>
/// <param name="G">Green channel.</param>
/// <param name="B">Blue channel.</param>
public readonly record struct TerrainRgbPixel(byte R, byte G, byte B);

/// <summary>
/// Request for a Terrain-RGB tile.
/// </summary>
public readonly record struct TerrainTileRequest
{
    /// <summary>
    /// Layer identifier that owns the raster source dataset.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// XYZ tile zoom level.
    /// </summary>
    public required int Z { get; init; }

    /// <summary>
    /// XYZ tile column.
    /// </summary>
    public required int X { get; init; }

    /// <summary>
    /// XYZ tile row.
    /// </summary>
    public required int Y { get; init; }

    /// <summary>
    /// Raster mosaic merge strategy used when multiple rasters overlap.
    /// </summary>
    public RasterMergeStrategy MergeStrategy { get; init; } = RasterMergeStrategy.Newest;

    /// <summary>
    /// Output tile size in pixels.
    /// </summary>
    public int TileSize { get; init; } = TerrainRgbEncoding.TileSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="TerrainTileRequest"/> struct.
    /// </summary>
    public TerrainTileRequest()
    {
    }
}

/// <summary>
/// Terrain-RGB tile generation result.
/// </summary>
public sealed record TerrainTileResult
{
    /// <summary>
    /// Encoded PNG bytes.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// HTTP content type for the tile.
    /// </summary>
    public string ContentType { get; init; } = "image/png";

    /// <summary>
    /// Tile width in pixels.
    /// </summary>
    public int Width { get; init; } = TerrainRgbEncoding.TileSize;

    /// <summary>
    /// Tile height in pixels.
    /// </summary>
    public int Height { get; init; } = TerrainRgbEncoding.TileSize;

    /// <summary>
    /// Tile coordinate reference system identifier. V1 terrain tiles are Web Mercator.
    /// </summary>
    public int Srid { get; init; } = 3857;

    /// <summary>
    /// Number of source rasters sampled for the tile.
    /// </summary>
    public int SelectedRasterCount { get; init; }

    /// <summary>
    /// Whether every output pixel is the Terrain-RGB no-data sentinel.
    /// </summary>
    public bool IsAllNoData { get; init; }
}

/// <summary>
/// Metadata for a raster-backed terrain dataset.
/// </summary>
public sealed record TerrainDatasetMetadata
{
    /// <summary>
    /// Layer identifier that owns the terrain source.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Raster identifiers that contribute to the terrain dataset.
    /// </summary>
    public required long[] RasterIds { get; init; }

    /// <summary>
    /// Number of registered rasters in the terrain dataset.
    /// </summary>
    public int RasterCount { get; init; }

    /// <summary>
    /// Source raster coordinate reference system identifier when available and consistent.
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// Source raster extent in the source coordinate reference system.
    /// </summary>
    public RasterExtent? SourceExtent { get; init; }

    /// <summary>
    /// Dataset bounds transformed to WGS84 longitude/latitude order for TileJSON clients.
    /// </summary>
    public double[]? Wgs84Bounds { get; init; }

    /// <summary>
    /// Pixel type for the first source band when consistent.
    /// </summary>
    public string? PixelType { get; init; }

    /// <summary>
    /// Number of bands in the source rasters when consistent.
    /// </summary>
    public int? BandCount { get; init; }

    /// <summary>
    /// Source no-data value for band 1 when declared.
    /// </summary>
    public double? SourceNoDataValue { get; init; }

    /// <summary>
    /// Terrain-RGB no-data sentinel emitted for source no-data and uncovered pixels.
    /// </summary>
    public double TerrainNoDataValue { get; init; } = TerrainRgbEncoding.NoDataSentinelMeters;

    /// <summary>
    /// Vertical unit declared by the source dataset, when available.
    /// </summary>
    public string? VerticalUnit { get; init; }

    /// <summary>
    /// Vertical datum declared by the source dataset, when available.
    /// </summary>
    public string? VerticalDatum { get; init; }

    /// <summary>
    /// Whether the registered raster sources can be served as v1 Terrain-RGB tiles.
    /// </summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>
    /// Reasons the terrain source cannot be served, if unsupported.
    /// </summary>
    public string[] UnsupportedReasons { get; init; } = [];
}

/// <summary>
/// Failure categories for terrain tile generation.
/// </summary>
public enum TerrainTileFailureKind
{
    /// <summary>
    /// No raster source is registered for the requested layer.
    /// </summary>
    SourceUnavailable = 0,

    /// <summary>
    /// The source raster shape or pixel model is unsupported for Terrain-RGB output.
    /// </summary>
    UnsupportedSource = 1,

    /// <summary>
    /// The source raster has no usable coordinate reference system.
    /// </summary>
    UnknownCrs = 2
}

/// <summary>
/// Exception raised when a terrain tile request cannot be fulfilled with the registered raster source.
/// </summary>
public sealed class TerrainTileException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerrainTileException"/> class.
    /// </summary>
    /// <param name="failureKind">Failure category.</param>
    /// <param name="message">Public error message.</param>
    public TerrainTileException(TerrainTileFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    /// <summary>
    /// Failure category.
    /// </summary>
    public TerrainTileFailureKind FailureKind { get; }
}
