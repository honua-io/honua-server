// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Idempotent sink for tile records emitted by
/// <see cref="IOgcTileCacheExportService"/>. Implementations persist tile
/// bytes into Honua's tile catalog. Repeated writes for the same
/// (cache, z, x, y) tuple must leave existing rows untouched.
/// </summary>
public interface IOgcTileCacheSink
{
    /// <summary>
    /// Ensure a tile-cache catalog row exists for the supplied descriptor.
    /// Implementations must return the stable target identifier they will
    /// use for tile rows referencing this cache.
    /// </summary>
    /// <param name="descriptor">Tile-cache descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stable target tile-cache identifier.</returns>
    Task<string> EnsureTileCacheAsync(
        OgcTileCacheDescriptor descriptor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a single tile record into the cache. Implementations must be
    /// idempotent: a repeated write for the same (cache, z, x, y) tuple
    /// returns <see cref="OgcTileCacheWriteStatus.AlreadyPresent"/> and does
    /// not overwrite the existing row.
    /// </summary>
    /// <param name="record">Tile record to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the record was inserted or skipped as already present.</returns>
    Task<OgcTileCacheWriteStatus> WriteTileAsync(
        OgcTileCacheRecord record,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stable descriptor for a tile cache.
/// </summary>
public sealed record OgcTileCacheDescriptor
{
    /// <summary>Source WMTS layer identifier.</summary>
    public required string LayerIdentifier { get; init; }

    /// <summary>Source WMTS tile-matrix-set identifier.</summary>
    public required string TileMatrixSetIdentifier { get; init; }

    /// <summary>Sanitized source service URL the export ran against.</summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>Tile format such as <c>image/png</c>.</summary>
    public required string TileFormat { get; init; }

    /// <summary>WMTS style identifier the export was fetched with.</summary>
    public required string StyleIdentifier { get; init; }

    /// <summary>Resolved minimum zoom level included in the export.</summary>
    public required int MinZoom { get; init; }

    /// <summary>Resolved maximum zoom level included in the export.</summary>
    public required int MaxZoom { get; init; }
}

/// <summary>
/// One tile-cache row.
/// </summary>
public sealed record OgcTileCacheRecord
{
    /// <summary>Target tile-cache identifier this tile belongs to.</summary>
    public required string TileCacheId { get; init; }

    /// <summary>Zoom level.</summary>
    public required int Z { get; init; }

    /// <summary>Tile column.</summary>
    public required int X { get; init; }

    /// <summary>Tile row.</summary>
    public required int Y { get; init; }

    /// <summary>Tile content type, such as <c>image/png</c>.</summary>
    public required string ContentType { get; init; }

    /// <summary>Tile content bytes.</summary>
    public required byte[] Content { get; init; }

    /// <summary>Sanitized source URL the tile was fetched from.</summary>
    public required string SourceUrl { get; init; }
}

/// <summary>
/// Outcome of a tile-cache write.
/// </summary>
public enum OgcTileCacheWriteStatus
{
    /// <summary>The tile was inserted into the cache.</summary>
    Inserted = 0,

    /// <summary>A tile already existed at this coordinate; no write occurred.</summary>
    AlreadyPresent = 1
}
