// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Version and worker-projection constants for referenced raster outputs.</summary>
public static class RasterOutputContract
{
    /// <summary>The earliest descriptor version understood by this release.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>The descriptor version written by this release.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The serving-to-worker contract required by typed raster output publication.</summary>
    public const int JobContractVersion = 2;

    /// <summary>Absolute maximum payload size for an intentionally inline raster output.</summary>
    public const int MaximumInlineBytes = 64 * 1024;

    /// <summary>Absolute maximum staged outputs in one job-attempt manifest.</summary>
    public const int MaximumOutputsPerManifest = 32;
}

/// <summary>Encoding of a referenced raster output.</summary>
public enum RasterOutputEncoding
{
    /// <summary>A Cloud Optimized GeoTIFF object.</summary>
    CloudOptimizedGeoTiff,

    /// <summary>A Zarr hierarchy or array.</summary>
    Zarr,

    /// <summary>A PostGIS raster registered in a tenant catalog.</summary>
    PostgisRaster,

    /// <summary>A deliberately bounded inline preview.</summary>
    Inline
}

/// <summary>
/// Durable metadata for a published raster output. Descriptors contain stable logical identities,
/// never signed URLs, credentials, database connection strings, or provider SDK objects.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "outputType")]
[JsonDerivedType(typeof(ObjectStoreRasterOutputDescriptor), "object")]
[JsonDerivedType(typeof(PostgisRasterOutputDescriptor), "postgis")]
[JsonDerivedType(typeof(InlineRasterOutputDescriptor), "inline")]
public abstract record RasterOutputDescriptor
{
    /// <summary>The version of this output descriptor schema.</summary>
    public int OutputContractVersion { get; init; } = RasterOutputContract.CurrentVersion;

    /// <summary>Stable, retry-independent artifact identity.</summary>
    public required string ArtifactId { get; init; }

    /// <summary>Stable logical output name declared by the process.</summary>
    public required string OutputName { get; init; }

    /// <summary>Exact content identity of the encoded raster.</summary>
    public required RasterContentIdentity Content { get; init; }

    /// <summary>CRS, dimensions, bands, and affine grid metadata.</summary>
    public required RasterGridMetadata Grid { get; init; }

    /// <summary>Engine and engine version that produced the content.</summary>
    public required RasterProducingEngine Engine { get; init; }

    /// <summary>Job, attempt, process, and source lineage.</summary>
    public required RasterOutputLineage Lineage { get; init; }

    /// <summary>Publication and expiry timestamps governed by the existing result-retention policy.</summary>
    public required RasterOutputRetention Retention { get; init; }
}

/// <summary>A published COG or Zarr output in an operator-registered object store.</summary>
public sealed record ObjectStoreRasterOutputDescriptor : RasterOutputDescriptor
{
    /// <summary>Logical object-store registration, not a bucket URL or credential.</summary>
    public required string StoreReference { get; init; }

    /// <summary>Relative immutable object key within the registered store.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>Immutable provider version or content-derived local version.</summary>
    public required string ObjectVersion { get; init; }

    /// <summary>Raster object encoding.</summary>
    public required RasterOutputEncoding Encoding { get; init; }
}

/// <summary>A raster output atomically registered in PostGIS and the Honua catalog.</summary>
public sealed record PostgisRasterOutputDescriptor : RasterOutputDescriptor
{
    /// <summary>Stable idempotent registration identity.</summary>
    public required string RegistrationId { get; init; }

    /// <summary>Tenant-scoped catalog layer identifier.</summary>
    public required int LayerId { get; init; }

    /// <summary>Tenant-scoped PostGIS raster row identifier.</summary>
    public required long RasterId { get; init; }

    /// <summary>Immutable catalog version created by the registration transaction.</summary>
    public required string CatalogVersion { get; init; }
}

/// <summary>A deliberately small raster preview held inline.</summary>
public sealed record InlineRasterOutputDescriptor : RasterOutputDescriptor
{
    /// <summary>Bounded inline payload. Large outputs must use a referenced descriptor.</summary>
    public required byte[] Payload { get; init; }
}

