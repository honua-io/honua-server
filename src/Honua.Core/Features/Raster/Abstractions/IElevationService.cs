// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Provides numeric elevation lookup against registered raster datasets.
/// </summary>
public interface IElevationService
{
    /// <summary>
    /// Samples elevation at a single point against the layer's mosaic.
    /// </summary>
    /// <param name="layerId">Layer identifier that owns the elevation source.</param>
    /// <param name="x">X coordinate in the requested SRID.</param>
    /// <param name="y">Y coordinate in the requested SRID.</param>
    /// <param name="srid">SRID of the supplied coordinate. <c>null</c> defaults to 4326.</param>
    /// <param name="mergeStrategy">Mosaic merge strategy applied across overlapping rasters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Elevation point result, including no-data and source metadata.</returns>
    Task<ElevationPointResult> QueryPointAsync(
        int layerId,
        double x,
        double y,
        int? srid,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Samples elevation along a LineString geometry, returning ordered
    /// distance/elevation samples.
    /// </summary>
    /// <param name="layerId">Layer identifier that owns the elevation source.</param>
    /// <param name="lineWkb">LineString geometry encoded as WKB.</param>
    /// <param name="lineSrid">SRID of the supplied LineString.</param>
    /// <param name="options">Sampling options controlling the sample count.</param>
    /// <param name="mergeStrategy">Mosaic merge strategy applied across overlapping rasters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Elevation profile result with one sample per requested position.</returns>
    Task<ElevationProfileResult> QueryProfileAsync(
        int layerId,
        byte[] lineWkb,
        int lineSrid,
        ProfileSamplingOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);
}
