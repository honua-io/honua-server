// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Tiles;

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

public readonly record struct TerrainGeographicBounds
{
    public required double West { get; init; }

    public required double South { get; init; }

    public required double East { get; init; }

    public required double North { get; init; }
}

public readonly record struct TerrainTileRange
{
    public required int StartX { get; init; }

    public required int StartY { get; init; }

    public required int EndX { get; init; }

    public required int EndY { get; init; }
}

public readonly record struct TerrainGeographicGridRequest
{
    public required int LayerId { get; init; }

    public required int Z { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public RasterMergeStrategy MergeStrategy { get; init; } = RasterMergeStrategy.Newest;

    public int GridSize { get; init; } = TerrainTmsGrid.DefaultGridSize;

    public TerrainGeographicGridRequest()
    {
    }
}

public sealed record TerrainElevationGrid
{
    public required int GridSize { get; init; }

    public required TerrainGeographicBounds Bounds { get; init; }

    public required double?[] Elevations { get; init; }

    public int SelectedRasterCount { get; init; }

    public bool IsAllNoData { get; init; }
}

public static class TerrainTmsGrid
{
    public const int DefaultGridSize = 65;

    public const int MinGridSize = 2;

    public const int MaxGridSize = 257;

    public static bool ValidateTileCoordinates(int z, int x, int y)
    {
        if (z is < 0 or > TileMath.MaxSupportedZoomLevel)
        {
            return false;
        }

        var numX = 2L << z;
        var numY = 1L << z;
        return x >= 0 && x < numX && y >= 0 && y < numY;
    }

    public static TerrainGeographicBounds GetTileBounds(int z, int x, int y)
    {
        if (!ValidateTileCoordinates(z, x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(z), $"Invalid TMS geographic tile coordinates z={z}, x={x}, y={y}.");
        }

        var numX = 2.0 * Math.Pow(2, z);
        var numY = Math.Pow(2, z);
        var tileWidth = 360.0 / numX;
        var tileHeight = 180.0 / numY;

        var west = -180.0 + x * tileWidth;
        var south = -90.0 + y * tileHeight;
        return new TerrainGeographicBounds { West = west, South = south, East = west + tileWidth, North = south + tileHeight };
    }

    public static TerrainTileRange[][] ComputeAvailableRanges(double[]? wgs84Bounds, int minZoom, int maxZoom)
    {
        if (wgs84Bounds is not { Length: 4 } || maxZoom < 0 || minZoom > maxZoom)
        {
            return [];
        }

        var west = Math.Clamp(wgs84Bounds[0], -180.0, 180.0);
        var south = Math.Clamp(wgs84Bounds[1], -90.0, 90.0);
        var east = Math.Clamp(wgs84Bounds[2], -180.0, 180.0);
        var north = Math.Clamp(wgs84Bounds[3], -90.0, 90.0);
        if (!double.IsFinite(west) || !double.IsFinite(south) || !double.IsFinite(east) || !double.IsFinite(north)
            || west > east || south > north)
        {
            return [];
        }

        var clampedMaxZoom = Math.Min(maxZoom, TileMath.MaxSupportedZoomLevel);
        var clampedMinZoom = Math.Clamp(minZoom, 0, clampedMaxZoom);
        var levels = new TerrainTileRange[clampedMaxZoom + 1][];
        for (var z = 0; z <= clampedMaxZoom; z++)
        {
            levels[z] = z < clampedMinZoom
                ? []
                : [ComputeLevelRange(z, new TerrainGeographicBounds { West = west, South = south, East = east, North = north })];
        }

        return levels;
    }

    private static TerrainTileRange ComputeLevelRange(int z, TerrainGeographicBounds bounds)
    {
        var numX = 2 << z;
        var numY = 1 << z;
        var startX = Math.Clamp((int)Math.Floor((bounds.West + 180.0) / 360.0 * numX), 0, numX - 1);
        var eastSample = bounds.East > bounds.West ? Math.BitDecrement(bounds.East) : bounds.East;
        var endX = Math.Clamp((int)Math.Floor((eastSample + 180.0) / 360.0 * numX), startX, numX - 1);

        var startY = Math.Clamp((int)Math.Floor((bounds.South + 90.0) / 180.0 * numY), 0, numY - 1);
        var northSample = bounds.North > bounds.South ? Math.BitDecrement(bounds.North) : bounds.North;
        var endY = Math.Clamp((int)Math.Floor((northSample + 90.0) / 180.0 * numY), startY, numY - 1);

        return new TerrainTileRange { StartX = startX, StartY = startY, EndX = endX, EndY = endY };
    }
}
