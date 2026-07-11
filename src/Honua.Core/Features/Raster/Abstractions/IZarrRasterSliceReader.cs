// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Canonical bounded reader and renderer for one coordinate-selected 2D Zarr slice.
/// </summary>
public interface IZarrRasterSliceReader
{
    /// <summary>
    /// Reads one native-CRS grid window and renders it as a grayscale PNG.
    /// </summary>
    Task<ZarrRasterSliceReadResult> ReadAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        CancellationToken cancellationToken = default);
}
