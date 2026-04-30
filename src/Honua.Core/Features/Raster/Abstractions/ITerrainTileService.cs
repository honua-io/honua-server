// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Generates terrain/elevation tile outputs from registered raster datasets.
/// </summary>
public interface ITerrainTileService
{
    /// <summary>
    /// Gets terrain metadata for a raster-backed layer.
    /// </summary>
    /// <param name="layerId">Layer identifier that owns the raster source dataset.</param>
    /// <param name="mergeStrategy">Raster mosaic merge strategy used when multiple rasters overlap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Terrain metadata, or <c>null</c> when the layer has no registered raster sources.</returns>
    Task<TerrainDatasetMetadata?> GetDatasetMetadataAsync(
        int layerId,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a Terrain-RGB PNG tile for a raster-backed layer.
    /// </summary>
    /// <param name="request">Tile generation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Terrain tile result.</returns>
    Task<TerrainTileResult> GetTerrainRgbTileAsync(
        TerrainTileRequest request,
        CancellationToken cancellationToken = default);
}
