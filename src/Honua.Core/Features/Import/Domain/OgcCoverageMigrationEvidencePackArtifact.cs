// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Slice 5 (capstone) of issue #1030. Single deterministic evidence bundle
/// emitted from a successful OGC coverage migration run. Aggregates the
/// slice-1 coverage inventory, slice-2 OGC API Coverages import records,
/// slice-3 legacy WCS import records, and slice-4 coverage-style migration
/// diagnostics into a single artifact so reviewers (and nightly fixture
/// runs) have one pack to audit raster data + style migration outcomes.
/// </summary>
/// <remarks>
/// <para>
/// The pack carries a SHA-256 fingerprint computed over the canonical JSON
/// of its bundle so identical inputs always produce the same fingerprint.
/// The fingerprint deliberately excludes the wall-clock timestamp, run id,
/// and generator label so re-runs across machines stay byte-identical.
/// </para>
/// <para>
/// Privacy posture: <see cref="OgcCoverageMigrationEvidencePackBundle.Source"/>
/// and the embedded inventory snapshot reuse the redaction behavior of the
/// upstream inventory artifact. The builder additionally strips credentials
/// (userinfo, query, fragment) from the source URL before any field is
/// embedded in the pack. Raw raster payloads and style documents are never
/// included — only counts, classifications, and diagnostic messages derived
/// from slices 1-4.
/// </para>
/// <para>
/// AOT note: this record uses POCO-only properties (no polymorphic
/// converters, no <c>JsonExtensionData</c>) so the source-generated
/// JSON context remains trim/AOT safe.
/// </para>
/// </remarks>
public sealed record OgcCoverageMigrationEvidencePackArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.ogc-coverage-evidence-pack";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable run identifier supplied by the harness or nightly workflow.
    /// Excluded from <see cref="BundleFingerprint"/>.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Free-form generator label, e.g. <c>ogc-coverage-evidence-builder/1.0</c>.
    /// Excluded from <see cref="BundleFingerprint"/> so re-runs across CI
    /// images stay byte-identical.
    /// </summary>
    public required string Generator { get; init; }

    /// <summary>
    /// UTC instant the pack was generated. Excluded from
    /// <see cref="BundleFingerprint"/> so re-runs stay byte-identical.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// SHA-256 fingerprint computed over the canonical JSON of
    /// <see cref="Bundle"/>. Identical inputs always produce the same value.
    /// </summary>
    public required string BundleFingerprint { get; init; }

    /// <summary>
    /// Deterministic evidence bundle that aggregates slice 1-4 artifacts.
    /// </summary>
    public required OgcCoverageMigrationEvidencePackBundle Bundle { get; init; }
}

/// <summary>
/// Deterministic content bundle covered by
/// <see cref="OgcCoverageMigrationEvidencePackArtifact.BundleFingerprint"/>.
/// </summary>
public sealed record OgcCoverageMigrationEvidencePackBundle
{
    /// <summary>
    /// Source kind identifier such as <c>ogc-api-coverages</c> or
    /// <c>ogc-wcs</c>. Mirrors the inventory artifact source kind.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Secret-safe source identity (credentials and URL secrets stripped).
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Operator-requested coverage scope. Empty when the run imported every
    /// inventoried coverage. Captured here as coverage-scoping evidence so
    /// reviewers can audit that coverages outside the requested scope were
    /// not migrated.
    /// </summary>
    public required OgcCoverageMigrationEvidencePackScope CoverageScope { get; init; }

    /// <summary>
    /// Aggregate counts across the OGC API Coverages and WCS protocol
    /// channels, plus a style roll-up surfaced from slice-4 diagnostics.
    /// </summary>
    public required OgcCoverageMigrationEvidencePackSummary Summary { get; init; }

    /// <summary>
    /// Per-protocol channels covering OGC API Coverages and WCS uniformly.
    /// Channels are emitted in canonical order
    /// (<see cref="OgcCoverageMigrationEvidencePackChannelIds.OgcApiCoverages"/>,
    /// <see cref="OgcCoverageMigrationEvidencePackChannelIds.Wcs"/>) so the
    /// fingerprint stays deterministic regardless of caller order.
    /// </summary>
    public OgcCoverageMigrationEvidencePackChannel[] Channels { get; init; } = [];

    /// <summary>
    /// Aggregated coverage-style migration diagnostics (slice 4 of #1030).
    /// Combined across all channels so a reviewer can audit every
    /// non-trivial style hint from one place.
    /// </summary>
    public MigrationCoverageStyleDiagnostic[] StyleDiagnostics { get; init; } = [];

    /// <summary>
    /// Coverage inventory snapshot copied from the slice-1 scan input. Source
    /// URLs are redacted.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }
}

