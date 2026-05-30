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
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Deterministic envelope produced by the migration acceptance parity stage. Aggregates per-source
/// parity outcomes (feature-count match, schema-field-name match, bbox-overlap check) into a single
/// artifact suitable for downstream readiness gates of the acceptance suite.
/// </summary>
/// <remarks>
/// The parity stage is the third pipeline stage emitted by the migration acceptance suite described
/// in issue #1024 (scan -> manifest -> apply/dry-run -> publish -> parity -> readiness). Parity
/// probes compare the applied target state (carried on the slice-3
/// <see cref="MigrationManifestArtifact"/> output of the apply stage) against the original
/// <see cref="MigrationSourceInventoryArtifact"/>. Slice 4 keeps the probe set deliberately small —
/// feature count, schema field-name set, and an axis-aligned bbox overlap — so the parity stage can
/// classify fixture sources deterministically without depending on heavyweight per-feature checks.
/// Geometry hashing, attribute distribution, and live target probing belong in later slices.
/// </remarks>
public sealed record MigrationParityStageReport
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.parity-stage-report";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable identifier for the acceptance run that produced this report. Callers supply a
    /// deterministic value (e.g. a fixture set name) so the report can be diffed across runs.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Stable identifier for the upstream apply stage report this parity stage was derived from.
    /// </summary>
    public required string ApplyRunId { get; init; }

    /// <summary>
    /// Aggregate counts across all per-source parity outcomes.
    /// </summary>
    public required MigrationParityStageSummary Summary { get; init; }

    /// <summary>
    /// Per-source parity stage entries, ordered deterministically by
    /// <see cref="MigrationParityStageEntry.FixtureId"/>.
    /// </summary>
    public MigrationParityStageEntry[] Sources { get; init; } = [];
}

/// <summary>
/// One per-source entry in a <see cref="MigrationParityStageReport"/>.
/// </summary>
public sealed record MigrationParityStageEntry
{
    /// <summary>
    /// Stable fixture identifier (e.g. <c>arcgis-mapserver-mixed-renderers</c>). Used to order
    /// entries and to cross-reference upstream scan/apply artifacts.
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Source kind such as <c>arcgis-geoservices-rest</c>, <c>geoserver-rest</c>, or
    /// <c>ogc-api-features</c>. Mirrors <see cref="MigrationManifestArtifact.SourceKind"/>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Aggregate parity classification for the fixture: <c>pass</c>, <c>warn</c>,
    /// <c>fail</c>, or <c>manual-review</c>. Mirrors the per-section thresholds rolled up by
    /// <c>MigrationAcceptanceParityStageRunner</c>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Per-source parity outcome — counts, schema diffs, extent diffs, and the parity evidence
    /// artifact produced for downstream readiness consumers.
    /// </summary>
    public required MigrationParityStageOutcome Outcome { get; init; }
}

/// <summary>
/// Per-source parity outcome rolled up into a <see cref="MigrationParityStageEntry"/>.
/// </summary>
public sealed record MigrationParityStageOutcome
{
    /// <summary>
    /// Deterministic parity evidence artifact produced for this fixture by the parity stage.
    /// </summary>
    public required MigrationParityEvidenceArtifact Evidence { get; init; }

    /// <summary>
    /// Stable replay token (SHA-256 fingerprint) over the parity probe payload. Identical across
    /// re-runs of the same fixture set.
    /// </summary>
    public required string ReplayToken { get; init; }

    /// <summary>
    /// Number of source resources the parity stage compared.
    /// </summary>
    public int ResourceCount { get; init; }

    /// <summary>
    /// Number of resources whose feature counts matched the manifest target state.
    /// </summary>
    public int FeatureCountMatchCount { get; init; }

    /// <summary>
    /// Number of resources whose feature counts diverged between source and target.
    /// </summary>
    public int FeatureCountMismatchCount { get; init; }

    /// <summary>
    /// Number of resources whose schema field-name set matched the manifest target state.
    /// </summary>
    public int SchemaMatchCount { get; init; }

