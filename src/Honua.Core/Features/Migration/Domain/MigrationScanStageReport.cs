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
/// Deterministic envelope produced by the migration acceptance scan stage. Aggregates per-source
/// <see cref="MigrationSourceInventoryArtifact"/> outputs into a single artifact suitable for
/// downstream manifest, apply, and parity stages of the acceptance suite.
/// </summary>
/// <remarks>
/// The scan stage is the first of the acceptance pipeline stages described in issue #1024
/// (scan -> manifest -> apply/dry-run -> publish -> parity -> readiness). The report is emitted
/// per acceptance run and pins the inputs and outputs of the scan stage so later stages can
/// re-derive their work deterministically from the same source set.
/// </remarks>
public sealed record MigrationScanStageReport
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.scan-stage-report";

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
    /// Aggregate counts across all scanned sources.
    /// </summary>
    public required MigrationScanStageSummary Summary { get; init; }

    /// <summary>
    /// Per-source scan stage entries, ordered deterministically by <see cref="MigrationScanStageEntry.FixtureId"/>.
    /// </summary>
    public MigrationScanStageEntry[] Sources { get; init; } = [];
}

/// <summary>
/// One per-source entry in a <see cref="MigrationScanStageReport"/>.
/// </summary>
public sealed record MigrationScanStageEntry
{
    /// <summary>
    /// Stable fixture identifier (e.g. <c>arcgis-featureserver-supported</c>). Used to order
    /// entries and to cross-reference downstream manifest and parity artifacts.
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Source kind such as <c>arcgis-geoservices-rest</c>, <c>geoserver-rest</c>, or
    /// <c>ogc-api-features</c>. Mirrors <see cref="MigrationSourceInventoryArtifact.SourceKind"/>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// The deterministic inventory artifact produced by the scan stage for this source.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }
}

/// <summary>
/// Aggregate counts rolled up across all scan stage entries in a report.
/// </summary>
public sealed record MigrationScanStageSummary
{
    /// <summary>
    /// Total number of scanned sources.
    /// </summary>
    public int SourceCount { get; init; }

    /// <summary>
    /// Total number of containers discovered across all sources.
    /// </summary>
    public int ContainerCount { get; init; }

    /// <summary>
    /// Total number of resources discovered across all sources.
    /// </summary>
    public int ResourceCount { get; init; }

    /// <summary>
    /// Total number of styles discovered across all sources.
    /// </summary>
    public int StyleCount { get; init; }

    /// <summary>
    /// Total number of external dependencies discovered across all sources.
    /// </summary>
    public int ExternalDependencyCount { get; init; }

    /// <summary>
    /// Total number of fidelity classifications recorded as <c>automated</c>.
    /// </summary>
    public int AutomatedCount { get; init; }

    /// <summary>
    /// Total number of fidelity classifications recorded as <c>assisted</c>.
    /// </summary>
    public int AssistedCount { get; init; }

    /// <summary>
    /// Total number of fidelity classifications recorded as <c>manual-review</c>.
    /// </summary>
    public int ManualReviewCount { get; init; }

    /// <summary>
    /// Total number of fidelity classifications recorded as <c>unsupported</c>.
    /// </summary>
    public int UnsupportedCount { get; init; }
}
