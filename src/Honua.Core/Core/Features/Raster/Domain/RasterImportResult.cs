// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public sealed record RasterImportResult
{
    public required bool Success { get; init; }

    public long? RasterId { get; init; }

    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public required SupportedRasterFormat Format { get; init; }

    public int? Srid { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int BandCount { get; init; }

    public int StatisticsBands { get; init; }

    public int TilesGenerated { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public static RasterImportResult CreateSuccess(
        long rasterId,
        int layerId,
        string name,
        SupportedRasterFormat format,
        int? srid,
        int width,
        int height,
        int bandCount,
        int statisticsBands,
        int tilesGenerated,
        TimeSpan duration,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = true,
            RasterId = rasterId,
            LayerId = layerId,
            Name = name,
            Format = format,
            Srid = srid,
            Width = width,
            Height = height,
            BandCount = bandCount,
            StatisticsBands = statisticsBands,
            TilesGenerated = tilesGenerated,
            Duration = duration,
            Warnings = warnings ?? []
        };

    public static RasterImportResult CreateFailure(
        int layerId,
        string name,
        SupportedRasterFormat format,
        string errorMessage,
        TimeSpan duration = default,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = false,
            LayerId = layerId,
            Name = name,
            Format = format,
            ErrorMessage = errorMessage,
            Duration = duration,
            Warnings = warnings ?? []
        };
}