    /// <summary>
    /// Number of resources whose schema field-name set diverged.
    /// </summary>
    public int SchemaMismatchCount { get; init; }

    /// <summary>
    /// Number of resources whose bbox overlap probe passed.
    /// </summary>
    public int BboxOverlapMatchCount { get; init; }

    /// <summary>
    /// Number of resources whose bbox overlap probe failed (disjoint envelopes).
    /// </summary>
    public int BboxOverlapMismatchCount { get; init; }

    /// <summary>
    /// Number of probes that were not applicable (e.g. the source did not advertise a
    /// feature count or extent metadata for the resource).
    /// </summary>
    public int NotApplicableProbeCount { get; init; }

    /// <summary>
    /// Number of resources routed to manual review (e.g. manifest disposition was
    /// <c>manual-review</c> or <c>unsupported</c> so no target observation is available).
    /// </summary>
    public int ManualReviewResourceCount { get; init; }

    /// <summary>
    /// Per-resource parity probes recorded by the parity stage. Ordered deterministically by
    /// <see cref="MigrationParityResourceProbe.SourceResourceId"/>.
    /// </summary>
    public MigrationParityResourceProbe[] ResourceProbes { get; init; } = [];

    /// <summary>
    /// Parity-stage diagnostics. Operator-facing notes that surface non-fatal issues such as
    /// missing extent metadata or partially captured schemas.
    /// </summary>
    public MigrationParityStageDiagnostic[] Diagnostics { get; init; } = [];
}

/// <summary>
/// One per-resource parity probe recorded by the parity stage.
/// </summary>
public sealed record MigrationParityResourceProbe
{
    /// <summary>
    /// Source inventory resource identifier.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Target manifest resource identifier, when the apply stage emitted one.
    /// </summary>
    public string? TargetResourceId { get; init; }

    /// <summary>
    /// Manifest action recorded for the resource (e.g. <c>publish</c>, <c>manual-review</c>).
    /// </summary>
    public required string ManifestAction { get; init; }

    /// <summary>
    /// Aggregate per-resource classification: <c>pass</c>, <c>warn</c>, <c>fail</c>, or
    /// <c>manual-review</c>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Feature-count probe outcome.
    /// </summary>
    public required MigrationParityProbeResult FeatureCount { get; init; }

    /// <summary>
    /// Schema field-name probe outcome.
    /// </summary>
    public required MigrationParityProbeResult Schema { get; init; }

    /// <summary>
    /// Bbox overlap probe outcome.
    /// </summary>
    public required MigrationParityProbeResult BboxOverlap { get; init; }
}

/// <summary>
/// One per-probe result captured by the parity stage.
/// </summary>
public sealed record MigrationParityProbeResult
{
    /// <summary>
    /// Stable probe identifier such as <c>feature-count</c>, <c>schema-field-names</c>, or
    /// <c>bbox-overlap</c>.
    /// </summary>
    public required string ProbeId { get; init; }

    /// <summary>
    /// Probe state: <c>pass</c>, <c>warn</c>, <c>fail</c>, <c>manual-review</c>, or
    /// <c>not-applicable</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Human-readable summary suitable for operator review. Must not contain credentials.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Optional observed value from the source side (e.g. source feature count).
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>
    /// Optional observed value from the target side (e.g. target feature count).
    /// </summary>
    public string? Observed { get; init; }

    /// <summary>
    /// Optional deterministic difference description (e.g. field-name set diff or extent diff).
    /// </summary>
    public string[] Differences { get; init; } = [];
}

