// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Raster.Domain;

public sealed record CogMetadata(
    int Width,
    int Height,
    int BandCount,
    string PixelType,
    int Srid,
    string Compression,
    int TileWidth,
    int TileHeight,
    CogOverviewLevel[] OverviewLevels,
    RasterExtent Extent,
    int BitsPerSample = 8,
    int Predictor = 1,
    bool IsLittleEndian = true,
    int PhotometricInterpretation = 1,
    int PlanarConfiguration = 1,
    string? NoData = null);

public sealed record CogOverviewLevel(
    int Level,
    int Width,
    int Height,
    long IfdOffset,
    long[] TileOffsets,
    int[] TileByteCounts,
    byte[]? JpegTables = null,
    int Predictor = 0);

public sealed record CogOverviewLevelSummary(int Level, int Width, int Height);

public sealed record CogRegistration
{
    public required long Id { get; init; }

    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required CloudStorageProvider Provider { get; init; }

    public required string Bucket { get; init; }

    public required string ObjectKey { get; init; }

    public CogMetadata? Metadata { get; init; }

    public byte[]? IfdCache { get; init; }

    public DateTimeOffset? MetadataScannedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record CogRegistrationRequest
{
    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required CloudStorageProvider Provider { get; init; }

    public required string Bucket { get; init; }

    public required string ObjectKey { get; init; }
}
