// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Result of a layer-level COG tile lookup.
/// </summary>
public readonly record struct CogTileLookup(RasterResult? Result, bool EditionGateHit);

/// <summary>
/// Maps tile coordinates to cloud range reads for direct COG tile serving.
/// </summary>
public interface ICogTileResolver
{
    /// <summary>
    /// Gets a tile from a cloud-hosted COG by resolving the tile coordinates
    /// to a byte range in the cloud object and reading the compressed tile data.
    /// </summary>
    /// <param name="registration">COG registration with provider and object details</param>
    /// <param name="level">Zoom level</param>
    /// <param name="row">Tile row</param>
    /// <param name="col">Tile column</param>
    /// <param name="format">Desired output format</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tile data in the requested format, null if tile is outside COG extent</returns>
    Task<RasterResult?> GetTileAsync(
        CogRegistration registration,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up all COGs for a layer, checks edition gating, and returns
    /// the first matching tile. Encapsulates store lookup and license check.
    /// </summary>
    Task<CogTileLookup> GetTileForLayerAsync(
        int layerId,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default);
}