/// <summary>
/// Parity-stage diagnostic note surfaced to operators.
/// </summary>
public sealed record MigrationParityStageDiagnostic
{
    /// <summary>
    /// Stable machine-readable diagnostic code such as <c>parity.feature-count.mismatch</c>,
    /// <c>parity.schema.mismatch</c>, or <c>parity.bbox.disjoint</c>.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Diagnostic severity such as <c>warn</c>, <c>fail</c>, or <c>manual-review</c>.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Source manifest item identifier the diagnostic refers to.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Human-readable diagnostic message. Must not contain credentials or other secrets.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Aggregate counts rolled up across all parity-stage entries in a report.
/// </summary>
public sealed record MigrationParityStageSummary
{
    /// <summary>
    /// Total number of fixture sources processed by the parity stage.
    /// </summary>
    public int SourceCount { get; init; }

    /// <summary>
    /// Number of fixture sources classified as <c>pass</c>.
    /// </summary>
    public int PassSourceCount { get; init; }

    /// <summary>
    /// Number of fixture sources classified as <c>warn</c>.
    /// </summary>
    public int WarnSourceCount { get; init; }

    /// <summary>
    /// Number of fixture sources classified as <c>fail</c>.
    /// </summary>
    public int FailSourceCount { get; init; }

    /// <summary>
    /// Number of fixture sources classified as <c>manual-review</c>.
    /// </summary>
    public int ManualReviewSourceCount { get; init; }

    /// <summary>
    /// Total number of source resources compared across all fixture sources.
    /// </summary>
    public int ResourceCount { get; init; }

    /// <summary>
    /// Total number of feature-count probe matches across all sources.
    /// </summary>
    public int FeatureCountMatchCount { get; init; }

    /// <summary>
    /// Total number of feature-count probe mismatches across all sources.
    /// </summary>
    public int FeatureCountMismatchCount { get; init; }

    /// <summary>
    /// Total number of schema probe matches across all sources.
    /// </summary>
    public int SchemaMatchCount { get; init; }

    /// <summary>
    /// Total number of schema probe mismatches across all sources.
    /// </summary>
    public int SchemaMismatchCount { get; init; }

    /// <summary>
    /// Total number of bbox overlap probe matches across all sources.
    /// </summary>
    public int BboxOverlapMatchCount { get; init; }

    /// <summary>
    /// Total number of bbox overlap probe mismatches across all sources.
    /// </summary>
    public int BboxOverlapMismatchCount { get; init; }

    /// <summary>
    /// Total number of parity-stage diagnostics emitted across all sources.
    /// </summary>
    public int DiagnosticCount { get; init; }
}

/// <summary>
/// Optional per-resource observation from the actual target after apply. When supplied, the parity
/// runner uses these observations as the target side of each probe; otherwise it derives the target
/// observation deterministically from the manifest (which mirrors the source inventory by
/// construction for fixture-driven dry-run apply).
/// </summary>
public sealed record MigrationParityTargetObservation
{
    /// <summary>
    /// Source resource identifier the observation refers to.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Observed feature count on the target after apply. <c>null</c> when not measured.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Observed schema field-name set on the target after apply. <c>null</c> when not measured.
    /// </summary>
    public string[]? FieldNames { get; init; }

    /// <summary>
    /// Observed axis-aligned bbox on the target after apply, in source CRS, as
    /// <c>[minX, minY, maxX, maxY]</c>. <c>null</c> when not measured.
    /// </summary>
    public double[]? Bbox { get; init; }
}

/// <summary>
/// Optional per-resource observation from the source side. When supplied, overrides the source
/// inventory's reported counts/fields/bbox for the parity probe (used by callers that have probed
/// the live source after the inventory was captured).
/// </summary>
public sealed record MigrationParitySourceObservation
{
    /// <summary>
    /// Source resource identifier the observation refers to.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Observed feature count on the source. <c>null</c> falls back to the inventory value.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Observed schema field-name set on the source. <c>null</c> falls back to the inventory value.
    /// </summary>
    public string[]? FieldNames { get; init; }

    /// <summary>
    /// Observed axis-aligned bbox on the source, in source CRS, as
    /// <c>[minX, minY, maxX, maxY]</c>. <c>null</c> falls back to inventory metadata when present.
    /// </summary>
    public double[]? Bbox { get; init; }
}
