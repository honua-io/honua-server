// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Result of a layer-level cloud COG tile lookup.
/// </summary>
public readonly record struct CloudCogTileLookup(RasterResult? Result, bool EditionGateHit);

/// <summary>
/// Maps tile coordinates to cloud range reads for direct COG tile serving.
/// </summary>
public interface ICloudCogTileResolver
{
    /// <summary>
    /// Gets a tile from a cloud-hosted COG by resolving the tile coordinates
    /// to a byte range in the cloud object and reading the compressed tile data.
    /// </summary>
    /// <param name="registration">Cloud COG registration with provider and object details</param>
    /// <param name="level">Zoom level</param>
    /// <param name="row">Tile row</param>
    /// <param name="col">Tile column</param>
    /// <param name="format">Desired output format</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tile data in the requested format, null if tile is outside COG extent</returns>
    Task<RasterResult?> GetTileAsync(
        CloudCogRegistration registration,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up all cloud COGs for a layer, checks edition gating, and returns
    /// the first matching tile. Encapsulates store lookup and license check.
    /// </summary>
    Task<CloudCogTileLookup> GetTileForLayerAsync(
        int layerId,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default);
}