/// <summary>
/// Operator-requested coverage scope captured in the evidence pack.
/// </summary>
public sealed record OgcCoverageMigrationEvidencePackScope
{
    /// <summary>
    /// Whether the operator restricted the run to specific source coverage
    /// identifiers. When <c>false</c>, all inventoried coverages were
    /// eligible.
    /// </summary>
    public required bool Restricted { get; init; }

    /// <summary>
    /// Deterministically ordered list of requested source coverage ids.
    /// Empty when <see cref="Restricted"/> is <c>false</c>.
    /// </summary>
    public string[] CoverageIds { get; init; } = [];
}

/// <summary>
/// Aggregate evidence summary spanning every protocol channel and the
/// slice-4 style diagnostic roll-up.
/// </summary>
public sealed record OgcCoverageMigrationEvidencePackSummary
{
    /// <summary>
    /// Total number of per-coverage import records across all channels.
    /// </summary>
    public int TotalCoverageCount { get; init; }

    /// <summary>
    /// Number of records whose action is <c>imported</c>.
    /// </summary>
    public int ImportedCount { get; init; }

    /// <summary>
    /// Number of records whose action is <c>planned</c> (dry-run preview).
    /// </summary>
    public int PlannedCount { get; init; }

    /// <summary>
    /// Number of records whose action is <c>skipped</c>.
    /// </summary>
    public int SkippedCount { get; init; }

    /// <summary>
    /// Number of records whose action is <c>manual-review</c>.
    /// </summary>
    public int ManualReviewCount { get; init; }

    /// <summary>
    /// Number of records whose action is <c>failed</c>.
    /// </summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// Total style diagnostic count across all channels.
    /// </summary>
    public int StyleDiagnosticCount { get; init; }

    /// <summary>
    /// Number of slice-4 style diagnostics classified as
    /// <c>manual-review</c>. Per issue #1030 AC, these block any
    /// visual-parity claim.
    /// </summary>
    public int StyleManualReviewCount { get; init; }
}

/// <summary>
/// Per-channel view of the coverage migration. The pack carries one channel
/// per supported protocol so OGC API Coverages and legacy WCS imports are
/// rolled up uniformly.
/// </summary>
public sealed record OgcCoverageMigrationEvidencePackChannel
{
    /// <summary>
    /// Canonical channel id:
    /// <see cref="OgcCoverageMigrationEvidencePackChannelIds.OgcApiCoverages"/>
    /// or <see cref="OgcCoverageMigrationEvidencePackChannelIds.Wcs"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Whether the channel ran in apply mode. Slice-2/3 results carry this
    /// flag; it is mirrored here for audit.
    /// </summary>
    public bool ApplyMode { get; init; }

    /// <summary>
    /// Whether the channel ran in dry-run mode.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Per-channel WCS protocol version (e.g. <c>2.0.1</c>) when the channel
    /// is the legacy WCS path. Null for OGC API Coverages.
    /// </summary>
    public string? ResolvedVersion { get; init; }

    /// <summary>
    /// Per-channel WCS requested output format (e.g. <c>image/tiff</c>) when
    /// the channel is the legacy WCS path. Null for OGC API Coverages.
    /// </summary>
    public string? RequestedOutputFormat { get; init; }

    /// <summary>
    /// Number of per-coverage records in the channel.
    /// </summary>
    public int CoverageCount { get; init; }

    /// <summary>
    /// Number of records with action <c>imported</c>.
    /// </summary>
    public int ImportedCount { get; init; }

    /// <summary>
    /// Number of records with action <c>planned</c>.
    /// </summary>
    public int PlannedCount { get; init; }

    /// <summary>
    /// Number of records with action <c>skipped</c>.
    /// </summary>
    public int SkippedCount { get; init; }

    /// <summary>
    /// Number of records with action <c>manual-review</c>.
    /// </summary>
    public int ManualReviewCount { get; init; }

    /// <summary>
    /// Number of records with action <c>failed</c>.
    /// </summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// Per-coverage records ordered by
    /// <see cref="OgcCoverageImportRecord.SourceCoverageId"/>.
    /// </summary>
    public OgcCoverageImportRecord[] Records { get; init; } = [];

    /// <summary>
    /// Per-coverage migration manifest emitted by the channel. Source URLs
    /// are redacted in the embedded artifact.
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }
}

/// <summary>
/// Canonical channel identifiers used by the OGC coverage migration
/// evidence pack.
/// </summary>
public static class OgcCoverageMigrationEvidencePackChannelIds
{
    /// <summary>Modern OGC API Coverages import channel (slice 2).</summary>
    public const string OgcApiCoverages = "ogc-api-coverages";

    /// <summary>Legacy OGC WCS coverage import channel (slice 3).</summary>
    public const string Wcs = "ogc-wcs";
}