/// <summary>CRS and encoded grid metadata for an output raster.</summary>
public sealed record RasterGridMetadata
{
    /// <summary>Canonical CRS reference such as <c>EPSG:4326</c> or a bounded WKT identifier.</summary>
    public required string Crs { get; init; }

    /// <summary>Positive raster width in pixels.</summary>
    public required long Width { get; init; }

    /// <summary>Positive raster height in pixels.</summary>
    public required long Height { get; init; }

    /// <summary>Positive raster band count.</summary>
    public required int BandCount { get; init; }

    /// <summary>Six-coefficient affine geotransform.</summary>
    public required IReadOnlyList<double> GeoTransform { get; init; }
}

/// <summary>Producing raster engine identity.</summary>
/// <param name="Name">Bounded engine name, for example <c>postgis</c> or <c>gdal</c>.</param>
/// <param name="Version">Bounded engine version.</param>
public sealed record RasterProducingEngine(string Name, string Version);

/// <summary>Durable lineage for a raster output.</summary>
public sealed record RasterOutputLineage
{
    /// <summary>Stable operation identifier.</summary>
    public required string JobId { get; init; }

    /// <summary>Zero-based execution attempt that produced the bytes.</summary>
    public required int Attempt { get; init; }

    /// <summary>Process identifier that produced the output.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Stable source artifact identifiers, never source URLs or credentials.</summary>
    public IReadOnlyList<string> SourceArtifactIds { get; init; } = Array.Empty<string>();
}

