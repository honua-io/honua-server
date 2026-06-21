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
    LockOrder = 3,

    /// <summary>
    /// A contested pixel is resolved by an arbitrary allowlisted raster attribute (Esri
    /// <c>esriMosaicByAttribute</c> over a non-date field). The attribute column and sort
    /// direction are carried separately on <see cref="RasterMosaicAttributeSort"/>, since the
    /// ordering enum cannot encode an arbitrary column name.
    /// </summary>
    Attribute = 4,

    /// <summary>
    /// Each raster is clipped to its persisted seamline (cutline) before the union, so a
    /// contested pixel is resolved by the per-raster seamline geometry rather than by raster
    /// ordering alone (Esri <c>esriMosaicSeamline</c>, #1804). Among the seamline-clipped pieces
    /// the union orders by newest acquisition. Requires a per-raster seamline in the footprint
    /// store; rasters without one contribute their full footprint.
    /// </summary>
    Seamline = 5,

    /// <summary>
    /// The raster acquired closest to straight-down wins a contested pixel (Esri
    /// <c>esriMosaicNadir</c>, #1870): rasters are ordered by their persisted off-nadir angle
    /// (degrees from straight-down) read from the per-raster sensor/orientation metadata, lowest
    /// off-nadir wins. Rasters without sensor metadata (unknown off-nadir) rank last so a raster
    /// with a known, more-nadir view always outranks one with no orientation recorded. Falls back
    /// to newest acquisition as a tiebreaker.
    /// </summary>
    Nadir = 6
}

/// <summary>
/// Describes an <c>esriMosaicByAttribute</c> ordering over a non-date raster attribute. The
/// <see cref="Column"/> is a strictly allowlisted physical raster-catalog column name (never
/// caller-supplied free text) so it can be safely interpolated into the mosaic <c>ORDER BY</c>.
/// </summary>
/// <param name="Column">The allowlisted physical raster-catalog column to order by.</param>
/// <param name="Ascending">
/// When <c>true</c> the lowest attribute value wins a contested pixel; when <c>false</c> the
/// highest value wins (the Esri descending default).
/// </param>
public readonly record struct RasterMosaicAttributeSort(string Column, bool Ascending);

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
