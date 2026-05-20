// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Idempotent catalog writes used by migration apply paths to persist workspace
/// and layer-group catalog entries before per-layer publishing runs.
/// </summary>
/// <remarks>
/// Slice 1 limited this abstraction to catalog-level upserts. Slice 2 extends it
/// with idempotent data-source persistence and idempotent in-database feature
/// data copy. Style persistence remains deferred to follow-on slices.
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
