// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

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
    /// Zarr v3 specification (read-only support deferred).
    /// </summary>
    V3 = 3
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
/// <param name="DimensionNames">CF-style dimension names from <c>_ARRAY_DIMENSIONS</c> when present.</param>
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
    string[] DimensionNames);

/// <summary>
/// Parsed Zarr store metadata covering one or more arrays.
/// </summary>
public sealed record ZarrStoreMetadata(
    ZarrFormatVersion ZarrFormat,
    int Srid,
    RasterExtent Extent,
    ZarrArrayMetadata[] Arrays,
    string? PrimaryVariable,
    string? SpatialXDimension,
    string? SpatialYDimension,
    string? TemporalDimension);

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
