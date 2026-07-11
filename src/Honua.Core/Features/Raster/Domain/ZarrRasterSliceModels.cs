// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Bounded native-CRS request for a rendered 2D Zarr slice.
/// </summary>
/// <param name="Bounds">Requested spatial window in the coverage CRS, or null for the full extent.</param>
/// <param name="InputSrid">Optional bounds spatial reference; reprojection is not performed.</param>
/// <param name="OutputWidth">PNG width in pixels.</param>
/// <param name="OutputHeight">PNG height in pixels.</param>
/// <param name="Selections">Coordinate selections that pin non-spatial dimensions.</param>
public sealed record ZarrRasterSliceReadRequest(
    RasterExtent? Bounds,
    int? InputSrid,
    int OutputWidth,
    int OutputHeight,
    IReadOnlyList<ZarrPointSliceSelection> Selections);

/// <summary>
/// Stable outcome categories for a rendered 2D Zarr slice read.
/// </summary>
public enum ZarrRasterSliceReadStatus
{
    /// <summary>The native-CRS PNG was rendered successfully.</summary>
    Success,

    /// <summary>No scanned Zarr registration exists for the layer.</summary>
    RegistrationNotFound,

    /// <summary>No scanned registration has a configured storage reader.</summary>
    ReaderUnavailable,

    /// <summary>The variable, dimensions, bounds, CRS, or output shape is invalid.</summary>
    InvalidSelection,

    /// <summary>The requested spatial window does not intersect the coverage.</summary>
    OutsideCoverage,

    /// <summary>The bounded backing-store read or PNG render failed.</summary>
    ReadFailed,
}

/// <summary>
/// Result of a canonical rendered 2D Zarr slice read.
/// </summary>
/// <param name="Status">Stable outcome category.</param>
/// <param name="Raster">Rendered PNG and spatial metadata when successful.</param>
/// <param name="Variable">Resolved variable name when known.</param>
/// <param name="DimensionCount">Number of coordinate-selected dimensions.</param>
/// <param name="Error">Curated client-safe error detail when unsuccessful.</param>
public readonly record struct ZarrRasterSliceReadResult(
    ZarrRasterSliceReadStatus Status,
    RasterResult? Raster,
    string? Variable,
    int DimensionCount,
    string? Error);
