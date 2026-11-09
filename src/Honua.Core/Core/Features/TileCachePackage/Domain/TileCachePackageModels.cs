// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.TileCachePackage.Domain;

public enum TileCacheStorageFormat
{
    CompactV2 = 0,

    Exploded = 1
}

public enum TileCacheDataType
{
    Raster = 0,

    Vector = 1
}

public sealed record TileCachePackageDescriptor
{
    public required TileCacheStorageFormat StorageFormat { get; init; }

    public required TileCacheDataType DataType { get; init; }

    public required string ContentType { get; init; }

    public required string TileMatrixSetIdentifier { get; init; }

    public required int Wkid { get; init; }

    public required int TileSize { get; init; }

    public required int MinLevel { get; init; }

    public required int MaxLevel { get; init; }

    public string? Title { get; init; }

    public required string TileBundlesPath { get; init; }

    public string? Assumption { get; init; }
}

public sealed record TileCachePackageTile
{
    public required int Z { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required byte[] Content { get; init; }
}
