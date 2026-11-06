// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

// A clip whose box exceeds the pixel budget sets ExceededPixelBudget and returns no pixels, because a partial sample would corrupt the covariance.
public sealed class RasterBandVectorSet
{
    public required int[] Bands { get; init; }

    public required IReadOnlyList<double[]> Pixels { get; init; }

    // ExceededPixelBudget means the sample is empty and the caller must reject the request rather than report a partial signature.
    public bool ExceededPixelBudget { get; init; }

    public long BoundingPixelCount { get; init; }

    public static RasterBandVectorSet Empty(int[] bands) => new()
    {
        Bands = bands,
        Pixels = Array.Empty<double[]>(),
    };
}

public sealed class RasterClassAoi
{
    public required int ClassId { get; init; }

    public string? Name { get; init; }

    public required byte[] ClipGeometry { get; init; }

    public int? ClipSrid { get; init; }
}

public sealed class RasterClassStatisticsRequest
{
    public required int LayerId { get; init; }

    public required long[] RasterIds { get; init; }

    public RasterMergeStrategy MergeStrategy { get; init; } = RasterMergeStrategy.Newest;

    public int[]? Bands { get; init; }

    public required IReadOnlyList<RasterClassAoi> Classes { get; init; }

    public required int MaxPixelsPerClass { get; init; }
}

public readonly record struct RasterClassBandSummary
{
    public required int Band { get; init; }

    public required double Min { get; init; }

    public required double Max { get; init; }

    public required double Mean { get; init; }

    public required double StandardDeviation { get; init; }
}

public sealed class RasterClassSignature
{
    public required int ClassId { get; init; }

    public string? Name { get; init; }

    public required long PixelCount { get; init; }

    public required int[] Bands { get; init; }

    public required double[] Mean { get; init; }

    public required double[][] Covariance { get; init; }

    public required RasterClassBandSummary[] BandSummaries { get; init; }
}

public sealed class RasterClassStatisticsResult
{
    public required IReadOnlyList<RasterClassSignature> Signatures { get; init; }
}
