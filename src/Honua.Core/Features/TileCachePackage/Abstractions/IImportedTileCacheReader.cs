// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.TileCachePackage.Domain;

namespace Honua.Core.Features.TileCachePackage.Abstractions;

/// <summary>
/// Read path for tiles stored in Honua's tile catalog by the tile-cache package
/// importer (#1269). This is the serving binding that lets imported
/// <c>.tpk</c>/<c>.tpkx</c>/<c>.vtpk</c> caches be served through Honua tile
/// endpoints without re-rendering.
/// </summary>
public interface IImportedTileCacheReader
{
    /// <summary>
    /// Resolve the served tileset metadata for a cache identifier.
    /// </summary>
    /// <param name="tileCacheId">Stable tile-cache identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tileset metadata, or <see langword="null"/> when no such cache exists.</returns>
    Task<ImportedTileCacheInfo?> GetTileCacheAsync(
        string tileCacheId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read a single served tile.
    /// </summary>
    /// <param name="tileCacheId">Stable tile-cache identifier.</param>
    /// <param name="z">Zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tile bytes + content type, or <see langword="null"/> when absent.</returns>
    Task<ImportedTile?> GetTileAsync(
        string tileCacheId,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Served tileset metadata for an imported tile-cache.
/// </summary>
public sealed record ImportedTileCacheInfo
{
    /// <summary>Stable tile-cache identifier.</summary>
    public required string TileCacheId { get; init; }

    /// <summary>Logical tileset identifier the import bound to.</summary>
    public required string LayerIdentifier { get; init; }

    /// <summary>Honua tile-matrix-set identifier.</summary>
    public required string TileMatrixSet { get; init; }

    /// <summary>Served tile content type.</summary>
    public required string TileFormat { get; init; }

    /// <summary>Payload kind: <c>raster</c> or <c>vector</c>.</summary>
    public required string DataType { get; init; }

    /// <summary>Minimum zoom available.</summary>
    public required int MinZoom { get; init; }

    /// <summary>Maximum zoom available.</summary>
    public required int MaxZoom { get; init; }

    /// <summary>Optional human-readable title.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// A single served tile.
/// </summary>
public sealed record ImportedTile
{
    /// <summary>Tile content type.</summary>
    public required string ContentType { get; init; }

    /// <summary>Tile content bytes.</summary>
    public required byte[] Content { get; init; }
}
