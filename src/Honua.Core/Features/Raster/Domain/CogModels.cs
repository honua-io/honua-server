// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Parsed COG structure from IFD chain traversal.
/// </summary>
/// <param name="Width">Base image width in pixels.</param>
/// <param name="Height">Base image height in pixels.</param>
/// <param name="BandCount">Samples per pixel.</param>
/// <param name="PixelType">Classified sample type, e.g. <c>uint8</c> or <c>float32</c>.</param>
/// <param name="Srid">EPSG code parsed from the GeoTIFF keys, or 0 when absent.</param>
/// <param name="Compression">TIFF compression name, e.g. <c>LZW</c>.</param>
/// <param name="TileWidth">Tile width in pixels.</param>
/// <param name="TileHeight">Tile height in pixels.</param>
/// <param name="OverviewLevels">IFD chain, base level first.</param>
/// <param name="Extent">Georeferenced extent of the base level.</param>
/// <param name="BitsPerSample">Bits per sample (TIFF tag 258), needed to reverse a predictor.</param>
/// <param name="Predictor">TIFF predictor (tag 317): 1 = none, 2 = horizontal differencing.</param>
/// <param name="IsLittleEndian">Byte order of the source TIFF, which multi-byte samples are stored in.</param>
/// <param name="IsBigTiff">Whether the source uses the BigTIFF 64-bit container.</param>
/// <param name="PlanarConfiguration">TIFF planar configuration (1 = contiguous, 2 = separate).</param>
/// <param name="PhotometricInterpretation">TIFF photometric interpretation tag value.</param>
/// <param name="Orientation">TIFF orientation tag value (1 = top-left).</param>
/// <param name="HasModelTransformation">Whether a rotation/skew-capable model transformation tag is present.</param>
/// <param name="HasSubIfds">Whether the hierarchy declares SubIFDs that the linear reader does not traverse.</param>
/// <param name="HasHeterogeneousOverviewLayout">Whether a main-chain overview changes codec/tile/sample layout from the base IFD.</param>
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
    bool IsBigTiff = false,
    int PlanarConfiguration = 1,
    int PhotometricInterpretation = -1,
    int Orientation = 1,
    bool HasModelTransformation = false,
    bool HasSubIfds = false,
    bool HasHeterogeneousOverviewLayout = false);

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
public sealed record CogRegistration
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
public sealed record CogRegistrationRequest
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
