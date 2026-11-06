// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct VisibilityPoint
{
    public required double Longitude { get; init; }

    public required double Latitude { get; init; }

    public double HeightOffsetMeters { get; init; }
}

public sealed record LineOfSightObstruction
{
    public required double Longitude { get; init; }

    public required double Latitude { get; init; }

    public required double Elevation { get; init; }

    public required double DistanceMeters { get; init; }
}

public sealed record LineOfSightResult
{
    public required int LayerId { get; init; }

    public required bool Visible { get; init; }

    public LineOfSightObstruction? Obstruction { get; init; }

    public required double ObserverGroundElevation { get; init; }

    public required double ObserverElevation { get; init; }

    public required double TargetGroundElevation { get; init; }

    public required double TargetElevation { get; init; }

    public required double DistanceMeters { get; init; }

    public required int SampleCount { get; init; }

    public required bool HasNoDataSamples { get; init; }
}

public readonly record struct ViewshedSample
{
    public required double Longitude { get; init; }

    public required double Latitude { get; init; }

    public required double AzimuthDegrees { get; init; }

    public required double DistanceMeters { get; init; }

    public double? Elevation { get; init; }

    public required bool Visible { get; init; }
}

public sealed record ViewshedResult
{
    public required int LayerId { get; init; }

    public required double ObserverGroundElevation { get; init; }

    public required double ObserverElevation { get; init; }

    public required double RadiusMeters { get; init; }

    public required int RayCount { get; init; }

    public required int SamplesPerRay { get; init; }

    public required ViewshedSample[] Samples { get; init; }

    public required int VisibleSampleCount { get; init; }

    public required bool ObserverNoData { get; init; }
}

public readonly record struct ViewshedOptions
{
    public required double RadiusMeters { get; init; }

    public int? RayCount { get; init; }

    public int? SamplesPerRay { get; init; }

    public double ObserverHeightOffsetMeters { get; init; }

    public double TargetHeightOffsetMeters { get; init; }
}
