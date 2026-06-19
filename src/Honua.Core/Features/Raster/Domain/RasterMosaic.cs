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
/// Ordering applied when overlapping rasters are unioned into a mosaic. This controls
/// which raster "wins" a contested pixel for the LAST/FIRST pixel-selection merge
/// strategies and is orthogonal to <see cref="RasterMergeStrategy"/> (which selects the
/// pixel-resolution operation). Ordering has no effect on the MEAN/MAX/MIN strategies,
/// which combine overlapping values without regard to raster order.
/// </summary>
public enum RasterMosaicOrdering
{
    /// <summary>
    /// Newest acquisition wins a contested pixel (Esri <c>esriMosaicByAttribute</c> on an
    /// acquisition field, descending). This is the default mosaic ordering.
    /// </summary>
    AcquisitionNewest = 0,

    /// <summary>
    /// Oldest acquisition wins a contested pixel (Esri <c>esriMosaicByAttribute</c> on an
    /// acquisition field, ascending).
    /// </summary>
    AcquisitionOldest = 1,

    /// <summary>
    /// The upper-left-most raster wins a contested pixel: rasters whose envelope sits
    /// further north (and then further west) take precedence (Esri <c>esriMosaicNorthwest</c>).
    /// </summary>
    Northwest = 2,

    /// <summary>
    /// A caller-pinned set of rasters is composited (Esri <c>esriMosaicLockRaster</c>). The
    /// locked-id filtering happens upstream of the store; the union itself orders by newest
    /// acquisition so the contested pixel resolves deterministically among the locked rasters.
    /// </summary>
    LockOrder = 3
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
