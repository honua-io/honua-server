// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Result of an OGC coverage GeoTIFF/COG import attempt.
/// </summary>
public sealed record OgcCoverageImportResult
{
    /// <summary>
    /// Deterministic per-source migration manifest summarizing the import attempt.
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }

    /// <summary>
    /// Per-coverage execution records. Empty in dry-run mode.
    /// </summary>
    public OgcCoverageImportRecord[] Records { get; init; } = [];

    /// <summary>
    /// Whether the request ran in apply mode.
    /// </summary>
    public bool ApplyMode { get; init; }

    /// <summary>
    /// Whether the request was a dry-run preview.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Coverage-style migration diagnostics emitted while inspecting source
    /// coverage style hints. Empty when no inventoried coverage advertised a
    /// color map, band statistics, NoData marker, transparency preset, legend,
    /// or rendering hint that required separate operator attention.
    /// Slice 4 of issue #1030.
    /// </summary>
    public MigrationCoverageStyleDiagnostic[] StyleDiagnostics { get; init; } = [];
}

/// <summary>
/// Outcome for a single coverage processed by the import service.
/// </summary>
public sealed record OgcCoverageImportRecord
{
    /// <summary>
    /// Source coverage identifier.
    /// </summary>
    public required string SourceCoverageId { get; init; }

    /// <summary>
    /// Target raster name registered (or planned) in Honua.
    /// </summary>
    public required string TargetCoverageName { get; init; }

    /// <summary>
    /// Output format requested from the source service ( <c>GeoTIFF</c>, <c>CloudOptimizedGeoTIFF</c>, etc. ).
    /// </summary>
    public required string OutputFormat { get; init; }

    /// <summary>
    /// Inventory classification: <c>automated</c>, <c>manual-review</c>, or <c>unsupported</c>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Final action: <c>imported</c>, <c>planned</c>, <c>skipped</c>, <c>manual-review</c>, or <c>failed</c>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Declared CRS values copied from the source coverage.
    /// </summary>
    public string[] DeclaredCrs { get; init; } = [];

    /// <summary>
    /// Declared range/band names copied from the source coverage.
    /// </summary>
    public string[] DeclaredBands { get; init; } = [];

    /// <summary>
    /// Number of bytes streamed from the source. Zero in dry-run and manual-review records.
    /// </summary>
    public long ByteCount { get; init; }

    /// <summary>
    /// Honua raster identifier when the import was registered.
    /// </summary>
    public long? RasterId { get; init; }

    /// <summary>
    /// Honua target layer identifier when applicable.
    /// </summary>
    public int? TargetLayerId { get; init; }

    /// <summary>
    /// Warnings emitted while processing this coverage.
    /// </summary>
    public string[] Warnings { get; init; } = [];

    /// <summary>
    /// Optional error message when <see cref="Action"/> is <c>failed</c>.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
