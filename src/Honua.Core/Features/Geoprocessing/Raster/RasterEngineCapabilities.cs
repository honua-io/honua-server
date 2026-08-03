// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Provider-neutral raster execution engines understood by the planner contract.</summary>
public enum RasterEngine
{
    /// <summary>Database-resident execution through PostGIS Raster.</summary>
    [JsonStringEnumMemberName("postgis")]
    Postgis,

    /// <summary>Native execution in the isolated GDAL worker or a compatible remote backend.</summary>
    [JsonStringEnumMemberName("gdalNative")]
    GdalNative,
}

/// <summary>Physical residency of an input before an engine reads it.</summary>
public enum RasterInputResidency
{
    /// <summary>Tenant-scoped PostGIS raster storage.</summary>
    [JsonStringEnumMemberName("postgis")]
    Postgis,

    /// <summary>Immutable Cloud Optimized GeoTIFF in registered object storage.</summary>
    [JsonStringEnumMemberName("objectStoreCog")]
    ObjectStoreCog,

    /// <summary>Immutable Zarr array or slice in registered object storage.</summary>
    [JsonStringEnumMemberName("objectStoreZarr")]
    ObjectStoreZarr,

    /// <summary>Immutable artifact in Honua staging storage.</summary>
    [JsonStringEnumMemberName("stagedArtifact")]
    StagedArtifact,

    /// <summary>Deliberately small payload carried inline.</summary>
    [JsonStringEnumMemberName("inline")]
    Inline,
}

/// <summary>Durable destination an engine can write without changing process semantics.</summary>
public enum RasterOutputSink
{
    /// <summary>Tenant-scoped PostGIS raster storage.</summary>
    [JsonStringEnumMemberName("postgis")]
    Postgis,

    /// <summary>Immutable registered object storage.</summary>
    [JsonStringEnumMemberName("objectStore")]
    ObjectStore,

    /// <summary>Immutable artifact in Honua staging storage.</summary>
    [JsonStringEnumMemberName("stagedArtifact")]
    StagedArtifact,

    /// <summary>The canonical durable job artifact/result package.</summary>
    [JsonStringEnumMemberName("jobArtifact")]
    JobArtifact,
}

/// <summary>Default ordering hint used after incapable engines are eliminated.</summary>
public enum RasterEngineDefaultPreference
{
    /// <summary>Preferred when capability, residency, and workload budgets allow it.</summary>
    [JsonStringEnumMemberName("preferred")]
    Preferred,

    /// <summary>Fallback when the preferred engine cannot safely execute the request.</summary>
    [JsonStringEnumMemberName("fallback")]
    Fallback,
}

/// <summary>Executable-evidence status for one engine's canonical raster semantics.</summary>
public enum RasterSemanticConformanceStatus
{
    /// <summary>The engine must not participate in dynamic routing.</summary>
    [JsonStringEnumMemberName("unverified")]
    Unverified,

    /// <summary>The implementation defines the canonical baseline used by golden fixtures.</summary>
    [JsonStringEnumMemberName("canonicalBaseline")]
    CanonicalBaseline,

    /// <summary>The implementation passed the advertised cross-engine fixtures.</summary>
    [JsonStringEnumMemberName("verified")]
    Verified,

    /// <summary>Only the explicitly verified variants may participate in dynamic routing.</summary>
    [JsonStringEnumMemberName("restricted")]
    Restricted,
}

/// <summary>Provider-neutral input/output format restrictions for one engine implementation.</summary>
public sealed record RasterFormatRestrictions
{
    /// <summary>IANA media types the implementation can read for this process.</summary>
    public required IReadOnlyList<string> InputMediaTypes { get; init; }

    /// <summary>IANA media types the implementation can produce for this process.</summary>
    public required IReadOnlyList<string> OutputMediaTypes { get; init; }
}

/// <summary>
/// Describes one engine implementation of a canonical raster process without selecting its
/// final placement. Unavailable implementations remain visible with an explicit reason so
/// clients and planners can distinguish a missing executor from an unsupported public ID.
/// </summary>
public sealed record RasterEngineCapability
{
    /// <summary>The execution engine.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>
    /// Stable, semantic-versioned implementation identifier, for example
    /// <c>honua.gdal-native.surface.slope@1.0.0</c>.
    /// </summary>
    public required string ImplementationVersion { get; init; }

