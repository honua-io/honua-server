// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TemporalHistory.Domain;

/// <summary>
/// Capabilities advertised for a layer's temporal source. These flags combine operator configuration
/// with runtime checks (for example, as-of support is withdrawn when the supporting index is absent),
/// so clients can discover what history, diff, timeline, and rollback operations are actually available.
/// </summary>
public sealed record TemporalSourceCapabilityInfo
{
    /// <summary>
    /// Stable identifier of the layer this capability set describes.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// Whether as-of point-in-time queries are supported.
    /// </summary>
    public bool SupportsAsOf { get; init; }

    /// <summary>
    /// Whether checkpoint enumeration and history reads are supported.
    /// </summary>
    public bool SupportsHistory { get; init; }

    /// <summary>
    /// Whether diffs between two checkpoints are supported.
    /// </summary>
    public bool SupportsDiff { get; init; }

    /// <summary>
    /// Whether per-feature timelines are supported.
    /// </summary>
    public bool SupportsTimeline { get; init; }

    /// <summary>
    /// Whether rollback planning is supported.
    /// </summary>
    public bool SupportsRollbackPlan { get; init; }

    /// <summary>
    /// Whether approved rollback execution is supported.
    /// </summary>
    public bool SupportsRollbackExecution { get; init; }

    /// <summary>
    /// Whether geometry history is recorded and exposed.
    /// </summary>
    public bool SupportsGeometryHistory { get; init; }

    /// <summary>
    /// Whether actor/source attribution is exposed.
    /// </summary>
    public bool SupportsAttribution { get; init; }

    /// <summary>
    /// Backend strategy used by the temporal source.
    /// </summary>
    public required TemporalSourceKind SourceKind { get; init; }

    /// <summary>
    /// ISO 8601 retention duration advertised to clients (for example <c>P2Y</c>), or null when unbounded.
    /// </summary>
    public string? RetentionPolicy { get; init; }

    /// <summary>
    /// Attribution field names exposed for this source.
    /// </summary>
    public string[] AttributionFields { get; init; } = [];

    /// <summary>
    /// Declared schema-evolution tolerance for history reads.
    /// </summary>
    public SchemaEvolutionPolicy SchemaEvolution { get; init; }

    /// <summary>
    /// The CRS/SRID that diff and as-of geometries are expressed in (source CRS; no reprojection is applied).
    /// </summary>
    public int? GeometrySrid { get; init; }

    /// <summary>
    /// Non-fatal advisories explaining withdrawn capabilities (for example a missing as-of index).
    /// </summary>
    public string[] Warnings { get; init; } = [];
}
