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
/// Slice 5 of issue #1016. Single deterministic evidence bundle emitted from a
/// successful classic OGC (WFS / WMS / WMTS) migration run. Aggregates the
/// slice 1-4 outputs — the <see cref="MigrationSourceInventoryArtifact"/>
/// produced by the OGC service scanner, the <see cref="OgcWfsImportResult"/>
/// emitted by the WFS data-import service, the per-service WMS/WMTS render
/// plan entries and diagnostics produced by the dedicated render planners,
/// and the <see cref="OgcTileCacheExportResult"/> emitted by the tile-cache
/// exporter — into a single per-source artifact so reviewers (and nightly
/// fixture runs) can audit every stage of an OGC migration from one file.
/// </summary>
/// <remarks>
/// <para>
/// The pack carries a SHA-256 fingerprint computed over the canonical JSON of
/// its bundle so identical inputs always produce the same fingerprint. The
/// fingerprint deliberately excludes wall-clock timestamps and the generator
/// label so re-runs across CI images stay byte-identical.
/// </para>
/// <para>
/// Privacy posture: the bundle reuses the redaction behavior of the upstream
/// inventory/import/export artifacts. The builder additionally strips
/// userinfo, query, and fragment components from any embedded source URL
/// before the snapshots are included. The pack carries counts, deterministic
/// metadata, and plan diagnostics only — never raw feature payloads, raw
/// capabilities documents, or raw tile bytes.
/// </para>
/// <para>
/// AOT note: this record uses POCO-only properties (no polymorphic
/// converters, no <c>JsonExtensionData</c>) so the source-generated
/// <see cref="Services.OgcMigrationEvidencePackJsonContext"/> remains
/// trim/AOT safe.
/// </para>
/// </remarks>
public sealed record OgcMigrationEvidencePackArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.ogc.evidence-pack";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable run identifier supplied by the harness or nightly workflow.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Free-form generator label, e.g.
    /// <c>honua.migration.ogc.evidence-pack-builder/1.0</c>. Excluded from the
    /// bundle fingerprint so re-runs across CI images stay byte-identical.
    /// </summary>
    public required string Generator { get; init; }

    /// <summary>
    /// UTC instant the pack was generated. Excluded from the bundle fingerprint
    /// so re-runs stay byte-identical.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// SHA-256 fingerprint computed over the canonical JSON of
    /// <see cref="Bundle"/>. Identical inputs always produce the same value.
    /// </summary>
    public required string BundleFingerprint { get; init; }

    /// <summary>
    /// Deterministic evidence bundle that aggregates slice 1-4 outputs.
    /// </summary>
    public required OgcMigrationEvidencePackBundle Bundle { get; init; }
}

/// <summary>
/// Deterministic content bundle covered by
/// <see cref="OgcMigrationEvidencePackArtifact.BundleFingerprint"/>.
/// </summary>
public sealed record OgcMigrationEvidencePackBundle
{
    /// <summary>
    /// Source kind identifier such as <c>ogc-wfs</c>, <c>ogc-wms</c>, or
    /// <c>ogc-wmts</c>. Copied from the inventory artifact.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Secret-safe source identity (credentials and URL secrets stripped).
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Aggregate counts across the four migration stages.
    /// </summary>
    public required OgcMigrationEvidencePackSummary Summary { get; init; }

    /// <summary>
    /// Stage 1: inventory snapshot copied from the slice-1 OGC service scanner.
    /// Source URLs are redacted before inclusion.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Stage 2: WFS data-import outcome copied from the slice-2 import service.
    /// Source URLs are redacted before inclusion. <c>null</c> when no WFS
    /// import was executed for this source (e.g. WMS- or WMTS-only run).
    /// </summary>
    public OgcWfsImportResult? WfsImport { get; init; }

    /// <summary>
    /// Stage 3: WMS render-plan entries and diagnostics copied from the
    /// slice-3 WMS migration planner. Empty when the source did not advertise
    /// a WMS service or planning was skipped.
    /// </summary>
    public required OgcMigrationEvidencePackRenderStage WmsPlan { get; init; }

    /// <summary>
    /// Stage 3: WMTS render-plan entries and diagnostics copied from the
    /// slice-3 WMTS migration planner. Empty when the source did not advertise
    /// a WMTS service or planning was skipped.
    /// </summary>
    public required OgcMigrationEvidencePackRenderStage WmtsPlan { get; init; }

    /// <summary>
    /// Stage 4: tile-cache export outcome copied from the slice-4 exporter.
    /// Source URLs are redacted before inclusion. <c>null</c> when no tile
    /// cache export was executed for this source.
    /// </summary>
    public OgcTileCacheExportResult? TileCacheExport { get; init; }
}

