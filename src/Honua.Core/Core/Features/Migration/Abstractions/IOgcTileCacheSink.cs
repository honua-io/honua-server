// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

public interface IOgcTileCacheSink
{
    Task<string> EnsureTileCacheAsync(
        OgcTileCacheDescriptor descriptor,
        CancellationToken cancellationToken = default);

    // An export write does not replace bytes already stored at that coordinate; package re-import uses the overwrite path.
    Task<OgcTileCacheWriteStatus> WriteTileAsync(
        OgcTileCacheRecord record,
        CancellationToken cancellationToken = default);

    // Package re-import overwrites the bytes at a coordinate; resumable export must keep the insert-if-absent path.
    Task<OgcTileCacheWriteStatus> ReplaceTileAsync(
        OgcTileCacheRecord record,
        CancellationToken cancellationToken = default);

    // An empty keep-list deletes every tile in the cache.
    Task DeleteTilesExceptAsync(
        string tileCacheId,
        IReadOnlyList<OgcTileCoordinate> retained,
        CancellationToken cancellationToken = default);
}

public readonly record struct OgcTileCoordinate(int Z, int X, int Y);

public sealed record OgcTileCacheDescriptor
{
    public required string LayerIdentifier { get; init; }

    public required string TileMatrixSetIdentifier { get; init; }

    public required string SourceServiceUrl { get; init; }

    public required string TileFormat { get; init; }

    public required string StyleIdentifier { get; init; }

    public required int MinZoom { get; init; }

    public required int MaxZoom { get; init; }

    public string? DataType { get; init; }

    public string? Title { get; init; }
}

public sealed record OgcTileCacheRecord
{
    public required string TileCacheId { get; init; }

    public required int Z { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required string ContentType { get; init; }

    public required byte[] Content { get; init; }

    public required string SourceUrl { get; init; }
}

public enum OgcTileCacheWriteStatus
{
    Inserted = 0,

    AlreadyPresent = 1,

    Replaced = 2
}
