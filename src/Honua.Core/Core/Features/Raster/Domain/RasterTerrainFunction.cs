// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

// The method is a closed set, so this boundary never accepts caller-supplied SQL.
public enum RasterTerrainMethod
{
    Hillshade = 0,

    Slope = 1,

    Aspect = 2,
}

// There is no free-form expression; the method is the injection boundary and the numbers are SQL parameters.
public readonly record struct RasterTerrainFunction
{
    public const double DefaultAzimuthDegrees = 315.0;

    public const double DefaultAltitudeDegrees = 45.0;

    public const double DefaultZFactor = 1.0;

    public required RasterTerrainMethod Method { get; init; }

    public int Band { get; init; } = 1;

    public double AzimuthDegrees { get; init; } = DefaultAzimuthDegrees;

    public double AltitudeDegrees { get; init; } = DefaultAltitudeDegrees;

    public double ZFactor { get; init; } = DefaultZFactor;

    public RasterTerrainFunction()
    {
    }
}
