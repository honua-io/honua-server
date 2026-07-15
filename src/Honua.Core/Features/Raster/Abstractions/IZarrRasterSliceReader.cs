// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Canonical bounded reader and renderer for one coordinate-selected 2D Zarr slice.
/// </summary>
public interface IZarrRasterSliceReader
{
    /// <summary>
    /// Reads one grid window and renders it as a PNG (plus an RGBA buffer for re-encoding).
    /// When <see cref="ZarrRasterSliceReadRequest.OutputSrid"/> differs from the coverage CRS
    /// and <paramref name="coordinateTransform"/> is supplied, the slice is warped into the
    /// requested output spatial reference.
    /// </summary>
    /// <param name="layerId">Layer whose registered Zarr coverage is read.</param>
    /// <param name="request">Bounded slice request with optional transform options.</param>
    /// <param name="coordinateTransform">
    /// Shared coordinate-transform service used for reprojection. Required only when the
    /// request asks for an output CRS that differs from the coverage CRS; native reads pass null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ZarrRasterSliceReadResult> ReadAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        ICoordinateTransformService? coordinateTransform = null,
        CancellationToken cancellationToken = default);
}