/// <summary>
/// One render-only planning stage (WMS or WMTS) captured in the evidence
/// pack. Mirrors the slice-3 planner output structure
/// (<see cref="MigrationManifestPlanEntry"/> +
/// <see cref="MigrationManifestPlanDiagnostic"/>) without copying the entire
/// manifest so the pack stays compact and stage-scoped.
/// </summary>
public sealed record OgcMigrationEvidencePackRenderStage
{
    /// <summary>
    /// Service kind label: <c>ogc-wms</c>, <c>ogc-wmts</c>, or
    /// <c>none</c> when no planner ran for this stage.
    /// </summary>
    public required string ServiceKind { get; init; }

    /// <summary>
    /// Number of plan entries captured for this stage.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// Number of plan entries classified as
    /// <see cref="MigrationFidelityAutomationStatuses.Automated"/>.
    /// </summary>
    public int AutomatedCount { get; init; }

    /// <summary>
    /// Number of plan entries classified as
    /// <see cref="MigrationFidelityAutomationStatuses.Assisted"/>.
    /// </summary>
    public int AssistedCount { get; init; }

    /// <summary>
    /// Number of plan entries classified as
    /// <see cref="MigrationFidelityAutomationStatuses.ManualReview"/>.
    /// </summary>
    public int ManualReviewCount { get; init; }

    /// <summary>
    /// Number of plan entries classified as
    /// <see cref="MigrationFidelityAutomationStatuses.Unsupported"/>.
    /// </summary>
    public int UnsupportedCount { get; init; }

    /// <summary>
    /// Plan entries, ordered by <see cref="MigrationManifestPlanEntry.Id"/>.
    /// </summary>
    public MigrationManifestPlanEntry[] Entries { get; init; } = [];

    /// <summary>
    /// Plan diagnostics, ordered by <c>sourceId</c> then <c>code</c>.
    /// </summary>
    public MigrationManifestPlanDiagnostic[] Diagnostics { get; init; } = [];

    /// <summary>
    /// Empty stage placeholder used when no planner ran for a source.
    /// </summary>
    public static OgcMigrationEvidencePackRenderStage Empty(string serviceKind) =>
        new()
        {
            ServiceKind = serviceKind,
            EntryCount = 0,
            Entries = [],
            Diagnostics = []
        };
}

/// <summary>
/// Aggregate evidence summary across the four OGC migration stages.
/// </summary>
public sealed record OgcMigrationEvidencePackSummary
{
    /// <summary>
    /// Number of inventory containers discovered by the slice-1 scanner.
    /// </summary>
    public int InventoryContainerCount { get; init; }

    /// <summary>
    /// Number of inventory resources discovered by the slice-1 scanner.
    /// </summary>
    public int InventoryResourceCount { get; init; }

    /// <summary>
    /// Number of inventory styles discovered by the slice-1 scanner.
    /// </summary>
    public int InventoryStyleCount { get; init; }

    /// <summary>
    /// Whether a WFS data-import stage ran for this source.
    /// </summary>
    public bool WfsImportExecuted { get; init; }

    /// <summary>
    /// Number of WFS feature types imported (or planned during dry-run).
    /// </summary>
    public int WfsFeatureTypesImported { get; init; }

    /// <summary>
    /// Number of WFS feature types skipped because no automated path was
    /// available.
    /// </summary>
    public int WfsFeatureTypesSkipped { get; init; }

    /// <summary>
    /// Total feature count copied (or planned during dry-run) across all
    /// imported WFS feature types.
    /// </summary>
    public int WfsFeaturesCopied { get; init; }

    /// <summary>
    /// Total WMS render-plan entry count across all classifications.
    /// </summary>
    public int WmsPlanEntryCount { get; init; }

    /// <summary>
    /// Total WMTS render-plan entry count across all classifications.
    /// </summary>
    public int WmtsPlanEntryCount { get; init; }

    /// <summary>
    /// Combined render-plan entries classified as
    /// <see cref="MigrationFidelityAutomationStatuses.ManualReview"/> or
    /// <see cref="MigrationFidelityAutomationStatuses.Unsupported"/> across
    /// both WMS and WMTS stages. Surfaced separately because these entries
    /// block any automated render-parity claim and must be reviewed before
    /// cutover.
    /// </summary>
    public int RenderManualReviewOrUnsupportedCount { get; init; }

    /// <summary>
    /// Whether a tile-cache export stage ran for this source.
    /// </summary>
    public bool TileCacheExportExecuted { get; init; }

    /// <summary>
    /// Number of tile-sets exported (or planned during dry-run).
    /// </summary>
    public int TileCacheTileSetsExported { get; init; }

    /// <summary>
    /// Number of tile-sets skipped because they were classified as
    /// manual-review, exceeded a safety threshold, or errored.
    /// </summary>
    public int TileCacheTileSetsSkipped { get; init; }

    /// <summary>
    /// Total tile records persisted by the export stage.
    /// </summary>
    public int TileCacheTilesPersisted { get; init; }

    /// <summary>
    /// Total tile records that failed to fetch or persist.
    /// </summary>
    public int TileCacheTilesFailed { get; init; }
}