    /// <summary>Provider-neutral algorithms or primitives required by this implementation.</summary>
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }

    /// <summary>Accepted input and output media types.</summary>
    public required RasterFormatRestrictions Formats { get; init; }

    /// <summary>Input residencies the implementation can read directly.</summary>
    public required IReadOnlyList<RasterInputResidency> InputResidencies { get; init; }

    /// <summary>Output sinks the implementation can write directly.</summary>
    public required IReadOnlyList<RasterOutputSink> OutputSinks { get; init; }

    /// <summary>
    /// Whether this engine may ever execute synchronously in the request-serving envelope.
    /// Final eligibility also requires complete metadata, workload budgets, and availability.
    /// </summary>
    public required bool RequestExecutionAllowed { get; init; }

    /// <summary>Default ordering hint; final selection is owned by the placement planner.</summary>
    public required RasterEngineDefaultPreference DefaultPreference { get; init; }

    /// <summary>Whether a real executor for this process/engine pair is registered.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// Actionable reason the engine is unavailable, or <see langword="null"/> when available.
    /// </summary>
    public string? UnavailabilityReason { get; init; }

    /// <summary>Cross-engine semantic evidence status.</summary>
    public required RasterSemanticConformanceStatus SemanticConformance { get; init; }

    /// <summary>
    /// Upstream engine version or supported version range exercised by the semantic fixtures.
    /// This is evidence metadata, not a substitute for runtime compatibility checks.
    /// </summary>
    public required string TestedRuntimeVersion { get; init; }

    /// <summary>
    /// Semantic variants admitted under this engine's conformance status. For a canonical baseline,
    /// these variants define the golden contract; verified and restricted engines require executable
    /// provider evidence for every advertised variant.
    /// </summary>
    public required IReadOnlyList<string> VerifiedSemanticVariants { get; init; }

    /// <summary>
    /// Stable checked-in fixture identifiers supporting the conformance claim. Fixture linkage alone
    /// is not proof that every provider runner exercised the fixture; executable tests establish that
    /// evidence for verified and restricted engines.
    /// </summary>
    public required IReadOnlyList<string> SemanticEvidenceFixtureIds { get; init; }

    /// <summary>Explicit known divergences excluded from the verified variant set.</summary>
    public required IReadOnlyList<string> KnownSemanticDivergences { get; init; }

    /// <summary>Whether this engine may participate for the requested variant under its conformance status.</summary>
    public bool SupportsSemanticVariant(string variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        return SemanticConformance != RasterSemanticConformanceStatus.Unverified
            && VerifiedSemanticVariants.Contains(variant, StringComparer.Ordinal);
    }
}

/// <summary>Engine capability metadata attached to one canonical public process ID.</summary>
public sealed record RasterProcessCapability
{
    /// <summary>Canonical process ID shared by every engine implementation.</summary>
    public required string ProcessId { get; init; }

    /// <summary>
    /// Version of the engine-independent raster semantics (NoData, grid, CRS, and output shape).
    /// </summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Canonical semantic variants that a planner may request explicitly.</summary>
    public required IReadOnlyList<string> SemanticVariants { get; init; }

    /// <summary>Available and unavailable engine implementations for this process.</summary>
    public required IReadOnlyList<RasterEngineCapability> Engines { get; init; }
}

/// <summary>
/// Metadata used to estimate raster work before job persistence. A missing value means unknown,
/// never zero; callers use zero only when a dimension is known not to apply (for example zones).
/// </summary>
public sealed record RasterCostEstimatorInput
{
    /// <summary>Number of independent input sources.</summary>
    public long? SourceCount { get; init; }

    /// <summary>Total number of bands read across the selected inputs.</summary>
    public long? BandCount { get; init; }

    /// <summary>Number of vector zones evaluated by zonal operations, or zero when not applicable.</summary>
    public long? ZoneCount { get; init; }

    /// <summary>Total source pixels examined.</summary>
    public long? InputPixels { get; init; }

    /// <summary>Total output pixels expected.</summary>
    public long? OutputPixels { get; init; }

    /// <summary>Expected decoded input bytes in memory or database buffers.</summary>
    public long? DecodedBytes { get; init; }

    /// <summary>Expected temporary/scratch bytes.</summary>
    public long? ExpectedScratchBytes { get; init; }

    /// <summary>Provider-neutral database work units supplied by metadata/cost probes.</summary>
    public long? ExpectedDatabaseWork { get; init; }
}

/// <summary>
/// Conservative, normalized raster cost estimate. Unknown metrics saturate to
/// <see cref="long.MaxValue"/> and make request execution ineligible; a later planner may route
/// the work durably but must never interpret unknown metadata as a small request.
/// </summary>
public sealed record RasterCostEstimate
{
    /// <summary>Process being estimated.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Engine whose static request allowance/availability was evaluated.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>Normalized source count.</summary>
    public required long SourceCount { get; init; }

    /// <summary>Normalized band count.</summary>
    public required long BandCount { get; init; }

    /// <summary>Normalized zone count.</summary>
    public required long ZoneCount { get; init; }

    /// <summary>Normalized input pixel count.</summary>
    public required long InputPixels { get; init; }

    /// <summary>Normalized output pixel count.</summary>
    public required long OutputPixels { get; init; }

    /// <summary>Normalized decoded-byte estimate.</summary>
    public required long DecodedBytes { get; init; }

    /// <summary>Normalized scratch-byte estimate.</summary>
    public required long ExpectedScratchBytes { get; init; }

    /// <summary>Normalized database work units.</summary>
    public required long ExpectedDatabaseWork { get; init; }

    /// <summary>Field names whose values were unknown and conservatively saturated.</summary>
    public required IReadOnlyList<string> UnknownInputs { get; init; }

    /// <summary>Whether any result value came from the conservative unknown sentinel.</summary>
    public bool UsesConservativeValues => UnknownInputs.Count > 0;

    /// <summary>
    /// Whether this engine/estimate may proceed in the request envelope before configurable
    /// planner budgets are applied.
    /// </summary>
    public required bool RequestExecutionAllowed { get; init; }

    /// <summary>Reason request execution is unavailable, or <see langword="null"/> when allowed.</summary>
    public string? RequestExecutionUnavailabilityReason { get; init; }
}
