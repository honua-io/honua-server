// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct RasterResult
{
    public required byte[] Data { get; init; }

    public required string ContentType { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public int? Srid { get; init; }

    public double[]? GeoTransform { get; init; }

    public RasterExtent? Extent { get; init; }

    public int BandCount { get; init; }

    public string? PixelType { get; init; }

    public byte[][]? BandValidityMasks { get; init; }
}

public readonly record struct PixelValueResult
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public int? Srid { get; init; }

    public required Dictionary<int, object?> BandValues { get; init; }

    public bool HasData { get; init; }

    public IReadOnlyList<long>? ContributingRasterIds { get; init; }
}

public readonly record struct RasterHistogram
{
    public required int Band { get; init; }

    public required int BinCount { get; init; }

    public required double Min { get; init; }

    public required double Max { get; init; }

    public required long[] Counts { get; init; }
}

public readonly record struct RasterStatistics
{
    public required int Band { get; init; }

    public double? MinValue { get; init; }

    public double? MaxValue { get; init; }

    public double? MeanValue { get; init; }

    public double? StandardDeviation { get; init; }

    public long ValidPixelCount { get; init; }

    public long NoDataPixelCount { get; init; }
}
