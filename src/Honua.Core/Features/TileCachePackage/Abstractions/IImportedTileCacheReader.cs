// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.TileCachePackage.Domain;

namespace Honua.Core.Features.TileCachePackage.Abstractions;

public interface IImportedTileCacheReader
{
    Task<ImportedTileCacheInfo?> GetTileCacheAsync(
        string tileCacheId,
        CancellationToken cancellationToken = default);

    Task<ImportedTile?> GetTileAsync(
        string tileCacheId,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken = default);
}

public sealed record ImportedTileCacheInfo
{
    public required string TileCacheId { get; init; }

    public required string LayerIdentifier { get; init; }

    public required string TileMatrixSet { get; init; }

    public required string TileFormat { get; init; }

    public required string DataType { get; init; }

    public required int MinZoom { get; init; }

    public required int MaxZoom { get; init; }

    public string? Title { get; init; }
}

public sealed record ImportedTile
{
    public required string ContentType { get; init; }

    public required byte[] Content { get; init; }
}
