// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides tile generation capabilities for map visualization.
/// Segregated to enable optimized caching and specialized tile servers.
/// </summary>
public interface ITileProvider
{
    /// <summary>
    /// Generates an MVT (Mapbox Vector Tile) for the specified tile coordinates with custom tile options
    /// </summary>
    /// <param name="layerId">Layer identifier to generate tile from</param>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <param name="query">Optional query specification for filtering</param>
    /// <param name="tileOptions">Tile rendering options (extent, buffer, simplification, etc.)</param>
    /// <param name="tileLimits">Tile generation limits</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="gridGeometry">
    /// Optional gridset geometry for operator-defined custom tile matrix sets (#1839). When
    /// <see langword="null"/> the provider renders against the two built-in pyramids
    /// (WebMercatorQuad / WorldCRS84Quad) exactly as before — output stays byte-identical. When
    /// supplied, the provider derives the tile envelope and target SRID from the gridset so it can
    /// serve vector tiles for arbitrary custom gridsets (reprojecting the stored geometry into the
    /// gridset CRS via <c>ST_Transform</c>).
    /// </param>
    /// <returns>MVT tile data as byte array, null if no features in tile</returns>
    Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        Honua.Core.Features.Tiles.TileOptions tileOptions,
        TileLimits tileLimits,
        Honua.Core.Features.Tiles.GridGeometry? gridGeometry = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an MVT tile containing H3 hexagonal cell boundaries with aggregated feature counts.
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <param name="resolution">H3 resolution level (0–15)</param>
    /// <param name="query">Optional query specification for filtering</param>
    /// <param name="tileOptions">Tile rendering options</param>
    /// <param name="tileLimits">Tile generation limits</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MVT tile data as byte array, null if no features in tile</returns>
    Task<byte[]?> GetH3MvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        Honua.Core.Features.Tiles.TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken = default);
}
