// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Slope output units. Mirrors the PostGIS ST_Slope <c>units</c> argument.
/// </summary>
public enum SlopeUnits
{
    /// <summary>
    /// Slope reported as degrees from the horizontal plane (0–90).
    /// </summary>
    Degrees = 0,

    /// <summary>
    /// Slope reported as rise/run percent (0–∞).
    /// </summary>
    Percent = 1,

    /// <summary>
    /// Slope reported as radians from the horizontal plane (0–π/2).
    /// </summary>
    Radians = 2
}

/// <summary>
/// Terrain-ruggedness families supported by the canonical surface service.
/// </summary>
public enum RugosityMethod
{
    /// <summary>
    /// Terrain Ruggedness Index (mean absolute elevation difference over the window).
    /// </summary>
    TerrainRuggednessIndex = 0,

    /// <summary>
    /// Topographic Position Index (center minus mean over the window).
    /// </summary>
    TopographicPositionIndex = 1,

    /// <summary>
    /// Roughness (max minus min over the window).
    /// </summary>
    Roughness = 2
}

/// <summary>
/// Shared identification for the raster that a surface operation reads and the
/// layer that will receive the derived raster output.
/// </summary>
/// <remarks>
/// The derived raster is written into <see cref="OutputLayerId"/> via the same
/// PostGIS <c>raster_data</c> path the import service uses, so callers downstream
/// can read it through <see cref="Abstractions.IRasterStore"/>.
/// </remarks>
public readonly record struct SurfaceAnalysisRequest
{
    /// <summary>
    /// Source layer containing the DEM raster.
    /// </summary>
    public required int SourceLayerId { get; init; }

    /// <summary>
    /// Source raster identifier within <see cref="SourceLayerId"/>.
    /// </summary>
    public required long SourceRasterId { get; init; }

    /// <summary>
    /// Target layer that will own the derived raster.
    /// </summary>
    public required int OutputLayerId { get; init; }

    /// <summary>
    /// Human-readable name to stamp onto the derived raster.
    /// </summary>
    public required string OutputName { get; init; }
}

/// <summary>
/// Result descriptor returned after a surface operation persists a derived raster.
/// </summary>
public readonly record struct SurfaceAnalysisResult
{
    /// <summary>
    /// Layer that owns the derived raster.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Identifier of the derived raster within the layer.
    /// </summary>
    public required long RasterId { get; init; }

    /// <summary>
    /// Width in pixels of the derived raster.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Height in pixels of the derived raster.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// SRID of the derived raster, if known.
    /// </summary>
    public int? Srid { get; init; }
}

/// <summary>
/// Zonal statistics row: one aggregate row per eligible input zone feature.
/// </summary>
/// <remarks>
/// The stats map always contains every requested stat name (case-insensitive,
/// canonicalized to lowercase). Values are <c>null</c> when the stat could not
/// be computed for the zone (for example, an empty intersection with the raster).
/// </remarks>
public readonly record struct RasterZonalStatisticsRow
{
    /// <summary>
    /// Primary key of the zone feature from the zones layer.
    /// </summary>
    public required long ZoneFeatureId { get; init; }

    /// <summary>
    /// Band number the stats were computed over (1-based).
    /// </summary>
    public required int Band { get; init; }

    /// <summary>
    /// Number of valid (non-NoData) pixels that contributed to the aggregates.
    /// </summary>
    public required long PixelCount { get; init; }

    /// <summary>
    /// Aggregate values keyed by stat name (e.g. "count", "sum", "mean", "min",
    /// "max", "stddev", "variance").
    /// </summary>
    public required IReadOnlyDictionary<string, double?> Stats { get; init; }
}
