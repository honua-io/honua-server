// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct SolarPosition
{
    public required double AltitudeDegrees { get; init; }

    public required double AzimuthDegrees { get; init; }

    public required double DeclinationDegrees { get; init; }

    public required double EquationOfTimeMinutes { get; init; }

    public required double HourAngleDegrees { get; init; }

    public bool IsAboveHorizon => AltitudeDegrees > 0;
}

public readonly record struct ShadowObserver
{
    public required double Longitude { get; init; }

    public required double Latitude { get; init; }

    public double HeightMeters { get; init; }
}

public readonly record struct ShadowSample
{
    public required double Longitude { get; init; }

    public required double Latitude { get; init; }

    public required double DistanceMeters { get; init; }

    public double? TerrainElevation { get; init; }

    public required double RayElevation { get; init; }
}

public sealed record SunShadowResult
{
    public required int LayerId { get; init; }

    public required SolarPosition SolarPosition { get; init; }

    public required bool ShadowCast { get; init; }

    public string? NoShadowReason { get; init; }

    public required double ObserverGroundElevation { get; init; }

    public required double ObserverTopElevation { get; init; }

    public required double ShadowAzimuthDegrees { get; init; }

    public required double ShadowLengthMeters { get; init; }

    public double? TipLongitude { get; init; }

    public double? TipLatitude { get; init; }

    public required ShadowSample[] Samples { get; init; }
}

public readonly record struct SunShadowOptions
{
    public required DateTimeOffset InstantUtc { get; init; }

    public required double MaxShadowLengthMeters { get; init; }

    public int? SampleCount { get; init; }
}

public readonly record struct SlicePlane
{
    public required double StartLongitude { get; init; }

    public required double StartLatitude { get; init; }

    public required double EndLongitude { get; init; }

    public required double EndLatitude { get; init; }
}

public readonly record struct SliceSample
{
    public required double Longitude { get; init; }

    public required double Latitude { get; init; }

    public required double DistanceMeters { get; init; }

    public double? Elevation { get; init; }
}

public sealed record SliceResult
{
    public required int LayerId { get; init; }

    public required double LengthMeters { get; init; }

    public required int SampleCount { get; init; }

    public double? MinElevation { get; init; }

    public double? MaxElevation { get; init; }

    public double? ReliefMeters { get; init; }

    public required bool HasNoDataSamples { get; init; }

    public required SliceSample[] Samples { get; init; }
}
