// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Version constants for the provider-neutral raster source contract.
/// </summary>
public static class RasterSourceContract
{
    /// <summary>The earliest descriptor version understood by this release.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>The descriptor version written by this release.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The serving-to-worker job contract required when a durable specification carries
    /// typed raster source references. Version-one jobs predate this contract.
    /// </summary>
    public const int JobContractVersion = 2;
}

/// <summary>
/// Identifies a raster input without materializing its content in the serving process.
/// </summary>
/// <remarks>
/// The discriminator and explicit source contract version allow a worker to reject an
/// unsupported source during rolling upgrades. Implementations contain only stable logical
/// identifiers and never provider SDK objects or raw credentials.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "sourceType")]
[JsonDerivedType(typeof(PostgisRasterSourceDescriptor), "postgis")]
[JsonDerivedType(typeof(ObjectStoreCogRasterSourceDescriptor), "cog")]
[JsonDerivedType(typeof(ObjectStoreZarrRasterSourceDescriptor), "zarr")]
[JsonDerivedType(typeof(StagedArtifactRasterSourceDescriptor), "staged-artifact")]
[JsonDerivedType(typeof(InlineRasterSourceDescriptor), "inline")]
public abstract record RasterSourceDescriptor
{
    /// <summary>The version of this descriptor schema.</summary>
    public int SourceContractVersion { get; init; } = RasterSourceContract.CurrentVersion;

    /// <summary>
    /// Immutable source generation or catalog version. Object sources may use an object
    /// version ID; database sources use the pinned catalog version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>Content identity and integrity metadata used for admission and stale checks.</summary>
    public required RasterContentIdentity Content { get; init; }

    /// <summary>
    /// Untrusted, caller-authored security-context hint. It is correlation metadata only:
    /// workers must not use it to authorize or obtain source access. The submission boundary
    /// introduced by #3068 must mint or revalidate an execution-owned context before any
    /// descriptor becomes executable.
    /// </summary>
    public required RasterSecurityContextReference SecurityContext { get; init; }

    /// <summary>Optional bounded window, band, time, or dimension selection.</summary>
    public RasterSourceSelection? Selection { get; init; }
}

/// <summary>References a raster pinned in the tenant's PostGIS raster catalog.</summary>
public sealed record PostgisRasterSourceDescriptor : RasterSourceDescriptor
{
    /// <summary>Tenant-scoped catalog layer identifier.</summary>
    public required int LayerId { get; init; }

    /// <summary>Tenant-scoped raster identifier within the layer.</summary>
    public required long RasterId { get; init; }
}

/// <summary>References an immutable Cloud Optimized GeoTIFF in a registered object store.</summary>
public sealed record ObjectStoreCogRasterSourceDescriptor : RasterSourceDescriptor
{
    /// <summary>Cloud provider whose execution-owned credentials open the object.</summary>
    public required CloudStorageProvider Provider { get; init; }

    /// <summary>Owning catalog layer retained for audit and worker telemetry.</summary>
    public int? CatalogLayerId { get; init; }

    /// <summary>Catalog raster registration retained for audit and worker telemetry.</summary>
    public long? CatalogRasterId { get; init; }

    /// <summary>
    /// Logical identifier of an operator-registered object store. It is not a provider
    /// connection string, URI, or credential.
    /// </summary>
    public required string StoreReference { get; init; }

    /// <summary>Relative object key within the registered store.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Dimensions read from a bounded header probe of the same immutable object version.
    /// Catalog resolution must populate this before a native worker can open the reference.
    /// </summary>
    public RasterSourceDimensions? DeclaredDimensions { get; init; }
}

/// <summary>Bounded raster dimensions used for pre-GDAL resource admission.</summary>
/// <param name="Width">Declared base-image width in pixels.</param>
/// <param name="Height">Declared base-image height in pixels.</param>
/// <param name="BandCount">Declared samples/bands per pixel.</param>
/// <param name="BitsPerSample">Largest declared sample width across bands.</param>
public sealed record RasterSourceDimensions(
    long Width,
    long Height,
    int BandCount,
    int BitsPerSample);

/// <summary>References an immutable Zarr hierarchy and array in a registered object store.</summary>
public sealed record ObjectStoreZarrRasterSourceDescriptor : RasterSourceDescriptor
{
    /// <summary>
    /// Logical identifier of an operator-registered object store. It is not a provider
    /// connection string, URI, or credential.
    /// </summary>
    public required string StoreReference { get; init; }

