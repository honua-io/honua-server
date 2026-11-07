// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public sealed record ElevationPointResult
{
    public double? Elevation { get; init; }

    public required bool NoData { get; init; }

    public required bool OutOfBounds { get; init; }

    public required int LayerId { get; init; }

    public required long[] RasterIds { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public int? QuerySrid { get; init; }

    public int? SourceSrid { get; init; }

    public string? PixelType { get; init; }

    public double? NoDataValue { get; init; }

    public string? VerticalUnit { get; init; }

    public string? VerticalDatum { get; init; }
}

public readonly record struct ElevationSample
{
    public required double DistanceMeters { get; init; }

    public double? Elevation { get; init; }

    public required bool NoData { get; init; }
}

public sealed record ElevationProfileResult
{
    public required ElevationSample[] Samples { get; init; }

    public required double LineLengthMeters { get; init; }

    public required int SampleCount { get; init; }

    public required int LayerId { get; init; }

    public required long[] RasterIds { get; init; }

    public int? SourceSrid { get; init; }

    public string? PixelType { get; init; }

    public double? NoDataValue { get; init; }

    public string? VerticalUnit { get; init; }

    public string? VerticalDatum { get; init; }

    public required bool IsAllNoData { get; init; }
}

public readonly record struct ProfileSamplingOptions
{
    public int? SampleCount { get; init; }

    public double? IntervalMeters { get; init; }

    public required int DefaultSampleCount { get; init; }

    public required int MaxSampleCount { get; init; }
}

public enum ElevationFailureKind
{
    SourceUnavailable = 0,

    UnsupportedCrs = 1,

    InvalidGeometry = 2,

    // The selected rasters do not share a pixel-grid alignment and cannot be combined for a profile.
    MisalignedMosaic = 3
}

public sealed class ElevationQueryException : InvalidOperationException
{
    public ElevationQueryException(ElevationFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public ElevationFailureKind FailureKind { get; }
}
