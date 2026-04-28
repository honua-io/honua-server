// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Merge strategy applied when multiple rasters overlap inside a layer mosaic.
/// </summary>
public enum RasterMergeStrategy
{
    /// <summary>
    /// Newer rasters take precedence over older rasters.
    /// </summary>
    Newest = 0,

    /// <summary>
    /// Older rasters take precedence over newer rasters.
    /// </summary>
    Oldest = 1,

    /// <summary>
    /// Overlapping pixels are averaged.
    /// </summary>
    Average = 2,

    /// <summary>
    /// Overlapping pixels keep the maximum value.
    /// </summary>
    Max = 3,

    /// <summary>
    /// Overlapping pixels keep the minimum value.
    /// </summary>
    Min = 4
}

/// <summary>
/// Selection filter applied before a raster mosaic is built.
/// </summary>
public readonly record struct RasterSelectionQuery
{
    /// <summary>
    /// Optional selection geometry encoded as WKB.
    /// </summary>
    public byte[]? Geometry { get; init; }

    /// <summary>
    /// SRID of <see cref="Geometry"/>. When null, the raster SRID is assumed.
    /// </summary>
    public int? GeometrySrid { get; init; }

    /// <summary>
    /// Optional temporal selection instant. When supplied, the store selects rasters whose
    /// effective acquisition equals the single most-recent acquisition across the layer at
    /// or before the requested instant ("newest batch" snapshot). Rasters from earlier
    /// acquisitions are excluded — layers with mixed-date scenes can therefore produce
    /// spatial coverage gaps under a timestamp filter.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }
}
