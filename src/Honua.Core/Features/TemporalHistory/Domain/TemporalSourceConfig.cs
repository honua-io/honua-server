// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TemporalHistory.Domain;

/// <summary>
/// Operator-declared temporal-history configuration attached to a layer. This is distinct from
/// the interval-filter <c>TemporalCapabilities</c> used by WMS/WFS time queries: it describes the
/// history, diff, timeline, and rollback surfaces exposed through the temporal-history API and is
/// adjacent to — but independent of — named-version reconcile/post (honua-server#371).
/// </summary>
public sealed record TemporalSourceConfig
{
    /// <summary>
    /// Backend strategy used to reconstruct history for the layer.
    /// </summary>
    public required TemporalSourceKind SourceKind { get; init; }

    /// <summary>
    /// For <see cref="TemporalSourceKind.AuditLog"/>, the audit-log table name. When null the
    /// implementation falls back to the convention <c>{tableName}_history</c>.
    /// </summary>
    public string? HistoryTableName { get; init; }

    /// <summary>
    /// For <see cref="TemporalSourceKind.TemporalTable"/>, the <c>tstzrange</c> system-period column.
    /// Defaults to <c>sys_period</c> when null.
    /// </summary>
    public string? SystemPeriodColumn { get; init; }

    /// <summary>
    /// Column mapping used to read attribution from the history rows.
    /// </summary>
    public TemporalAttributionMapping Attribution { get; init; } = new();

    /// <summary>
    /// Whether geometry history is recorded and exposed for diffs and timelines.
    /// </summary>
    public bool GeometryHistory { get; init; } = true;

    /// <summary>
    /// ISO 8601 retention duration advertised to clients (for example <c>P2Y</c>), or null when unbounded.
    /// </summary>
    public string? RetentionPolicy { get; init; }

    /// <summary>
    /// Declared schema-evolution tolerance for history reads.
    /// </summary>
    public SchemaEvolutionPolicy SchemaEvolution { get; init; } = SchemaEvolutionPolicy.Fixed;

    /// <summary>
    /// Whether rollback execution is permitted for this layer (policy gate, independent of feasibility).
    /// </summary>
    public bool AllowRollback { get; init; }

    /// <summary>
    /// History-specific access policy; falls back to the layer's general access policy when null.
    /// </summary>
    public TemporalAccessPolicy? AccessPolicy { get; init; }
}

/// <summary>
/// Maps the attribution and revision columns recorded by a temporal source. Defaults follow the
/// canonical Honua audit-log schema (<c>feature_id, operation, changed_at, actor, source_ref,
/// correlation_id, before_attrs, after_attrs, geometry</c>).
/// </summary>
public sealed record TemporalAttributionMapping
{
    /// <summary>
    /// Column holding the stable feature/row identifier across revisions.
    /// </summary>
    public string FeatureIdColumn { get; init; } = "feature_id";

    /// <summary>
    /// Column holding the revision operation (<c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>).
    /// </summary>
    public string OperationColumn { get; init; } = "operation";

    /// <summary>
    /// Column holding the revision timestamp.
    /// </summary>
    public string ChangedAtColumn { get; init; } = "changed_at";

    /// <summary>
    /// Column holding the acting principal, or null when not recorded.
    /// </summary>
    public string? ActorColumn { get; init; } = "actor";

    /// <summary>
    /// Column linking the revision to a source operation/release, or null when not recorded.
    /// </summary>
    public string? SourceRefColumn { get; init; } = "source_ref";

    /// <summary>
    /// Column holding the change-set correlation identifier, or null when not recorded.
    /// </summary>
    public string? CorrelationIdColumn { get; init; } = "correlation_id";

    /// <summary>
    /// JSONB column holding the pre-change attributes (audit-log strategy), or null when not recorded.
    /// </summary>
    public string? BeforeAttributesColumn { get; init; } = "before_attrs";

    /// <summary>
    /// JSONB column holding the post-change attributes (audit-log strategy), or null when not recorded.
    /// </summary>
    public string? AfterAttributesColumn { get; init; } = "after_attrs";

    /// <summary>
    /// Geometry column recorded with each revision, or null when geometry history is absent.
    /// </summary>
    public string? GeometryColumn { get; init; } = "geometry";

    /// <summary>
    /// Attribution field names advertised through capability discovery.
    /// </summary>
    public string[] AdvertisedFields { get; init; } = ["actor", "source_ref", "correlation_id"];
}
