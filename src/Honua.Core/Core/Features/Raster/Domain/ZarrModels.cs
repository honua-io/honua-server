// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Domain;

public enum ZarrFormatVersion
{
    V2 = 2,

    V3 = 3
}

public sealed record ZarrChunkKeyEncoding(string Separator, string Prefix, string RankZeroKey)
{
    public static ZarrChunkKeyEncoding V2 { get; } = new(".", string.Empty, "0");

    public static ZarrChunkKeyEncoding V3Default { get; } = new("/", "c", "c/0");
}

public sealed record ZarrArrayMetadata(
    string Name,
    ZarrFormatVersion ZarrFormat,
    string RelativePath,
    int[] Shape,
    int[] Chunks,
    string DataType,
    string Order,
    string? Compressor,
    object? FillValue,
    string[] DimensionNames,
    ZarrChunkKeyEncoding ChunkKeyEncoding)
{
    public ZarrArrayMetadata(
        string Name,
        ZarrFormatVersion ZarrFormat,
        string RelativePath,
        int[] Shape,
        int[] Chunks,
        string DataType,
        string Order,
        string? Compressor,
        object? FillValue,
        string[] DimensionNames)
        : this(Name, ZarrFormat, RelativePath, Shape, Chunks, DataType, Order, Compressor, FillValue, DimensionNames, ZarrChunkKeyEncoding.V2)
    {
    }
}

public sealed record ZarrAxis(
    string Name,
    long Count,
    double[]? Coordinates,
    double Start,
    double End,
    string? Unit = null,
    string? Positive = null);

public sealed record ZarrStoreMetadata(
    ZarrFormatVersion ZarrFormat,
    int Srid,
    RasterExtent Extent,
    ZarrArrayMetadata[] Arrays,
    string? PrimaryVariable,
    string? SpatialXDimension,
    string? SpatialYDimension,
    string? TemporalDimension,
    TemporalExtent? Temporal = null,
    ZarrAxis[]? Axes = null,
    bool YAxisAscending = false)
{
    public ZarrAxis[] Axes { get; init; } = Axes ?? [];
}

public sealed record ZarrRegistration
{
    public required long Id { get; init; }

    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required CloudStorageProvider Provider { get; init; }

    public required string Bucket { get; init; }

    public required string RootPath { get; init; }

    public ZarrStoreMetadata? Metadata { get; init; }

    public DateTimeOffset? MetadataScannedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ZarrRegistrationRequest
{
    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required CloudStorageProvider Provider { get; init; }

    public required string Bucket { get; init; }

    public required string RootPath { get; init; }
}

public sealed record ZarrSubsetRequest
{
    public required string Variable { get; init; }

    public required int[] Start { get; init; }

    public required int[] Stop { get; init; }
}

public sealed record ZarrSubsetResult(
    string Variable,
    int[] Shape,
    string DataType,
    byte[] Data);
