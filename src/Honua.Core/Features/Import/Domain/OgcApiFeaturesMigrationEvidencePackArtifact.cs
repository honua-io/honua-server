// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Slice 5 (capstone) of issue #1029. Per-source deterministic evidence bundle for an
/// OGC API Features migration run. Aggregates the slice-1 inventory scan, slice-2/3
/// per-collection import results (success, paging, filter/bbox/datetime pushdown),
/// slice-3 filter-scope drift records, and slice-4 schema-mapping diagnostics so
/// reviewers (and nightly fixture runs) have a single artifact to audit the source's
/// OGC API Features migration outcome.
/// </summary>
/// <remarks>
/// <para>
/// The pack carries a SHA-256 fingerprint computed over the canonical JSON of its
/// <see cref="Bundle"/> so identical inputs always produce the same fingerprint.
/// Wall-clock <see cref="GeneratedAt"/>, <see cref="RunId"/>, and <see cref="Generator"/>
/// are intentionally excluded from the fingerprint so re-runs across machines remain
/// byte-identical.
/// </para>
/// <para>
/// Privacy posture: the builder strips credentials (userinfo, query, fragment) from
/// every embedded source URL and never copies raw feature payloads, response bodies,
/// or HTTP headers. Only deterministic counts, identifiers, classifications, and
/// diagnostic messages flow into the artifact.
/// </para>
/// <para>
/// AOT note: this record uses POCO-only properties (no polymorphic converters, no
/// <c>JsonExtensionData</c>) so the source-generated
/// <c>OgcApiFeaturesMigrationEvidencePackJsonContext</c> remains trim/AOT safe.
/// </para>
/// </remarks>
public sealed record OgcApiFeaturesMigrationEvidencePackArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.ogc-api-features.evidence-pack";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable run identifier supplied by the harness or nightly workflow.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Free-form generator label, e.g. <c>ogc-api-features-migration-evidence-builder/1.0</c>.
    /// Excluded from the bundle fingerprint so re-runs across CI images stay byte-identical.
    /// </summary>
    public required string Generator { get; init; }

    /// <summary>
    /// UTC instant the pack was generated. Excluded from the bundle fingerprint so re-runs
    /// stay byte-identical.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// SHA-256 fingerprint computed over the canonical JSON of <see cref="Bundle"/>.
    /// Identical inputs always produce the same value.
    /// </summary>
    public required string BundleFingerprint { get; init; }

    /// <summary>
    /// Deterministic evidence bundle that aggregates slice 1-4 artifacts.
    /// </summary>
    public required OgcApiFeaturesMigrationEvidencePackBundle Bundle { get; init; }
}

/// <summary>
/// Deterministic content bundle covered by
/// <see cref="OgcApiFeaturesMigrationEvidencePackArtifact.BundleFingerprint"/>.
/// </summary>
public sealed record OgcApiFeaturesMigrationEvidencePackBundle
{
    /// <summary>
    /// Source kind identifier. Always <c>ogc-api-features</c> for this evidence pack.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Secret-safe source identity (credentials and URL secrets stripped).
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Aggregate counts across the slice-1 inventory and slice-2/3 collection imports.
    /// </summary>
    public required OgcApiFeaturesMigrationEvidencePackSummary Summary { get; init; }

    /// <summary>
    /// Inventory snapshot copied from the slice-1 scan input. Source URLs are redacted in place.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Per-collection import evidence in deterministic <see cref="OgcApiFeaturesMigrationEvidencePackCollectionResult.CollectionId"/>
    /// order. Each entry mirrors the public surface of <see cref="OgcApiFeaturesImportResult"/>
    /// minus warning bodies that may echo unrelated text.
    /// </summary>
    public OgcApiFeaturesMigrationEvidencePackCollectionResult[] Collections { get; init; } = [];
}

/// <summary>
/// Aggregate summary across the inventory and per-collection import results.
/// </summary>
public sealed record OgcApiFeaturesMigrationEvidencePackSummary
{
    /// <summary>
    /// Number of collections advertised by the slice-1 inventory snapshot.
    /// </summary>
    public int InventoryCollectionCount { get; init; }

    /// <summary>
    /// Number of conformance classes advertised by the slice-1 inventory snapshot.
    /// </summary>
    public int ConformanceClassCount { get; init; }

    /// <summary>
    /// Number of per-collection import attempts captured in this pack.
    /// </summary>
    public int CollectionResultCount { get; init; }

    /// <summary>
    /// Number of collection imports that succeeded.
    /// </summary>
    public int SucceededCollectionCount { get; init; }

    /// <summary>
    /// Number of collection imports that failed.
    /// </summary>
    public int FailedCollectionCount { get; init; }

    /// <summary>
    /// Total features written across all per-collection imports.
    /// </summary>
    public long TotalFeaturesImported { get; init; }

