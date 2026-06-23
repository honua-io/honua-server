// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Supported Zarr storage specification versions.
/// </summary>
public enum ZarrFormatVersion
{
    /// <summary>
    /// Zarr v2 specification.
    /// </summary>
    V2 = 2,

    /// <summary>
    /// Zarr v3 specification.
    /// </summary>
    V3 = 3
}

/// <summary>
/// Chunk key encoding for locating a chunk object from its grid coordinate.
/// </summary>
/// <param name="Separator">Separator placed between per-dimension chunk indices.</param>
/// <param name="Prefix">
/// Prefix prepended to the chunk key (the Zarr v3 <c>default</c> encoding prepends
/// <c>c</c>, e.g. <c>c/0/1</c>); empty for the Zarr v2 dotted form (<c>0.1</c>).
/// </param>
/// <param name="RankZeroKey">Key used for a zero-dimensional (scalar) array.</param>
public sealed record ZarrChunkKeyEncoding(string Separator, string Prefix, string RankZeroKey)
{
    /// <summary>Zarr v2 chunk key encoding: dotted indices, no prefix (<c>0.1</c>).</summary>
    public static ZarrChunkKeyEncoding V2 { get; } = new(".", string.Empty, "0");

    /// <summary>Zarr v3 <c>default</c> chunk key encoding: <c>c</c>-prefixed, slash-separated (<c>c/0/1</c>).</summary>
    public static ZarrChunkKeyEncoding V3Default { get; } = new("/", "c", "c/0");
}

/// <summary>
/// Parsed metadata for a single Zarr array (variable).
/// </summary>
/// <param name="Name">Logical variable name (last path segment for single-array stores).</param>
/// <param name="ZarrFormat">Detected Zarr format version.</param>
/// <param name="RelativePath">Path under the store root where chunks and <c>.zarray</c> live. Empty for single-array stores.</param>
/// <param name="Shape">Per-dimension array shape.</param>
/// <param name="Chunks">Per-dimension chunk shape.</param>
/// <param name="DataType">numpy-style dtype string (e.g. <c>&lt;f4</c>).</param>
/// <param name="Order">Memory layout order (<c>C</c> or <c>F</c>).</param>
/// <param name="Compressor">Compressor codec id (e.g. <c>zlib</c>); null when uncompressed.</param>
/// <param name="FillValue">Fill value or null when unset.</param>
/// <param name="DimensionNames">CF-style dimension names from <c>_ARRAY_DIMENSIONS</c> (v2) or <c>dimension_names</c> (v3) when present.</param>
/// <param name="ChunkKeyEncoding">How chunk objects are keyed from their grid coordinate (v2 dotted vs. v3 <c>c/</c>-prefixed).</param>
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
    /// <summary>
    /// Backwards-compatible constructor for Zarr v2 arrays that defaults the chunk
    /// key encoding to the v2 dotted form.
    /// </summary>
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

/// <summary>
/// A non-spatial, non-temporal coordinate axis declared on a Zarr store
/// (e.g. a vertical/elevation/pressure-level axis). The axis maps real-world
/// coordinate values to integer indices along the matching array dimension.
/// Exactly one of <paramref name="Coordinates"/> (explicit, possibly irregular
/// sample values) or the evenly-spaced descriptor
/// (<paramref name="Start"/>/<paramref name="End"/>) describes the spacing;
/// when <paramref name="Coordinates"/> is null the axis is treated as evenly
/// spaced over <c>[Start, End]</c> with <paramref name="Count"/> samples.
/// </summary>
/// <param name="Name">Dimension name as it appears in array <c>DimensionNames</c>.</param>
/// <param name="Count">Number of samples along the axis.</param>
/// <param name="Coordinates">Explicit per-sample coordinate values (length == <paramref name="Count"/>), or null for an evenly-spaced axis.</param>
/// <param name="Start">First sample coordinate for an evenly-spaced axis.</param>
/// <param name="End">Last sample coordinate for an evenly-spaced axis.</param>
/// <param name="Unit">Optional coordinate unit (e.g. <c>m</c>, <c>hPa</c>), informational.</param>
/// <param name="Positive">Optional CF <c>positive</c> orientation hint (<c>up</c> / <c>down</c>), informational.</param>
public sealed record ZarrAxis(
    string Name,
    long Count,
    double[]? Coordinates,
    double Start,
    double End,
    string? Unit = null,
    string? Positive = null);

/// <summary>
/// Parsed Zarr store metadata covering one or more arrays.
/// </summary>
/// <param name="ZarrFormat">Detected Zarr format version.</param>
/// <param name="Srid">CRS as an EPSG SRID; 0 when not georeferenced.</param>
/// <param name="Extent">Spatial extent in the declared CRS.</param>
/// <param name="Arrays">Discovered arrays (variables).</param>
/// <param name="PrimaryVariable">Default variable name, or null.</param>
/// <param name="SpatialXDimension">Declared X dimension name, or null.</param>
/// <param name="SpatialYDimension">Declared Y dimension name, or null.</param>
/// <param name="TemporalDimension">Declared time dimension name, or null.</param>
/// <param name="Temporal">Temporal extent of the time axis, or null when the
/// store declares no resolvable time axis.</param>
/// <param name="Axes">Additional non-spatial, non-temporal coordinate axes
/// (vertical/elevation/named) the store declares; empty when none.</param>
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
    ZarrAxis[]? Axes = null)
{
    /// <summary>
    /// Additional coordinate axes declared on the store; never null.
    /// </summary>
    public ZarrAxis[] Axes { get; init; } = Axes ?? [];
}

/// <summary>
/// Persisted catalog entry for a registered Zarr store.
/// </summary>
public sealed record ZarrRegistration
{
    /// <summary>
    /// Unique registration identifier.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Layer this Zarr store is associated with.
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
    /// Cloud storage provider hosting the Zarr store.
    /// </summary>
    public required CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Root key/prefix for the Zarr store.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Parsed Zarr metadata (null if scan has not completed).
    /// </summary>
    public ZarrStoreMetadata? Metadata { get; init; }

    /// <summary>
    /// When metadata was last scanned from the backing store.
    /// </summary>
    public DateTimeOffset? MetadataScannedAt { get; init; }

    /// <summary>
    /// Registration timestamp.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Request to register a cloud-hosted or local Zarr store.
/// </summary>
public sealed record ZarrRegistrationRequest
{
    /// <summary>
    /// Layer to associate the Zarr store with.
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
    /// Root key/prefix for the Zarr store.
    /// </summary>
    public required string RootPath { get; init; }
}

/// <summary>
/// Bounded request for a Zarr subset read.
/// </summary>
public sealed record ZarrSubsetRequest
{
    /// <summary>
    /// Variable (array name) to read.
    /// </summary>
    public required string Variable { get; init; }

    /// <summary>
    /// Inclusive start index per dimension (same order as the array shape).
    /// </summary>
    public required int[] Start { get; init; }

    /// <summary>
    /// Exclusive stop index per dimension.
    /// </summary>
    public required int[] Stop { get; init; }
}

/// <summary>
/// Result of a Zarr subset read.
/// </summary>
public sealed record ZarrSubsetResult(
    string Variable,
    int[] Shape,
    string DataType,
    byte[] Data);
