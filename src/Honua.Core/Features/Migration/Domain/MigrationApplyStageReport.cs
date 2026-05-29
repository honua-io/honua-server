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
/// Deterministic envelope produced by the migration acceptance apply stage. Aggregates per-source
/// <see cref="MigrationManifestArtifact"/> outputs (and an apply-plan replay token) into a single
/// artifact suitable for downstream publish, parity, and readiness stages of the acceptance suite.
/// </summary>
/// <remarks>
/// The apply stage is the second pipeline stage emitted by the migration acceptance suite described
/// in issue #1024 (scan -> manifest -> apply/dry-run -> publish -> parity -> readiness). When the
/// upstream source family does not yet support a non-destructive apply against the fixture target,
/// the runner falls back to the deterministic <see cref="MigrationApplyPlanArtifact"/> dry-run path.
/// Either way the per-source outcome on this report pins the manifest artifact and a replay token so
/// later stages can re-derive their work deterministically from the same source set.
/// </remarks>
public sealed record MigrationApplyStageReport
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.apply-stage-report";

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
    /// Stable identifier for the upstream scan stage report this apply stage was derived from.
    /// </summary>
    public required string ScanRunId { get; init; }

    /// <summary>
    /// Aggregate counts across all per-source apply outcomes.
    /// </summary>
    public required MigrationApplyStageSummary Summary { get; init; }

    /// <summary>
    /// Per-source apply stage entries, ordered deterministically by
    /// <see cref="MigrationApplyStageEntry.FixtureId"/>.
    /// </summary>
    public MigrationApplyStageEntry[] Sources { get; init; } = [];
}

/// <summary>
/// One per-source entry in a <see cref="MigrationApplyStageReport"/>.
/// </summary>
public sealed record MigrationApplyStageEntry
{
    /// <summary>
    /// Stable fixture identifier (e.g. <c>arcgis-mapserver-mixed-renderers</c>). Used to order
    /// entries and to cross-reference upstream scan and downstream parity artifacts.
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Source kind such as <c>arcgis-geoservices-rest</c>, <c>geoserver-rest</c>, or
    /// <c>ogc-api-features</c>. Mirrors <see cref="MigrationManifestArtifact.SourceKind"/>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Apply mode used for the fixture. <c>apply</c> indicates a real apply against the fixture
    /// target was executed; <c>dry-run</c> indicates the deterministic
    /// <see cref="MigrationApplyPlanArtifact"/> dry-run path was used instead (the source family
    /// does not yet support fixture-driven apply, or the fixture explicitly opts into dry-run for
    /// release gates).
    /// </summary>
    public required string ApplyMode { get; init; }

    /// <summary>
    /// Per-source apply outcome — items applied, manual-review entries, diagnostics, and the
    /// deterministic manifest the apply stage emitted for downstream consumers.
    /// </summary>
    public required MigrationApplyStageOutcome Outcome { get; init; }
}

/// <summary>
/// Per-source apply outcome rolled up into a <see cref="MigrationApplyStageEntry"/>.
/// </summary>
public sealed record MigrationApplyStageOutcome
{
    /// <summary>
    /// Deterministic manifest artifact produced for this fixture.
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }

    /// <summary>
    /// Stable replay token (SHA-256 fingerprint) over the apply-plan payload. Identical across
    /// re-runs of the same fixture set.
    /// </summary>
    public required string ReplayToken { get; init; }

    /// <summary>
    /// Number of manifest items the apply stage was able to stage automatically (i.e. apply-plan
    /// steps with disposition <c>ready</c>).
    /// </summary>
    public int AppliedItemCount { get; init; }

    /// <summary>
    /// Number of manifest items routed to operator review (i.e. apply-plan steps with disposition
    /// <c>manual-review</c>).
    /// </summary>
    public int ManualReviewItemCount { get; init; }

    /// <summary>
    /// Number of manifest items that this apply path does not support (i.e. apply-plan steps with
    /// disposition <c>unsupported</c>).
    /// </summary>
    public int UnsupportedItemCount { get; init; }

    /// <summary>
    /// Per-item classifications recorded by the apply stage. Ordered deterministically by
    /// <see cref="MigrationApplyStageItemClassification.SourceId"/>.
    /// </summary>
    public MigrationApplyStageItemClassification[] Classifications { get; init; } = [];

    /// <summary>
    /// Manual-review entries copied from the manifest for fixture-level review. Ordered by
    /// <see cref="MigrationManifestReviewItem.SourceId"/> then <see cref="MigrationManifestReviewItem.Code"/>.
    /// </summary>
    public MigrationManifestReviewItem[] ManualReviewItems { get; init; } = [];

    /// <summary>
    /// Apply-stage diagnostics. These are operator-facing notes that surface non-fatal issues that
    /// did not prevent the manifest from being emitted (e.g. a style format that requires manual
    /// review, an unsupported renderer that was deliberately not auto-migrated).
    /// </summary>
    public MigrationApplyStageDiagnostic[] Diagnostics { get; init; } = [];
}

/// <summary>
/// One per-item classification recorded by the apply stage.
/// </summary>
public sealed record MigrationApplyStageItemClassification
{
    /// <summary>
    /// Source manifest item identifier.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Source item kind such as <c>feature-layer</c> or <c>style</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Disposition assigned by the apply stage: <c>applied</c>, <c>manual-review</c>, or
    /// <c>unsupported</c>.
    /// </summary>
    public required string Disposition { get; init; }

    /// <summary>
    /// Apply-stage action recorded for the item, such as <c>stage-catalog-resource</c>,
    /// <c>stage-style</c>, <c>manual-review</c>, or <c>unsupported</c>.
    /// </summary>
    public required string Action { get; init; }
}

/// <summary>
/// Apply-stage diagnostic note surfaced to operators.
/// </summary>
public sealed record MigrationApplyStageDiagnostic
{
    /// <summary>
    /// Stable machine-readable diagnostic code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Diagnostic severity such as <c>info</c>, <c>manual-review</c>, or <c>unsupported</c>.
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
/// Aggregate counts rolled up across all apply-stage entries in a report.
/// </summary>
public sealed record MigrationApplyStageSummary
{
    /// <summary>
    /// Total number of fixture sources processed by the apply stage.
    /// </summary>
    public int SourceCount { get; init; }

    /// <summary>
    /// Number of sources that ran in <c>apply</c> mode.
    /// </summary>
    public int AppliedSourceCount { get; init; }

    /// <summary>
    /// Number of sources that ran in <c>dry-run</c> mode.
    /// </summary>
    public int DryRunSourceCount { get; init; }

    /// <summary>
    /// Total number of manifest items applied across all sources.
    /// </summary>
    public int AppliedItemCount { get; init; }

    /// <summary>
    /// Total number of manifest items routed to operator review across all sources.
    /// </summary>
    public int ManualReviewItemCount { get; init; }

    /// <summary>
    /// Total number of manifest items rejected as unsupported across all sources.
    /// </summary>
    public int UnsupportedItemCount { get; init; }

    /// <summary>
    /// Total number of apply-stage diagnostics emitted across all sources.
    /// </summary>
    public int DiagnosticCount { get; init; }
}
