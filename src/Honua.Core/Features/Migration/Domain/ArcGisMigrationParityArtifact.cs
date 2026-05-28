// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Deterministic per-source post-migration parity outcome emitted after a Honua target apply
/// has been performed for an ArcGIS source. Bundles the four probe results
/// (feature count, schema match, identity-remap coverage, attachment coverage, relationship coverage)
/// plus an aggregate <see cref="Classification"/> so cutover tooling can decide whether the source
/// is ready to flip traffic, requires operator attention, or must be reworked.
/// </summary>
/// <remarks>
/// This artifact is intentionally additive to the existing slice 1-4 manifest evidence stack: it is
/// produced by <see cref="Services.ArcGisMigrationParityRunner"/> after the manifest has been applied
/// to the Honua target, and consumed by the cross-cutting evidence index when assembling the final
/// migration acceptance pack.
/// </remarks>
public sealed record ArcGisMigrationParityArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.arcgis-parity";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier; always <c>arcgis-geoservices-rest</c> for this slice.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Identity and version information for the scanned source.
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Overall classification aggregated from the per-probe classifications using the worst-state
    /// rule: any <c>fail</c> -&gt; <c>fail</c>; else any <c>warn</c> -&gt; <c>warn</c>; else <c>pass</c>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Human-readable reasons that justify the overall classification. Reasons are sorted ordinal,
    /// distinct, and trimmed so artifacts round-trip deterministically.
    /// </summary>
    public string[] Reasons { get; init; } = [];

    /// <summary>
    /// Per-target-resource probe outcomes, sorted by <see cref="ArcGisMigrationParityResourceProbe.TargetResourceId"/>.
    /// </summary>
    public ArcGisMigrationParityResourceProbe[] ResourceProbes { get; init; } = [];
}

/// <summary>
/// Stable parity classification values emitted by <see cref="Services.ArcGisMigrationParityRunner"/>.
/// </summary>
public static class ArcGisMigrationParityClassifications
{
    /// <summary>All probes are within the deterministic pass band.</summary>
    public const string Pass = "pass";

    /// <summary>At least one probe is within the deterministic warn band; manual review recommended.</summary>
    public const string Warn = "warn";

    /// <summary>At least one probe is outside the warn band; cutover must not proceed without remediation.</summary>
    public const string Fail = "fail";
}

/// <summary>
/// Per-target-resource parity probe bundle.
/// </summary>
public sealed record ArcGisMigrationParityResourceProbe
{
    /// <summary>
    /// Source resource identifier copied from the manifest target resource.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Stable target resource identifier copied from the manifest target resource.
    /// </summary>
    public required string TargetResourceId { get; init; }

    /// <summary>
    /// Aggregate classification for this resource using the worst-state rule across the four probes.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Feature-count probe outcome.
    /// </summary>
    public required ArcGisMigrationParityFeatureCountProbe FeatureCount { get; init; }

    /// <summary>
    /// Schema match probe outcome.
    /// </summary>
    public required ArcGisMigrationParitySchemaProbe Schema { get; init; }

    /// <summary>
    /// Identity-remap coverage probe outcome.
    /// </summary>
    public required ArcGisMigrationParityCoverageProbe IdentityCoverage { get; init; }

    /// <summary>
    /// Attachment-coverage probe outcome (over slice-3 records classified <c>automated</c>).
    /// </summary>
    public required ArcGisMigrationParityCoverageProbe AttachmentCoverage { get; init; }

    /// <summary>
    /// Relationship-coverage probe outcome (over slice-3 records classified <c>automated</c> or <c>assisted</c>).
    /// </summary>
    public required ArcGisMigrationParityCoverageProbe RelationshipCoverage { get; init; }
}

/// <summary>
/// Feature-count probe outcome. Compares the source-advertised feature count against the count the
/// target feature reader reports after apply.
/// </summary>
/// <remarks>
/// Bands: <c>pass</c> when the absolute delta ratio is &lt;= 5%; <c>warn</c> when &gt; 5% and &lt;= 20%;
/// <c>fail</c> when &gt; 20%. The probe reports <c>warn</c> when the source did not advertise a count
/// (the runner cannot prove parity without a source baseline).
/// </remarks>
public sealed record ArcGisMigrationParityFeatureCountProbe
{
    /// <summary>Source-advertised feature count. <c>null</c> when the source did not report a count.</summary>
    public long? SourceCount { get; init; }

    /// <summary>Target feature count observed via the supplied feature reader.</summary>
    public long TargetCount { get; init; }

    /// <summary>
    /// Signed delta (<c>TargetCount - SourceCount</c>). <c>null</c> when the source did not advertise
    /// a count.
    /// </summary>
    public long? Delta { get; init; }

    /// <summary>
    /// Absolute delta as a ratio of <see cref="SourceCount"/>. <c>null</c> when the source did not
    /// advertise a count or when the source count was zero.
    /// </summary>
    public double? DeltaRatio { get; init; }

    /// <summary>Classification one of <c>pass</c>, <c>warn</c>, <c>fail</c>.</summary>
    public required string Classification { get; init; }

    /// <summary>Optional human-readable explanation for the classification.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Schema match probe outcome. Confirms every field that the manifest's identity-remap-target schema
/// requires is present on the target table.
/// </summary>
/// <remarks>
/// Bands: <c>pass</c> when zero expected fields are missing from the target; <c>fail</c> when at
/// least one expected field is missing. No <c>warn</c> band - missing fields would lose attribute
/// data and must be remediated before cutover.
/// </remarks>
public sealed record ArcGisMigrationParitySchemaProbe
{
    /// <summary>Field names the source manifest expects to exist on the target. Sorted ordinal.</summary>
    public string[] ExpectedFieldNames { get; init; } = [];

    /// <summary>Field names actually advertised by the target feature reader. Sorted ordinal.</summary>
    public string[] TargetFieldNames { get; init; } = [];

    /// <summary>Subset of expected fields that are missing from the target. Sorted ordinal.</summary>
    public string[] MissingFieldNames { get; init; } = [];

    /// <summary>Classification one of <c>pass</c> or <c>fail</c>.</summary>
    public required string Classification { get; init; }

    /// <summary>Optional human-readable explanation for the classification.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Generic coverage probe outcome. Captures the numerator/denominator and the resulting ratio so
/// the artifact is self-describing in audit reviews.
/// </summary>
/// <remarks>
/// Bands: <c>pass</c> when the coverage ratio is &gt;= 0.99; <c>warn</c> when &gt;= 0.80 and &lt; 0.99;
/// <c>fail</c> when &lt; 0.80. When the denominator is zero the probe reports <c>pass</c> with a
/// "no expected items" reason - the source did not advertise anything to migrate so there is no
/// parity gap.
/// </remarks>
public sealed record ArcGisMigrationParityCoverageProbe
{
    /// <summary>Number of expected items advertised by the source/manifest.</summary>
    public int Expected { get; init; }

    /// <summary>Number of expected items that have a target reference.</summary>
    public int Observed { get; init; }

    /// <summary>
    /// Coverage ratio (<c>Observed / Expected</c>) clamped to <c>[0, 1]</c>. <c>1.0</c> when
    /// <see cref="Expected"/> is zero.
    /// </summary>
    public double Coverage { get; init; }

    /// <summary>Identifiers of expected items that have no target reference. Sorted ordinal.</summary>
    public string[] MissingIds { get; init; } = [];

    /// <summary>Classification one of <c>pass</c>, <c>warn</c>, <c>fail</c>.</summary>
    public required string Classification { get; init; }

    /// <summary>Optional human-readable explanation for the classification.</summary>
    public string? Reason { get; init; }
}
