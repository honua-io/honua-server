// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Canonical bounded reader for one point value from a coordinate-selected Zarr slice.
/// Protocol adapters parse their wire format into <see cref="ZarrPointSliceSelection"/>
/// values and map the typed result status to protocol-specific errors.
/// </summary>
public interface IZarrPointSliceReader
{
    /// <summary>
    /// Reads one point from the first servable Zarr registration associated with a layer.
    /// </summary>
    Task<ZarrPointSliceReadResult> ReadAsync(
        int layerId,
        double x,
        double y,
        int? inputSrid,
        IReadOnlyList<ZarrPointSliceSelection> selections,
        CancellationToken cancellationToken = default);
}
