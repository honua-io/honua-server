// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.TileCachePackage.Domain;

public sealed record TileCachePackageImportRequest
{
    public required Stream Package { get; init; }

    public required string FileName { get; init; }

    public required string TilesetId { get; init; }

    public int? MinZoom { get; init; }

    public int? MaxZoom { get; init; }

    public bool DryRun { get; init; }
}

public sealed record TileCachePackageImportResult
{
    public required bool Success { get; init; }

    public string? TileCacheId { get; init; }

    public required string StorageFormat { get; init; }

    public required string DataType { get; init; }

    public required string ContentType { get; init; }

    public required string TileMatrixSet { get; init; }

    public required int MinZoom { get; init; }

    public required int MaxZoom { get; init; }

    public required int TilesImported { get; init; }

    public required int TilesSkipped { get; init; }

    public required bool DryRun { get; init; }

    public string? Assumption { get; init; }
}