/// <summary>Publication and expiry timestamps for an output.</summary>
/// <param name="PublishedAt">Time at which registration made the output visible.</param>
/// <param name="ExpiresAt">Time after which existing retention policy may remove the output.</param>
public sealed record RasterOutputRetention(DateTimeOffset PublishedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Metadata-only reference to bytes staged by one job attempt. The durable job spec carries only
/// the store registration and derived key; raster content remains in the worker/object store.
/// </summary>
public sealed record StagedRasterOutputDescriptor
{
    /// <summary>The version of this staged-output descriptor schema.</summary>
    public int OutputContractVersion { get; init; } = RasterOutputContract.CurrentVersion;

    /// <summary>Stable operation identifier.</summary>
    public required string JobId { get; init; }

    /// <summary>Zero-based attempt that owns the staging prefix.</summary>
    public required int Attempt { get; init; }

    /// <summary>Stable logical output name.</summary>
    public required string OutputName { get; init; }

    /// <summary>Logical object-store registration.</summary>
    public required string StoreReference { get; init; }

    /// <summary>Job/attempt-scoped relative staging key.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>Declared content identity verified while staging and again before publication.</summary>
    public required RasterContentIdentity Content { get; init; }

    /// <summary>Encoding produced by the worker.</summary>
    public required RasterOutputEncoding Encoding { get; init; }

    /// <summary>Declared output grid metadata.</summary>
    public required RasterGridMetadata Grid { get; init; }

    /// <summary>Producing engine identity.</summary>
    public required RasterProducingEngine Engine { get; init; }

    /// <summary>Job and process lineage.</summary>
    public required RasterOutputLineage Lineage { get; init; }

    /// <summary>Time the staged descriptor was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Time after which an orphan reconciler may remove the staged object.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Metadata-only manifest written by a worker after it has staged every output for one attempt.
/// The manifest lives in the configured output store and never contains raster bytes, credentials,
/// or signed locators.
/// </summary>
public sealed record RasterOutputPublicationManifest
{
    /// <summary>The version of the output manifest schema.</summary>
    public int OutputContractVersion { get; init; } = RasterOutputContract.CurrentVersion;

    /// <summary>Stable operation identifier.</summary>
    public required string JobId { get; init; }

    /// <summary>Zero-based attempt that owns every staged entry.</summary>
    public required int Attempt { get; init; }

    /// <summary>Time the worker completed the metadata manifest.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Bounded staged outputs with unique logical names.</summary>
    public required IReadOnlyList<StagedRasterOutputDescriptor> Outputs { get; init; }
}

/// <summary>Deterministic identities for retry-safe raster publication.</summary>
public static class RasterOutputIdentity
{
    /// <summary>Creates an attempt-independent artifact ID from job, output, and content identity.</summary>
    public static string CreateArtifactId(string jobId, string outputName, RasterChecksum checksum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentNullException.ThrowIfNull(checksum);

        var expectedLength = checksum.Algorithm switch
        {
            "sha256" => 64,
            "sha512" => 128,
            _ => 0
        };
        if (expectedLength == 0 || checksum.Value is not { } checksumValue
            || checksumValue.Length != expectedLength || !checksumValue.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Raster artifact identity requires a sha256 or sha512 hex digest.",
                nameof(checksum));
        }

        var material = Encoding.UTF8.GetBytes(string.Concat(
            jobId, "\0", outputName, "\0", checksum.Algorithm, ":", checksumValue.ToUpperInvariant()));
        return "rast_" + Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }
}

/// <summary>Metadata-only serving-to-worker raster output contract.</summary>
public static class RasterOutputWorkerContract
{
    /// <summary>Durable spec parameter holding a logical output-store registration.</summary>
    public const string StoreReferenceParameter = "honua.geoprocessing.raster_output.store_reference";

    /// <summary>Worker environment variable containing the output contract version.</summary>
    public const string ContractVersionEnvironmentVariable = "HONUA_RASTER_OUTPUT_CONTRACT_VERSION";

    /// <summary>Worker environment variable containing the logical output-store registration.</summary>
    public const string StoreReferenceEnvironmentVariable = "HONUA_RASTER_OUTPUT_STORE_REFERENCE";

    /// <summary>Worker environment variable containing the job/attempt staging prefix.</summary>
    public const string StagingPrefixEnvironmentVariable = "HONUA_RASTER_OUTPUT_STAGING_PREFIX";

    /// <summary>Worker environment variable containing the metadata manifest object key.</summary>
    public const string ManifestKeyEnvironmentVariable = "HONUA_RASTER_OUTPUT_MANIFEST_KEY";

    /// <summary>Checks whether a worker environment name is owned by this contract.</summary>
    public static bool IsReservedEnvironmentVariable(string name) =>
        string.Equals(name, ContractVersionEnvironmentVariable, StringComparison.Ordinal)
        || string.Equals(name, StoreReferenceEnvironmentVariable, StringComparison.Ordinal)
        || string.Equals(name, StagingPrefixEnvironmentVariable, StringComparison.Ordinal)
        || string.Equals(name, ManifestKeyEnvironmentVariable, StringComparison.Ordinal);

    /// <summary>Checks that a store reference is a stable logical identifier rather than a locator.</summary>
    public static bool IsLogicalStoreReference(string? storeReference) =>
        !string.IsNullOrWhiteSpace(storeReference) && storeReference.Length <= 128
        && storeReference.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.');

    /// <summary>Builds a job/attempt/output-scoped staging object key.</summary>
    public static string BuildStagingObjectKey(string jobId, int attempt, string outputName)
    {
        ValidateSegment(jobId, nameof(jobId));
        ValidateSegment(outputName, nameof(outputName));
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        return $"raster/staging/{jobId}/attempt-{attempt}/{outputName}";
    }

    /// <summary>Builds the job/attempt staging prefix injected into a worker.</summary>
    public static string BuildStagingPrefix(string jobId, int attempt)
    {
        ValidateSegment(jobId, nameof(jobId));
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        return $"raster/staging/{jobId}/attempt-{attempt}/";
    }

    /// <summary>Builds the metadata-only publication manifest key for a job attempt.</summary>
    public static string BuildManifestObjectKey(string jobId, int attempt)
        => BuildStagingPrefix(jobId, attempt) + "publication-manifest.json";

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Raster output identity segments use only bounded ASCII letters, digits, '.', '_', and '-'.", parameterName);
        }
    }
}
