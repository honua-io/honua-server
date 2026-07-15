// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Bounded request for a rendered 2D Zarr slice. The reader renders in the coverage
/// CRS by default; when <see cref="OutputSrid"/> differs from the coverage CRS and a
/// coordinate-transform service is supplied to the reader, the slice is warped into the
/// requested output spatial reference.
/// </summary>
/// <param name="Bounds">Requested spatial window expressed in <see cref="InputSrid"/>, or null for the full extent.</param>
/// <param name="InputSrid">Spatial reference of <see cref="Bounds"/> (the request bbox CRS).</param>
/// <param name="OutputWidth">Output width in pixels.</param>
/// <param name="OutputHeight">Output height in pixels.</param>
/// <param name="Selections">Coordinate selections that pin non-spatial dimensions.</param>
/// <param name="OutputSrid">
/// Target output spatial reference for the rendered raster. When null or equal to the
/// coverage CRS the slice is rendered natively; otherwise the reader reprojects (warps)
/// the slice into this CRS using the supplied coordinate-transform service.
/// </param>
/// <param name="Resampling">
/// Resampling algorithm used when sampling the source grid. Defaults to nearest neighbor;
/// bilinear and bicubic produce smoother continuous-data output.
/// </param>
/// <param name="Stretch">
/// Optional display stretch that rescales source values to the 8-bit display range before
/// colouring. When null the reader auto-ramps grayscale over the slice value range.
/// </param>
/// <param name="Colormap">
/// Optional pseudocolour colormap applied after any <paramref name="Stretch"/>. When null a
/// grayscale ramp is used.
/// </param>
public sealed record ZarrRasterSliceReadRequest(
    RasterExtent? Bounds,
    int? InputSrid,
    int OutputWidth,
    int OutputHeight,
    IReadOnlyList<ZarrPointSliceSelection> Selections,
    int? OutputSrid = null,
    ResamplingAlgorithm Resampling = ResamplingAlgorithm.NearestNeighbor,
    RasterStretch? Stretch = null,
    RasterColormap? Colormap = null);

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
/// <param name="Rgba">
/// The rendered slice as a row-major 8-bit RGBA pixel buffer (4 bytes/pixel), sized
/// <c>Raster.Width * Raster.Height</c>. Protocol adapters use this to re-encode the slice
/// into container formats (JPEG, TIFF) that the reader itself does not emit. Null unless
/// the read succeeded.
/// </param>
public readonly record struct ZarrRasterSliceReadResult(
    ZarrRasterSliceReadStatus Status,
    RasterResult? Raster,
    string? Variable,
    int DimensionCount,
    string? Error,
    byte[]? Rgba = null);
