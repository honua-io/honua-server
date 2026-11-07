// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public enum SlopeUnits
{
    Degrees = 0,

    Percent = 1,

    Radians = 2
}

public enum RugosityMethod
{
    TerrainRuggednessIndex = 0,

    TopographicPositionIndex = 1,

    Roughness = 2
}

public readonly record struct SurfaceAnalysisRequest
{
    public required int SourceLayerId { get; init; }

    public required long SourceRasterId { get; init; }

    public required int OutputLayerId { get; init; }

    public required string OutputName { get; init; }
}

public readonly record struct SurfaceAnalysisResult
{
    public required int LayerId { get; init; }

    public required long RasterId { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public int? Srid { get; init; }
}

public readonly record struct RasterZonalStatisticsRow
{
    public required long ZoneFeatureId { get; init; }

    public required int Band { get; init; }

    public required long PixelCount { get; init; }

    public required IReadOnlyDictionary<string, double?> Stats { get; init; }
}
