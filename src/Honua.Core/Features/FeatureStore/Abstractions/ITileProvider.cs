// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides tile generation capabilities for map visualization.
/// Segregated to enable optimized caching and specialized tile servers.
/// </summary>
public interface ITileProvider
{
    /// <summary>
    /// Generates an MVT (Mapbox Vector Tile) for the specified tile coordinates
    /// </summary>
    /// <param name="layerId">Layer identifier to generate tile from</param>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <param name="query">Optional query specification for filtering</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MVT tile data as byte array, null if no features in tile</returns>
    Task<byte[]?> GetMvtTileAsync(int layerId, int x, int y, int z, FeatureQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an MVT (Mapbox Vector Tile) for the specified tile coordinates with custom tile options
    /// </summary>
    /// <param name="layerId">Layer identifier to generate tile from</param>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <param name="query">Optional query specification for filtering</param>
    /// <param name="tileOptions">Tile generation options (extent, buffer, simplification, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MVT tile data as byte array, null if no features in tile</returns>
    Task<byte[]?> GetMvtTileAsync(int layerId, int x, int y, int z, FeatureQuery? query, Honua.Core.Features.Tiles.TileOptions tileOptions, CancellationToken cancellationToken = default);
}
