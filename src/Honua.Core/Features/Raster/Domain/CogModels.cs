// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Parsed COG structure from IFD chain traversal.
/// </summary>
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
    RasterExtent Extent);

/// <summary>
/// One IFD resolution level within a COG.
/// </summary>
public sealed record CogOverviewLevel(
    int Level,
    int Width,
    int Height,
    long IfdOffset,
    long[] TileOffsets,
    int[] TileByteCounts);

/// <summary>
/// Lightweight overview level summary for JSONB persistence (no tile offset arrays).
/// </summary>
public sealed record CogOverviewLevelSummary(int Level, int Width, int Height);

/// <summary>
/// Persisted catalog entry for a cloud-hosted COG.
/// </summary>
public sealed record CloudCogRegistration
{
    /// <summary>
    /// Unique registration identifier.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Layer this COG is associated with.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Human-readable name for the registration.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Cloud storage provider hosting the COG.
    /// </summary>
    public required CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Object key or blob path.
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Parsed COG metadata (null if scan hasn't completed).
    /// </summary>
    public CogMetadata? Metadata { get; init; }

    /// <summary>
    /// Serialized IFD cache for fast tile offset lookup.
    /// </summary>
    public byte[]? IfdCache { get; init; }

    /// <summary>
    /// When metadata was last scanned from the cloud object.
    /// </summary>
    public DateTimeOffset? MetadataScannedAt { get; init; }

    /// <summary>
    /// Registration timestamp.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Request to register a cloud-hosted COG.
/// </summary>
public sealed record CloudCogRegistrationRequest
{
    /// <summary>
    /// Layer to associate the COG with.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Cloud storage provider.
    /// </summary>
    public required CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Object key or blob path.
    /// </summary>
    public required string ObjectKey { get; init; }
}