    /// <summary>
    /// Total features the importer skipped across all per-collection imports.
    /// </summary>
    public long TotalFeaturesSkipped { get; init; }

    /// <summary>
    /// Total pages fetched across all per-collection imports.
    /// </summary>
    public long TotalPagesFetched { get; init; }

    /// <summary>
    /// Number of collection imports whose page or feature limit truncated the run.
    /// </summary>
    public int TruncatedCollectionCount { get; init; }

    /// <summary>
    /// Number of collection imports that detected slice-3 filter-scope drift relative to a
    /// previous run against the same target. Per issue #1029 AC these are flagged for
    /// manual reconciliation.
    /// </summary>
    public int ScopeDriftCollectionCount { get; init; }

    /// <summary>
    /// Number of schema-mapping diagnostics aggregated across all per-collection imports.
    /// </summary>
    public int TotalSchemaMappingDiagnosticCount { get; init; }

    /// <summary>
    /// Number of slice-4 manual-review schema-mapping diagnostics aggregated across all
    /// per-collection imports. Per the slice-4 classification taxonomy, these block any
    /// automated cutover claim until an operator resolves them.
    /// </summary>
    public int SchemaMappingManualReviewCount { get; init; }

    /// <summary>
    /// Number of slice-4 unsupported schema-mapping diagnostics aggregated across all
    /// per-collection imports.
    /// </summary>
    public int SchemaMappingUnsupportedCount { get; init; }
}

/// <summary>
/// Per-collection evidence entry. Mirrors the slice-2/3 import result so reviewers can
/// audit paging, scope, and schema-mapping outcomes from the pack without loading the
/// raw service response.
/// </summary>
public sealed record OgcApiFeaturesMigrationEvidencePackCollectionResult
{
    /// <summary>
    /// OGC API Features collection identifier that was imported.
    /// </summary>
    public required string CollectionId { get; init; }

    /// <summary>
    /// Whether the per-collection import completed without error.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Schema-qualified target identifier the sink wrote to, in <c>schema.table</c> form.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// Number of features written to the sink for this collection.
    /// </summary>
    public int FeaturesImported { get; init; }

    /// <summary>
    /// Number of features the importer could not project for this collection.
    /// </summary>
    public int FeaturesSkipped { get; init; }

    /// <summary>
    /// Number of pages fetched from the source for this collection.
    /// </summary>
    public int PagesFetched { get; init; }

    /// <summary>
    /// Whether the source advertised additional pages that were suppressed because the
    /// importer reached its configured page or feature limit.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Optional stable error code copied from the import result when <see cref="Success"/>
    /// is <c>false</c>.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Slice-3 filter-scope record summarizing the pushdown applied to this run and any
    /// drift detected relative to the previous run against the same target.
    /// </summary>
    public required OgcApiFeaturesMigrationEvidencePackFilterScope FilterScope { get; init; }

    /// <summary>
    /// Slice-4 schema-mapping diagnostics that needed operator attention. Clean (automated)
    /// matches are intentionally omitted so the pack only carries actionable evidence.
    /// </summary>
    public OgcApiFeaturesSchemaMappingDiagnostic[] MappingDiagnostics { get; init; } = [];
}

/// <summary>
/// Slice-3 filter-scope record captured for a per-collection import.
/// </summary>
/// <remarks>
/// The record carries the normalized filter / bbox / datetime tokens that the importer
/// applied as OGC API Features query parameters, so reviewers can audit pushdown without
/// loading the importer's debug logs. Whether scope drift was detected relative to the
/// previous run against the same target is surfaced via <see cref="ScopeDriftDetected"/>.
/// </remarks>
public sealed record OgcApiFeaturesMigrationEvidencePackFilterScope
{
    /// <summary>
    /// Normalized CQL2-text filter applied to the items endpoint, or <c>null</c> when no
    /// filter was supplied.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Normalized bbox pushed through to the items endpoint, or <c>null</c> when no bbox
    /// was supplied.
    /// </summary>
    public string? Bbox { get; init; }

    /// <summary>
    /// Normalized RFC3339 instant or interval pushed through to the items endpoint, or
    /// <c>null</c> when no datetime was supplied.
    /// </summary>
    public string? Datetime { get; init; }

    /// <summary>
    /// Whether the slice-3 sink scope check detected a different filter/bbox/datetime
    /// signature relative to the previous run against the same target.
    /// </summary>
    public bool ScopeDriftDetected { get; init; }

    /// <summary>
    /// Optional manual-review reason emitted when <see cref="ScopeDriftDetected"/> is
    /// <c>true</c>. Per issue #1029 AC the importer surfaces this so operators can route
    /// it through the migration manifest review queue.
    /// </summary>
    public string? ManualReviewReason { get; init; }
}
