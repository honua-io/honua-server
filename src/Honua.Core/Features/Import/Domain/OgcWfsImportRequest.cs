// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to import feature data from an OGC Web Feature Service into Honua PostGIS.
/// </summary>
public sealed record OgcWfsImportRequest
{
    /// <summary>
    /// Optional job identifier used for log correlation.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// WFS service URL. May be a service root or an existing <c>GetCapabilities</c> URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional preferred WFS version such as <c>2.0.0</c>, <c>1.1.0</c>, or <c>1.0.0</c>.
    /// When omitted, the server-advertised version is used.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Optional username for HTTP Basic authentication against the WFS endpoint.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password for HTTP Basic authentication against the WFS endpoint.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional list of source feature type names to import. When omitted, all
    /// advertised feature types are imported.
    /// </summary>
    public string[]? FeatureTypeNames { get; init; }

    /// <summary>
    /// Target PostGIS schema for the imported feature tables. When omitted, the
    /// service falls back to the default operational schema.
    /// </summary>
    public string? TargetSchema { get; init; }

    /// <summary>
    /// Whether to drop and recreate the target table when it already exists.
    /// </summary>
    public bool OverwriteExisting { get; init; }

    /// <summary>
    /// Whether to perform a dry run (manifest preview only, no PostGIS writes).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Explicit operator opt-in for catalog mutation. When <c>DryRun</c> is <c>false</c>,
    /// the import endpoint rejects the request unless <c>ApplyMode</c> is <c>true</c>.
    /// </summary>
    public bool ApplyMode { get; init; }

    /// <summary>
    /// Optional target SRID. When omitted, the source geometry SRID is preserved.
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Page size for paged <c>GetFeature</c> requests. Default is 1000.
    /// </summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>
    /// Per-request timeout in seconds for WFS HTTP calls.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Whether HTTP and local URLs are allowed for test or controlled operator environments.
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }
}

/// <summary>
/// Result of an OGC WFS data import operation.
/// </summary>
public sealed record OgcWfsImportResult
{
    /// <summary>
    /// Whether the import completed without a top-level error.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Whether this was a dry run (no PostGIS writes occurred).
    /// </summary>
    public bool WasDryRun { get; init; }

    /// <summary>
    /// Source WFS URL the import ran against.
    /// </summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>
    /// Resolved WFS version reported by the source.
    /// </summary>
    public string? SourceVersion { get; init; }

    /// <summary>
    /// Number of feature types planned for import (including skipped or unsupported entries).
    /// </summary>
    public int FeatureTypesPlanned { get; init; }

    /// <summary>
    /// Number of feature types that produced (or would produce) a PostGIS table.
    /// </summary>
    public int FeatureTypesImported { get; init; }

    /// <summary>
    /// Number of feature types skipped because no automated path is available.
    /// </summary>
    public int FeatureTypesSkipped { get; init; }

    /// <summary>
    /// Total feature count copied across all imported feature types.
    /// </summary>
    public int FeaturesCopied { get; init; }

    /// <summary>
    /// Total feature count that failed to insert.
    /// </summary>
    public int FeaturesFailed { get; init; }

    /// <summary>
    /// Per-feature-type outcome records.
    /// </summary>
    public IReadOnlyList<OgcWfsImportedFeatureType> FeatureTypes { get; init; } = [];

    /// <summary>
    /// Top-level warnings encountered during the import.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Top-level error message when <see cref="Success"/> is <c>false</c>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Deterministic manifest artifact describing what was imported.
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }

    /// <summary>
    /// Initial parity record summarizing per-feature-type counts for the imported items.
    /// </summary>
    public required MigrationParityEvidenceArtifact ParityEvidence { get; init; }

    /// <summary>
    /// Import duration.
    /// </summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Per-feature-type record describing one WFS import outcome.
/// </summary>
public sealed record OgcWfsImportedFeatureType
{
    /// <summary>
    /// Source feature type name (typically <c>workspace:layer</c>).
    /// </summary>
    public required string SourceName { get; init; }

    /// <summary>
    /// Target PostGIS schema where the data was written. Populated for imported records.
    /// </summary>
    public string? TargetSchema { get; init; }

    /// <summary>
    /// Target PostGIS table name. Populated for imported records.
    /// </summary>
    public string? TargetTable { get; init; }

    /// <summary>
    /// Geometry type captured from the source schema, when available.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Geometry SRID resolved for the imported table.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Number of features successfully copied for this feature type.
    /// </summary>
    public int FeaturesCopied { get; init; }

    /// <summary>
    /// Number of features that failed to insert for this feature type.
    /// </summary>
    public int FeaturesFailed { get; init; }

    /// <summary>
    /// Fidelity classification, one of <see cref="MigrationFidelityAutomationStatuses"/>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Per-feature-type warnings (e.g. mixed geometry, unsupported types).
    /// </summary>
    public string[] Warnings { get; init; } = [];
}
