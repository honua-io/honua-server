// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public static class TerrainRgbEncoding
{
    public const int TileSize = 256;

    public const double NoDataSentinelMeters = -10000.0;

    public const double MaxElevationMeters = 1667721.5;

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

    public static double Decode(TerrainRgbPixel pixel)
    {
        var encoded = pixel.R * 256 * 256 + pixel.G * 256 + pixel.B;
        return NoDataSentinelMeters + encoded * 0.1;
    }
}

public readonly record struct TerrainRgbPixel(byte R, byte G, byte B);

public readonly record struct TerrainTileRequest
{
    public required int LayerId { get; init; }

    public required int Z { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public RasterMergeStrategy MergeStrategy { get; init; } = RasterMergeStrategy.Newest;

    public int TileSize { get; init; } = TerrainRgbEncoding.TileSize;

    public TerrainTileRequest()
    {
    }
}

public sealed record TerrainTileResult
{
    public required byte[] Data { get; init; }

    public string ContentType { get; init; } = "image/png";

    public int Width { get; init; } = TerrainRgbEncoding.TileSize;

    public int Height { get; init; } = TerrainRgbEncoding.TileSize;

    public int Srid { get; init; } = 3857;

    public int SelectedRasterCount { get; init; }

    public bool IsAllNoData { get; init; }
}

public sealed record TerrainDatasetMetadata
{
    public required int LayerId { get; init; }

    public required long[] RasterIds { get; init; }

    public int RasterCount { get; init; }

    public int? SourceSrid { get; init; }

    public RasterExtent? SourceExtent { get; init; }

    public double[]? Wgs84Bounds { get; init; }

    public string? PixelType { get; init; }

    public int? BandCount { get; init; }

    public double? SourceNoDataValue { get; init; }

    public double TerrainNoDataValue { get; init; } = TerrainRgbEncoding.NoDataSentinelMeters;

    public string? VerticalUnit { get; init; }

    public string? VerticalDatum { get; init; }

    public bool IsSupported { get; init; } = true;

    public string[] UnsupportedReasons { get; init; } = [];
}

public enum TerrainTileFailureKind
{
    SourceUnavailable = 0,

    UnsupportedSource = 1,

    UnknownCrs = 2
}

public sealed class TerrainTileException : InvalidOperationException
{
    public TerrainTileException(TerrainTileFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public TerrainTileFailureKind FailureKind { get; }
}