    /// <summary>Relative key of the Zarr hierarchy within the registered store.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>Relative array path inside the Zarr hierarchy.</summary>
    public required string ArrayPath { get; init; }
}

/// <summary>References an immutable generation of an artifact in the Honua staging service.</summary>
public sealed record StagedArtifactRasterSourceDescriptor : RasterSourceDescriptor
{
    /// <summary>Opaque staging artifact identifier, without a filesystem path or URI.</summary>
    public required string ArtifactReference { get; init; }
}

/// <summary>
/// Carries a deliberately small raster payload for tests and small callers that cannot use
/// a registered source. Submission validation enforces the configured byte ceiling.
/// </summary>
public sealed record InlineRasterSourceDescriptor : RasterSourceDescriptor
{
    /// <summary>Small inline raster payload. JSON encodes this byte array as base64.</summary>
    public required byte[] Payload { get; init; }
}

/// <summary>Immutable content metadata for a referenced raster source.</summary>
public sealed record RasterContentIdentity
{
    /// <summary>Exact encoded object or artifact size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>IANA media type without transport parameters.</summary>
    public required string MediaType { get; init; }

    /// <summary>Strong content checksum when one is available.</summary>
    public RasterChecksum? Checksum { get; init; }

    /// <summary>Opaque object ETag when one is available.</summary>
    public string? ETag { get; init; }
}

/// <summary>Strong content checksum used to detect source tampering.</summary>
/// <param name="Algorithm">Lower-case digest algorithm, currently <c>sha256</c> or <c>sha512</c>.</param>
/// <param name="Value">Hex-encoded digest value.</param>
public sealed record RasterChecksum(string Algorithm, string Value);

/// <summary>Caller-authored tenant/security context hint for a future raster job.</summary>
public sealed record RasterSecurityContextReference
{
    /// <summary>Tenant identifier that owns the source.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Opaque caller-provided authorization-context hint. It contains neither a token nor
    /// cloud/database credentials and is never evidence of authorization. #3068 must replace
    /// or revalidate it at an authenticated execution boundary.
    /// </summary>
    public required string AuthorizationSnapshotReference { get; init; }

    /// <summary>
    /// Always <see langword="false"/> for this public contract. Caller input cannot promote a
    /// descriptor to a trusted execution context.
    /// </summary>
    [JsonIgnore]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The instance property makes the untrusted status explicit on every caller-authored reference.")]
    public bool IsTrusted => false;
}

/// <summary>Optional bounded sub-selection applied while reading a raster source.</summary>
public sealed record RasterSourceSelection
{
    /// <summary>Optional encoded-pixel window.</summary>
    public RasterPixelWindow? PixelWindow { get; init; }

    /// <summary>One-based band indexes. An empty collection means all bands.</summary>
    public IReadOnlyList<int> Bands { get; init; } = Array.Empty<int>();

    /// <summary>Optional inclusive time range.</summary>
    public RasterTimeSelection? Time { get; init; }

    /// <summary>Optional named half-open Zarr dimension slices.</summary>
    public IReadOnlyList<RasterDimensionSlice> Dimensions { get; init; } = Array.Empty<RasterDimensionSlice>();
}

/// <summary>Bounded pixel window within an encoded raster.</summary>
/// <param name="X">Zero-based left pixel.</param>
/// <param name="Y">Zero-based top pixel.</param>
/// <param name="Width">Positive window width.</param>
/// <param name="Height">Positive window height.</param>
public sealed record RasterPixelWindow(long X, long Y, long Width, long Height);

/// <summary>Inclusive time selection for temporal rasters.</summary>
/// <param name="Start">Inclusive start time.</param>
/// <param name="End">Inclusive end time.</param>
public sealed record RasterTimeSelection(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Half-open selection over a named Zarr dimension.</summary>
/// <param name="Dimension">Dimension name.</param>
/// <param name="Start">Inclusive zero-based start index.</param>
/// <param name="Stop">Exclusive stop index.</param>
/// <param name="Step">Positive sampling step.</param>
public sealed record RasterDimensionSlice(string Dimension, long Start, long Stop, long Step = 1);
