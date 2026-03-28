// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Result of a raster import operation.
/// </summary>
public sealed record RasterImportResult
{
    /// <summary>
    /// Whether the import was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// ID of the imported raster in raster_data (null if failed).
    /// </summary>
    public long? RasterId { get; init; }

    /// <summary>
    /// Layer ID the raster was imported into.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Raster name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Detected raster format.
    /// </summary>
    public required SupportedRasterFormat Format { get; init; }

    /// <summary>
    /// Detected or specified SRID.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Width of the imported raster in pixels.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Height of the imported raster in pixels.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Number of bands in the raster.
    /// </summary>
    public int BandCount { get; init; }

    /// <summary>
    /// Number of statistics rows computed.
    /// </summary>
    public int StatisticsBands { get; init; }

    /// <summary>
    /// Number of tiles pre-generated.
    /// </summary>
    public int TilesGenerated { get; init; }

    /// <summary>
    /// Error message if import failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warning messages from the import process.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Import duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Create a successful import result.
    /// </summary>
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

    /// <summary>
    /// Create a failed import result.
    /// </summary>
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
