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
/// Deterministic envelope produced by the migration acceptance readiness stage. Aggregates the
/// per-source <see cref="MigrationReadinessAttestationArtifact"/> outputs into a single report that
/// gates whether a source set is safe to cut over.
/// </summary>
/// <remarks>
/// <para>
/// The readiness stage is the final pipeline stage emitted by the migration acceptance suite
/// described in issue #1024 (scan -> manifest -> apply/dry-run -> publish -> parity -> readiness).
/// It consumes the outputs of the slice-2 scan stage, the slice-3 apply stage, and the slice-4
/// parity stage and emits one <see cref="MigrationReadinessAttestationArtifact"/> per source plus
/// an aggregate report summary.
/// </para>
/// <para>
/// The classification rules implemented by <c>MigrationAcceptanceReadinessStageRunner</c> are:
/// every parity probe is <c>pass</c> and no manual-review items remain -> <c>ready</c>;
/// at least one probe is <c>fail</c> -> <c>not-ready</c>;
/// otherwise (only manual-review items, or parity warnings without failures) -> <c>conditional</c>.
/// </para>
/// </remarks>
public sealed record MigrationReadinessStageReport
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.readiness-stage-report";

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
    /// Stable identifier for the upstream scan stage report this readiness stage was derived from.
    /// </summary>
    public required string ScanRunId { get; init; }

    /// <summary>
    /// Stable identifier for the upstream apply stage report this readiness stage was derived from.
    /// </summary>
    public required string ApplyRunId { get; init; }

    /// <summary>
    /// Stable identifier for the upstream parity stage report this readiness stage was derived
    /// from.
    /// </summary>
    public required string ParityRunId { get; init; }

    /// <summary>
    /// Aggregate counts across all per-source readiness attestations.
    /// </summary>
    public required MigrationReadinessStageSummary Summary { get; init; }

    /// <summary>
    /// Per-source readiness stage entries, ordered deterministically by
    /// <see cref="MigrationReadinessStageEntry.FixtureId"/>.
    /// </summary>
    public MigrationReadinessStageEntry[] Sources { get; init; } = [];
}

/// <summary>
/// One per-source entry in a <see cref="MigrationReadinessStageReport"/>.
/// </summary>
public sealed record MigrationReadinessStageEntry
{
    /// <summary>
    /// Stable fixture identifier (e.g. <c>arcgis-mapserver-mixed-renderers</c>). Used to order
    /// entries and to cross-reference upstream scan/apply/parity artifacts.
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Source kind such as <c>arcgis-geoservices-rest</c>, <c>geoserver-rest</c>, or
    /// <c>ogc-api-features</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Aggregate readiness classification for the fixture: <c>ready</c>, <c>conditional</c>, or
    /// <c>not-ready</c>. Mirrors <see cref="MigrationReadinessAttestationArtifact.Status"/>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Deterministic readiness attestation artifact for this fixture.
    /// </summary>
    public required MigrationReadinessAttestationArtifact Attestation { get; init; }
}

/// <summary>
/// Aggregate counts rolled up across all readiness attestations in a report.
/// </summary>
public sealed record MigrationReadinessStageSummary
{
    /// <summary>
    /// Total number of fixture sources processed by the readiness stage.
    /// </summary>
    public int SourceCount { get; init; }

    /// <summary>
    /// Number of fixture sources classified as <c>ready</c>.
    /// </summary>
    public int ReadySourceCount { get; init; }

    /// <summary>
    /// Number of fixture sources classified as <c>conditional</c>.
    /// </summary>
    public int ConditionalSourceCount { get; init; }

    /// <summary>
    /// Number of fixture sources classified as <c>not-ready</c>.
    /// </summary>
    public int NotReadySourceCount { get; init; }

    /// <summary>
    /// Total number of readiness reasons emitted across all sources.
    /// </summary>
    public int ReasonCount { get; init; }

    /// <summary>
    /// Total number of evidence citations recorded across all sources.
    /// </summary>
    public int EvidenceCitationCount { get; init; }
}
