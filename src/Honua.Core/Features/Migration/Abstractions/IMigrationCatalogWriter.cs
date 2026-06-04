// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Idempotent catalog writes used by migration apply paths to persist workspace
/// and layer-group catalog entries before per-layer publishing runs.
/// </summary>
/// <remarks>
/// Slice 1 limited this abstraction to catalog-level upserts. Slice 2 extends it
/// with idempotent data-source persistence and idempotent in-database feature
/// data copy. Slice 3 (issue #1015) adds idempotent style persistence with
/// structured conversion diagnostics so the apply path can record manual-review
/// evidence when SLD-to-MapLibre conversion cannot guarantee visual parity.
/// </remarks>
public interface IMigrationCatalogWriter
{
    /// <summary>
    /// Ensure that a Honua catalog service entry exists. Idempotent: returns
    /// <see cref="MigrationCatalogWriteOutcome.Created"/> on first apply and
    /// <see cref="MigrationCatalogWriteOutcome.AlreadyExists"/> on re-apply.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for the target catalog.</param>
    /// <param name="request">Service definition to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
        string connectionString,
        MigrationCatalogServiceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensure that a migration data-source row exists in
    /// <c>honua.migration_data_sources</c>. Idempotent: returns
    /// <see cref="MigrationCatalogWriteOutcome.Created"/> on first apply and
    /// <see cref="MigrationCatalogWriteOutcome.AlreadyExists"/> on re-apply.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for the target catalog.</param>
    /// <param name="request">Data-source definition to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationCatalogWriteOutcome> EnsureDataSourceAsync(
        string connectionString,
        MigrationDataSourceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copy feature data from a source PostGIS table into a target Honua catalog
    /// table. Idempotent: if the target table already exists with rows, the call
    /// returns <see cref="MigrationFeatureCopyStatus.AlreadyApplied"/> without
    /// re-inserting; on the first apply it creates the target table via
    /// <c>CREATE TABLE ... LIKE</c> and copies rows, returning
    /// <see cref="MigrationFeatureCopyStatus.Copied"/>.
    /// </summary>
    /// <remarks>
    /// The copy is intentionally constrained to a single PostgreSQL instance
    /// (source schema/table in the same database as the target). Cross-database
    /// copies remain deferred to follow-on slices.
    /// </remarks>
    /// <param name="connectionString">PostgreSQL connection string for the target catalog.</param>
    /// <param name="request">Feature-copy request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationFeatureCopyOutcome> CopyFeatureDataAsync(
        string connectionString,
        MigrationFeatureCopyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensure that a migration style row exists in <c>honua.migration_styles</c>.
    /// Idempotent: returns <see cref="MigrationCatalogWriteOutcome.Created"/> on
    /// first apply and <see cref="MigrationCatalogWriteOutcome.AlreadyExists"/>
    /// on re-apply. The persisted row keeps the original SLD body, any converted
    /// MapLibre body, and structured conversion diagnostics so operators can
    /// audit manual-review outcomes.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for the target catalog.</param>
    /// <param name="request">Style definition to persist (slice 3 of #1015).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationCatalogWriteOutcome> EnsureStyleAsync(
        string connectionString,
        MigrationStyleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently persist relationship metadata for an imported layer set
    /// (issue #1256). Writes both the canonical Metadata v2 graph entry
    /// (<see cref="Honua.Core.Features.Metadata.Domain.V2.MetadataV2Resource.Relationships"/>
    /// on the origin resource) and the v1 <c>honua.relationships</c> row used by
    /// the GeoServices FeatureServer <c>queryRelatedRecords</c> path.
    /// </summary>
    /// <remarks>
    /// All requests in a single call MUST target the same target database. Per
    /// request, both writes are idempotent: the v2 graph append checks for an
    /// existing relationship by stable id, and the v1 insert uses
    /// <c>ON CONFLICT (layer_id, relationship_id) DO NOTHING</c>. Relationships
    /// that already exist on either side return
    /// <see cref="MigrationCatalogWriteOutcome.AlreadyExists"/>.
    /// </remarks>
    /// <param name="connectionString">PostgreSQL connection string for the target catalog.</param>
    /// <param name="graphStore">Read-write graph store used to mutate the v2 graph snapshot. May be <c>null</c>; when null the v2 graph write is skipped and only the v1 row is written.</param>
    /// <param name="requests">Per-relationship apply requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-relationship outcomes ordered by source relationship id.</returns>
    Task<MigrationRelationshipApplyOutcome[]> EnsureRelationshipsAsync(
        string connectionString,
        IMetadataV2GraphStore? graphStore,
        MigrationRelationshipApplyRequest[] requests,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request payload for <see cref="IMigrationCatalogWriter.EnsureCatalogServiceAsync"/>.
/// </summary>
public sealed record MigrationCatalogServiceRequest
{
    /// <summary>
    /// Stable service name (case-insensitive, lower-case kebab/snake form).
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Human-readable description for the catalog row.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Default SRID for the service. The catalog requires a value; 4326 is used
    /// when the source did not declare one.
    /// </summary>
    public int Srid { get; init; } = 4326;

    /// <summary>
    /// Catalog entry kind, such as <c>workspace</c> or <c>layer-group</c>. Used in
    /// telemetry and not persisted by the default writer schema.
    /// </summary>
    public string EntryKind { get; init; } = "workspace";
}

/// <summary>
/// Outcome of an idempotent catalog write.
/// </summary>
public enum MigrationCatalogWriteOutcome
{
    /// <summary>
    /// The catalog entry was newly created by this write.
    /// </summary>
    Created,

    /// <summary>
    /// The catalog entry already existed; no mutation was performed.
    /// </summary>
    AlreadyExists
}

/// <summary>
/// Request payload for <see cref="IMigrationCatalogWriter.EnsureDataSourceAsync"/>.
/// Persists evidence that a source data store (e.g. PostGIS, GeoPackage, shapefile)
/// has been wired up by an apply slice.
/// </summary>
public sealed record MigrationDataSourceRequest
{
    /// <summary>
    /// Source kind for the migration run (e.g. <c>geoserver-rest</c>).
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Stable id of the data source in the source system. For GeoServer this is
    /// <c>datastore:&lt;workspace&gt;:&lt;name&gt;</c>.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Data-store type (e.g. <c>PostGIS</c>, <c>GeoPackage</c>, <c>Shapefile</c>).
    /// </summary>
    public required string DataSourceType { get; init; }

    /// <summary>
    /// Owning workspace, when the source data store is workspace-scoped.
    /// </summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// Operator-visible display name. Defaults to the source id when omitted.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Sanitized connection summary (host + database for PostGIS, file path
    /// stem for file stores). Must not include secrets.
    /// </summary>
    public string ConnectionSummary { get; init; } = string.Empty;
}

/// <summary>
/// Request payload for <see cref="IMigrationCatalogWriter.CopyFeatureDataAsync"/>.
/// </summary>
public sealed record MigrationFeatureCopyRequest
{
    /// <summary>
    /// Source schema in the same PostgreSQL instance.
    /// </summary>
    public required string SourceSchema { get; init; }

    /// <summary>
    /// Source table in the same PostgreSQL instance.
    /// </summary>
    public required string SourceTable { get; init; }

    /// <summary>
    /// Target schema. Defaults to <c>honua_data</c>.
    /// </summary>
    public string TargetSchema { get; init; } = "honua_data";

    /// <summary>
    /// Target table name. Auto-derived from the source table when omitted.
    /// </summary>
    public required string TargetTable { get; init; }
}

/// <summary>
/// Outcome of a single <see cref="IMigrationCatalogWriter.CopyFeatureDataAsync"/>
/// invocation. <see cref="RowCount"/> reports rows in the target table after
/// the operation completes (so re-applies surface a stable observed count).
/// </summary>
public sealed record MigrationFeatureCopyOutcome
{
    /// <summary>Status of the copy.</summary>
    public required MigrationFeatureCopyStatus Status { get; init; }

    /// <summary>
    /// Row count present in the target table after the operation.
    /// </summary>
    public long RowCount { get; init; }
}

/// <summary>
/// Status of a feature data copy.
/// </summary>
public enum MigrationFeatureCopyStatus
{
    /// <summary>The target table was created and rows were copied.</summary>
    Copied,

    /// <summary>
    /// The target table already existed with at least one row; no copy
    /// was performed (idempotent re-apply).
    /// </summary>
    AlreadyApplied,

    /// <summary>
    /// The source table was not found. The caller should record a
    /// manual-review step result.
    /// </summary>
    SourceMissing
}

/// <summary>
/// Request payload for <see cref="IMigrationCatalogWriter.EnsureStyleAsync"/>.
/// Persists evidence that a source style (typically SLD) has been migrated to
/// the Honua catalog along with structured diagnostics for conversion
/// limitations.
/// </summary>
public sealed record MigrationStyleRequest
{
    /// <summary>
    /// Source kind for the migration run (e.g. <c>geoserver-rest</c>).
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Stable id of the style in the source system. For GeoServer this is
    /// <c>style:&lt;workspace|global&gt;:&lt;name&gt;</c>.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Stable id reserved in the target catalog by the manifest translator.
    /// </summary>
    public required string TargetStyleId { get; init; }

    /// <summary>
    /// Style name as advertised by the source system.
    /// </summary>
    public required string StyleName { get; init; }

    /// <summary>
    /// Owning workspace, when the source style is workspace-scoped.
    /// </summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// Source style format (e.g. <c>sld</c>, <c>css</c>, <c>mbstyle</c>).
    /// </summary>
    public string SourceFormat { get; init; } = "sld";

    /// <summary>
    /// Source language version (e.g. <c>1.0.0</c> for SLD 1.0).
    /// </summary>
    public string? SourceLanguageVersion { get; init; }

    /// <summary>
    /// Original style body as fetched from the source system. May be null when
    /// the source did not expose body content (in which case the row exists
    /// purely as a manual-review record).
    /// </summary>
    public string? SourceBody { get; init; }

    /// <summary>
    /// Converted style body (e.g. MapLibre JSON) when conversion succeeded;
    /// null otherwise.
    /// </summary>
    public string? ConvertedBody { get; init; }

    /// <summary>
    /// Identifier for the converted body's format (e.g. <c>maplibre-layers-json</c>).
    /// </summary>
    public string? ConvertedFormat { get; init; }

    /// <summary>
    /// Structured conversion diagnostics rendered as a JSON array. Each element
    /// must be a JSON object with <c>severity</c> (warning|error) and
    /// <c>message</c> fields. Persisted as <c>jsonb</c> so operators can query
    /// diagnostics by severity or message substring when auditing manual-review
    /// outcomes (issue #1015 AC: "Do not claim perfect SLD visual parity when
    /// conversion diagnostics report manual review").
    /// </summary>
    public string DiagnosticsJson { get; init; } = "[]";

    /// <summary>
    /// Review disposition recorded on the row: typically <c>applied</c> when
    /// conversion was clean, <c>manual-review</c> when diagnostics surfaced
    /// errors that block automatic visual parity, or <c>unsupported</c> when
    /// the manifest translator already marked the source style as unsupported.
    /// </summary>
    public string ReviewDisposition { get; init; } = "applied";
}

/// <summary>
/// Request payload for <see cref="IMigrationCatalogWriter.EnsureRelationshipsAsync"/>
/// (issue #1256). Carries a single source-side relationship class plus the
/// resolved Honua layer ids for both sides. Multiple requests can be passed
/// in a single call so the writer can batch v2 graph mutations.
/// </summary>
public sealed record MigrationRelationshipApplyRequest
{
    /// <summary>
    /// Stable identifier derived from the source fidelity record (for example
    /// <c>classification:resource:Inspections:layer:0:relationship:2</c>). Used
    /// for outcome correlation and step-result reporting.
    /// </summary>
    public required string SourceRelationshipId { get; init; }

    /// <summary>
    /// Optional original integer relationship id advertised by the ArcGIS
    /// service. Persisted to <c>honua.relationships.relationship_id</c> and
    /// surfaced as <see cref="Honua.Core.Features.Metadata.Domain.V2.MetadataV2Relationship.EsriRelationshipId"/>
    /// so Esri-style clients can call <c>queryRelatedRecords</c> with the same
    /// integer the source service advertised.
    /// </summary>
    public int? EsriRelationshipId { get; init; }

    /// <summary>
    /// Optional display name for the relationship.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Resolved Honua layer id of the origin (source) layer in the relationship.
    /// </summary>
    public required int OriginLayerId { get; init; }

    /// <summary>
    /// Resolved Honua layer id of the destination (related) layer.
    /// </summary>
    public required int RelatedLayerId { get; init; }

    /// <summary>
    /// Foreign-key field name on the origin layer.
    /// </summary>
    public required string OriginKeyField { get; init; }

    /// <summary>
    /// Primary/foreign-key field name on the destination layer.
    /// </summary>
    public required string DestinationKeyField { get; init; }

    /// <summary>
    /// Normalized relationship role (<c>origin</c> or <c>destination</c>). Used to
    /// pick the v1 <c>relationship_type</c> column value and the v2
    /// <see cref="Honua.Core.Features.Metadata.Domain.V2.MetadataV2Relationship.Role"/>.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Normalized relationship cardinality (<c>1:1</c>, <c>1:N</c>, or <c>M:N</c>).
    /// </summary>
    public required string Cardinality { get; init; }
}

/// <summary>
/// Outcome of a single <see cref="MigrationRelationshipApplyRequest"/>.
/// </summary>
public sealed record MigrationRelationshipApplyOutcome
{
    /// <summary>
    /// Source relationship id from the original request.
    /// </summary>
    public required string SourceRelationshipId { get; init; }

    /// <summary>
    /// Per-relationship outcome.
    /// </summary>
    public required MigrationCatalogWriteOutcome Outcome { get; init; }

    /// <summary>
    /// Operator-facing summary describing what was written, skipped, or
    /// re-applied.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Stable target relationship reference (<c>rel:{originLayerId}:{esriRelationshipId}</c>)
    /// that the apply step should record on its manifest manifest <c>TargetRelationshipRef</c>.
    /// </summary>
    public required string TargetRelationshipRef { get; init; }
}
