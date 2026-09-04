// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

/// <summary>
/// Read-only reconciliation between the DbUp journal and the physical objects owned by the
/// audited core migrations. This component never executes DDL: divergence is an operator-visible,
/// terminal failure rather than permission to replay part of a migration from a store.
/// </summary>
internal sealed class PostgresCoreSchemaGuard : IDatabaseSchemaGuard
{
    internal const string RasterTablesMigration =
        "Honua.Postgres.Migrations.001_CreateRasterTables.sql";
    internal const string RasterLayerStatisticsMigration =
        "Honua.Postgres.Migrations.003_CreateRasterLayerStatistics.sql";
    internal const string RasterLateProvisioningMigration =
        "Honua.Postgres.Migrations.005_CompleteLateRasterProvisioning.sql";

    private static readonly string[] _metadataV2Tables =
    [
        "metadata_v2_snapshots",
        "metadata_v2_current",
        "metadata_v2_resources_idx",
        "metadata_v2_services_idx",
        "metadata_v2_publications_idx",
        "metadata_v2_storage_bindings_idx",
        "metadata_v2_connections_idx",
    ];

    private static readonly string[] _sensorThingsTables =
    [
        "sta_thing",
        "sta_sensor",
        "sta_observed_property",
        "sta_datastream",
        "sta_observation",
        "sta_observation_default",
    ];

    private static readonly string[] _metadataV2ReleasePackageTables =
    [
        "metadata_v2_release_packages",
    ];

    private static readonly (string Table, string Column)[] _rasterLayerStatisticsColumns =
    [
        ("raster_layer_statistics", "layer_id"),
        ("raster_layer_statistics", "merge_strategy"),
        ("raster_layer_statistics", "raster_signature"),
        ("raster_layer_statistics", "band_number"),
        ("raster_layer_statistics", "min_value"),
        ("raster_layer_statistics", "max_value"),
        ("raster_layer_statistics", "mean_value"),
        ("raster_layer_statistics", "std_dev"),
        ("raster_layer_statistics", "valid_pixel_count"),
        ("raster_layer_statistics", "nodata_pixel_count"),
        ("raster_layer_statistics", "computed_at"),
    ];

    private static readonly string[] _rasterOverviewsTables =
    [
        "raster_overviews",
    ];

    private static readonly (string Table, string Column)[] _rasterOverviewsColumns =
    [
        ("raster_overviews", "id"),
        ("raster_overviews", "raster_data_id"),
        ("raster_overviews", "overview_factor"),
        ("raster_overviews", "raster"),
        ("raster_overviews", "ground_resolution"),
        ("raster_overviews", "created_at"),
    ];

    private static readonly string[] _rasterOverviewsIndexes =
    [
        "raster_overviews_pkey",
        "raster_overviews_unique_factor",
        "idx_raster_overviews_raster_data_id",
        "idx_raster_overviews_lookup",
    ];

    private static readonly string[] _rasterFootprintsTables =
    [
        "raster_footprints",
    ];

    private static readonly (string Table, string Column)[] _rasterFootprintsColumns =
    [
        ("raster_footprints", "raster_data_id"),
        ("raster_footprints", "footprint"),
        ("raster_footprints", "seamline"),
        ("raster_footprints", "srid"),
        ("raster_footprints", "created_at"),
        ("raster_footprints", "updated_at"),
    ];

    private static readonly string[] _rasterFootprintsIndexes =
    [
        "raster_footprints_pkey",
        "idx_raster_footprints_footprint",
    ];

    private static readonly (string Table, string Column)[] _metadataV2Columns =
    [
        ("metadata_v2_snapshots", "environment"),
        ("metadata_v2_snapshots", "revision"),
        ("metadata_v2_snapshots", "schema_version"),
        ("metadata_v2_snapshots", "api_version"),
        ("metadata_v2_snapshots", "document"),
        ("metadata_v2_snapshots", "etag"),
        ("metadata_v2_snapshots", "generated_at"),
        ("metadata_v2_snapshots", "created_at"),
        ("metadata_v2_current", "environment"),
        ("metadata_v2_current", "revision"),
        ("metadata_v2_current", "etag"),
        ("metadata_v2_current", "activated_at"),
        ("metadata_v2_resources_idx", "environment"),
        ("metadata_v2_resources_idx", "revision"),
        ("metadata_v2_resources_idx", "resource_id"),
        ("metadata_v2_resources_idx", "name"),
        ("metadata_v2_resources_idx", "namespace"),
        ("metadata_v2_resources_idx", "type"),
        ("metadata_v2_resources_idx", "primary_storage_binding_id"),
        ("metadata_v2_services_idx", "environment"),
        ("metadata_v2_services_idx", "revision"),
        ("metadata_v2_services_idx", "service_id"),
        ("metadata_v2_services_idx", "name"),
        ("metadata_v2_services_idx", "service_type"),
        ("metadata_v2_services_idx", "route"),
        ("metadata_v2_publications_idx", "environment"),
        ("metadata_v2_publications_idx", "revision"),
        ("metadata_v2_publications_idx", "publication_id"),
        ("metadata_v2_publications_idx", "service_id"),
        ("metadata_v2_publications_idx", "resource_id"),
        ("metadata_v2_publications_idx", "storage_binding_id"),
        ("metadata_v2_publications_idx", "publication_type"),
        ("metadata_v2_publications_idx", "path"),
        ("metadata_v2_publications_idx", "layer_index"),
        ("metadata_v2_publications_idx", "service_local_id"),
        ("metadata_v2_storage_bindings_idx", "environment"),
        ("metadata_v2_storage_bindings_idx", "revision"),
        ("metadata_v2_storage_bindings_idx", "storage_binding_id"),
        ("metadata_v2_storage_bindings_idx", "resource_id"),
        ("metadata_v2_storage_bindings_idx", "connection_id"),
        ("metadata_v2_storage_bindings_idx", "storage_type"),
        ("metadata_v2_storage_bindings_idx", "locator"),
        ("metadata_v2_connections_idx", "environment"),
        ("metadata_v2_connections_idx", "revision"),
        ("metadata_v2_connections_idx", "connection_id"),
        ("metadata_v2_connections_idx", "name"),
        ("metadata_v2_connections_idx", "type"),
        ("metadata_v2_connections_idx", "provider"),
    ];

    private static readonly (string Table, string Column)[] _sensorThingsColumns =
    [
        ("sta_thing", "id"),
        ("sta_thing", "name"),
        ("sta_thing", "description"),
        ("sta_sensor", "id"),
        ("sta_sensor", "name"),
        ("sta_sensor", "description"),
        ("sta_sensor", "encoding_type"),
        ("sta_sensor", "metadata"),
        ("sta_observed_property", "id"),
        ("sta_observed_property", "name"),
        ("sta_observed_property", "definition"),
        ("sta_observed_property", "description"),
        ("sta_datastream", "id"),
        ("sta_datastream", "name"),
        ("sta_datastream", "description"),
        ("sta_datastream", "observation_type"),
        ("sta_datastream", "unit_name"),
        ("sta_datastream", "unit_symbol"),
        ("sta_datastream", "unit_definition"),
        ("sta_datastream", "thing_id"),
        ("sta_datastream", "sensor_id"),
        ("sta_datastream", "observed_property_id"),
        ("sta_observation", "id"),
        ("sta_observation", "datastream_id"),
        ("sta_observation", "phenomenon_time"),
        ("sta_observation", "result_time"),
        ("sta_observation", "result"),
        ("sta_observation", "feature_of_interest_id"),
        ("sta_observation_default", "id"),
        ("sta_observation_default", "datastream_id"),
        ("sta_observation_default", "phenomenon_time"),
        ("sta_observation_default", "result_time"),
        ("sta_observation_default", "result"),
        ("sta_observation_default", "feature_of_interest_id"),
    ];

    private static readonly (string Table, string Column)[] _metadataV2ReleasePackageColumns =
    [
        ("metadata_v2_release_packages", "package_id"),
        ("metadata_v2_release_packages", "package_key"),
        ("metadata_v2_release_packages", "package_namespace"),
        ("metadata_v2_release_packages", "status"),
        ("metadata_v2_release_packages", "source_environment"),
        ("metadata_v2_release_packages", "source_revision"),
        ("metadata_v2_release_packages", "source_etag"),
        ("metadata_v2_release_packages", "target_environments"),
        ("metadata_v2_release_packages", "entries"),
        ("metadata_v2_release_packages", "package_metadata"),
        ("metadata_v2_release_packages", "created_by"),
        ("metadata_v2_release_packages", "created_at"),
        ("metadata_v2_release_packages", "updated_at"),
    ];

    private static readonly string[] _metadataV2Indexes =
    [
        "idx_metadata_v2_resources_name",
        "idx_metadata_v2_services_name",
        "idx_metadata_v2_publications_service",
        "idx_metadata_v2_publications_resource",
        "idx_metadata_v2_storage_bindings_resource",
    ];

    private static readonly string[] _sensorThingsIndexes =
    [
        "ix_sta_observation_time",
        "ix_sta_observation_datastream_time",
    ];

    private static readonly (string Table, string Column)[] _governedLineageColumns =
    [
        ("feature_change_outbox", "operation_instance_id"),
        ("feature_change_outbox", "correlation_id"),
        ("feature_change_outbox", "audit_id"),
        ("feature_change_outbox", "proposal_id"),
        ("feature_changes", "event_id"),
        ("feature_changes", "operation_instance_id"),
        ("feature_changes", "correlation_id"),
        ("feature_changes", "audit_id"),
        ("feature_changes", "proposal_id"),
        ("alert_events", "source_event_id"),
        ("alert_events", "job_id"),
        ("alert_events", "operation_instance_id"),
        ("alert_events", "correlation_id"),
        ("alert_events", "audit_id"),
        ("alert_events", "proposal_id"),
    ];

    private static readonly string[] _governedLineageTables =
    [
        "feature_change_outbox",
        "feature_changes",
        "alert_events",
    ];

    private static readonly string[] _governedLineageIndexes =
    [
        "ux_feature_changes_event_id",
    ];

    private static readonly string[] _metadataV2ReleasePackageIndexes =
    [
        "idx_metadata_v2_release_packages_key",
        "idx_metadata_v2_release_packages_created",
        "idx_metadata_v2_release_packages_status",
    ];

    private static readonly string[] _guardedIndexes =
    [
        "raster_layer_statistics_pkey",
        .. _rasterOverviewsIndexes,
        .. _rasterFootprintsIndexes,
        .. _metadataV2Indexes,
        .. _metadataV2ReleasePackageIndexes,
        .. _sensorThingsIndexes,
        .. _governedLineageIndexes,
    ];

    private static readonly string[] _rasterBaselineTables =
    [
        "raster_data",
        "raster_statistics",
        "raster_tiles",
    ];

    private static readonly string[] _guardedTables =
    [
        .. _metadataV2Tables,
        .. _metadataV2ReleasePackageTables,
        .. _sensorThingsTables,
        .. _governedLineageTables,
        "raster_layer_statistics",
        .. _rasterBaselineTables,
        .. _rasterOverviewsTables,
        .. _rasterFootprintsTables,
    ];

    private static readonly string[] _rasterTables =
    [
        "raster_layer_statistics",
        .. _rasterBaselineTables,
        .. _rasterOverviewsTables,
        .. _rasterFootprintsTables,
    ];

    private readonly string _schemaName;
    private readonly PostgresCoreSchemaMigrationManifest _migrations;

    public PostgresCoreSchemaGuard(
        PostgresCoreSchemaMigrationManifest migrations,
        IConfiguration? configuration = null)
    {
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
        var configuredSchema = configuration?["Database:Schema"];
        _schemaName = string.IsNullOrWhiteSpace(configuredSchema)
            ? PostgresSchemaConfiguration.DefaultMetadataSchema
            : configuredSchema.Trim();

        _ = SchemaSearchPath.ValidateAndQuote(_schemaName);
    }

    public async Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task VerifyAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var state = await ReadStateAsync(connection, cancellationToken).ConfigureAwait(false);

        if (state.RequiresRasterFloor)
        {
            VerifyRequiredRasterTablesMigration(state);
            VerifyRequiredMigration(
                state,
                RasterLayerStatisticsMigration,
                ["raster_layer_statistics"],
                _rasterLayerStatisticsColumns,
                ["raster_layer_statistics_pkey"]);
            VerifyRequiredExternalStorageMigration(state);
            VerifyRequiredLateRasterProvisioningMigration(state);
            VerifyRequiredMigration(
                state,
                _migrations.RasterOverviewsMigration,
                _rasterOverviewsTables,
                _rasterOverviewsColumns,
                _rasterOverviewsIndexes);
            VerifyRequiredMigration(
                state,
                _migrations.RasterFootprintsMigration,
                _rasterFootprintsTables,
                _rasterFootprintsColumns,
                _rasterFootprintsIndexes);
        }

        VerifyRequiredMigration(
            state,
            _migrations.MetadataV2SnapshotMigration,
            _metadataV2Tables,
            _metadataV2Columns,
            _metadataV2Indexes);
        VerifyRequiredMigration(
            state,
            _migrations.MetadataV2ReleasePackagesMigration,
            _metadataV2ReleasePackageTables,
            _metadataV2ReleasePackageColumns,
            _metadataV2ReleasePackageIndexes);
        VerifyRequiredMigration(
            state,
            _migrations.SensorThingsMigration,
            _sensorThingsTables,
            _sensorThingsColumns,
            _sensorThingsIndexes);
        VerifyRequiredMigration(
            state,
            _migrations.GovernedLineageMigration,
            _governedLineageTables,
            _governedLineageColumns,
            _governedLineageIndexes);
    }

    public async Task VerifyConsistencyAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var state = await ReadStateAsync(connection, cancellationToken).ConfigureAwait(false);

        if (state.RequiresRasterFloor)
        {
            var canAwaitLateRasterProvisioning = !state.IsApplied(RasterLateProvisioningMigration);
            VerifyExclusiveMigrationConsistency(
                state,
                _migrations.RasterOverviewsMigration,
                _rasterOverviewsTables,
                _rasterOverviewsColumns,
                _rasterOverviewsIndexes,
                state.CanAwaitConfiguredSchemaAdoption || canAwaitLateRasterProvisioning);
            VerifyExclusiveMigrationConsistency(
                state,
                _migrations.RasterFootprintsMigration,
                _rasterFootprintsTables,
                _rasterFootprintsColumns,
                _rasterFootprintsIndexes,
                state.CanAwaitConfiguredSchemaAdoption || canAwaitLateRasterProvisioning);
            VerifyLateRasterProvisioningConsistency(state);
        }

        VerifyExclusiveMigrationConsistency(
            state,
            RasterLayerStatisticsMigration,
            ["raster_layer_statistics"],
            _rasterLayerStatisticsColumns,
            ["raster_layer_statistics_pkey"],
            state.CanAwaitConfiguredSchemaAdoption);
        VerifyExclusiveMigrationConsistency(
            state,
            _migrations.MetadataV2SnapshotMigration,
            _metadataV2Tables,
            _metadataV2Columns,
            _metadataV2Indexes,
            state.CanAwaitConfiguredSchemaAdoption);
        VerifyExclusiveMigrationConsistency(
            state,
            _migrations.MetadataV2ReleasePackagesMigration,
            _metadataV2ReleasePackageTables,
            _metadataV2ReleasePackageColumns,
            _metadataV2ReleasePackageIndexes,
            state.CanAwaitConfiguredSchemaAdoption);
        VerifyExclusiveMigrationConsistency(
            state,
            _migrations.SensorThingsMigration,
            _sensorThingsTables,
            _sensorThingsColumns,
            _sensorThingsIndexes,
            state.CanAwaitConfiguredSchemaAdoption);

        // Migration 055 was deliberately a no-op when raster support had not been provisioned.
        // Once any provider-baseline table exists, however, a journal row claiming 055 must
        // agree with the complete baseline and physical storage policy. Absence of the journal
        // row is a normal pending migration: provider migration 001 establishes the same storage
        // policy before 055 journals it.
        if (state.IsApplied(_migrations.RasterExternalStorageMigration) && state.HasAnyRasterBaselineTable)
        {
            var missingTables = state.MissingRasterBaselineTables();
            if (missingTables.Count > 0)
            {
                throw CreateFailure(
                    _migrations.RasterExternalStorageMigration,
                    DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                    $"journal claims the migration is applied, but required raster table(s) are absent: {string.Join(", ", missingTables)}.");
            }

            var missingEffects = state.MissingExternalStorageEffects();
            if (missingEffects.Count > 0)
            {
                throw CreateFailure(
                    _migrations.RasterExternalStorageMigration,
                    DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                    $"journal claims the migration is applied, but EXTERNAL storage is absent for {string.Join(", ", missingEffects)}.");
            }
        }
    }

    public async Task VerifyRequirementAsync(
        DbConnection connection,
        DatabaseSchemaRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var state = await ReadStateAsync(connection, cancellationToken).ConfigureAwait(false);

        var (migration, requiredTables, requiredColumns, requiredIndexes) = requirement switch
        {
            DatabaseSchemaRequirement.RasterLayerStatistics =>
                (RasterLayerStatisticsMigration, new[] { "raster_layer_statistics" }, _rasterLayerStatisticsColumns, new[] { "raster_layer_statistics_pkey" }),
            DatabaseSchemaRequirement.MetadataV2Snapshot =>
                (_migrations.MetadataV2SnapshotMigration, _metadataV2Tables, _metadataV2Columns, _metadataV2Indexes),
            DatabaseSchemaRequirement.MetadataV2ReleasePackages =>
                (_migrations.MetadataV2ReleasePackagesMigration, _metadataV2ReleasePackageTables, _metadataV2ReleasePackageColumns, _metadataV2ReleasePackageIndexes),
            DatabaseSchemaRequirement.SensorThings =>
                (_migrations.SensorThingsMigration, _sensorThingsTables, _sensorThingsColumns, _sensorThingsIndexes),
            DatabaseSchemaRequirement.RasterExternalStorage =>
                (_migrations.RasterExternalStorageMigration, Array.Empty<string>(), Array.Empty<(string, string)>(), Array.Empty<string>()),
            _ => throw new ArgumentOutOfRangeException(nameof(requirement), requirement, null),
        };

        if (!state.IsApplied(migration))
        {
            var kind = requiredTables.Any(state.Tables.Contains)
                ? DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal
                : DatabaseSchemaFloorFailureKind.MigrationNotApplied;
            throw CreateFailure(
                migration,
                kind,
                "the required numbered migration is not recorded in public.schema_versions.");
        }

        if (requirement == DatabaseSchemaRequirement.RasterExternalStorage)
        {
            var missingTables = state.MissingRasterBaselineTables();
            if (missingTables.Count > 0)
            {
                throw CreateFailure(
                    migration,
                    DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                    $"required raster table(s) are absent: {string.Join(", ", missingTables)}.");
            }

            var missingEffects = state.MissingExternalStorageEffects();
            if (missingEffects.Count > 0)
            {
                throw CreateFailure(
                    migration,
                    DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                    $"EXTERNAL storage is absent for {string.Join(", ", missingEffects)}.");
            }

            VerifyRequiredLateRasterProvisioningMigration(state);
            return;
        }

        var missingObjects = FindMissingPhysicalState(state, requiredTables, requiredColumns, requiredIndexes);
        if (missingObjects.Length > 0)
        {
            throw CreateFailure(
                migration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"required physical object(s) are absent from schema '{_schemaName}': {string.Join(", ", missingObjects)}.");
        }
    }

    private static void VerifyRequiredMigration(
        SchemaState state,
        string migration,
        string[] requiredTables,
        (string Table, string Column)[] requiredColumns,
        string[] requiredIndexes)
    {
        if (!state.IsApplied(migration))
        {
            var present = requiredTables.Where(state.Tables.Contains).ToArray();
            var kind = present.Length > 0
                ? DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal
                : DatabaseSchemaFloorFailureKind.MigrationNotApplied;
            var detail = present.Length > 0
                ? $"migration-owned table(s) exist without a journal row: {string.Join(", ", present)}."
                : "the required numbered migration is not recorded in public.schema_versions.";
            throw CreateFailure(migration, kind, detail);
        }

        var missing = FindMissingPhysicalState(state, requiredTables, requiredColumns, requiredIndexes);
        if (missing.Length > 0)
        {
            throw CreateFailure(
                migration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the migration is applied, but required physical object(s) are absent: {string.Join(", ", missing)}.");
        }
    }

    private void VerifyRequiredExternalStorageMigration(SchemaState state)
    {
        if (!state.IsApplied(_migrations.RasterExternalStorageMigration))
        {
            throw CreateFailure(
                _migrations.RasterExternalStorageMigration,
                DatabaseSchemaFloorFailureKind.MigrationNotApplied,
                "the required numbered migration is not recorded in public.schema_versions.");
        }

        var missingTables = state.MissingRasterBaselineTables();
        if (missingTables.Count > 0)
        {
            throw CreateFailure(
                _migrations.RasterExternalStorageMigration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the migration is applied, but required raster table(s) are absent: {string.Join(", ", missingTables)}.");
        }

        var missingEffects = state.MissingExternalStorageEffects();
        if (missingEffects.Count > 0)
        {
            throw CreateFailure(
                _migrations.RasterExternalStorageMigration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the migration is applied, but EXTERNAL storage is absent for {string.Join(", ", missingEffects)}.");
        }
    }

    private static void VerifyRequiredLateRasterProvisioningMigration(SchemaState state)
    {
        VerifyRequiredMigration(
            state,
            RasterLateProvisioningMigration,
            [.. _rasterOverviewsTables, .. _rasterFootprintsTables],
            [.. _rasterOverviewsColumns, .. _rasterFootprintsColumns],
            [.. _rasterOverviewsIndexes, .. _rasterFootprintsIndexes]);

        if (state.MissingOverviewExternalStorageEffect)
        {
            throw CreateFailure(
                RasterLateProvisioningMigration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the migration is applied, but EXTERNAL storage is absent for {state.SchemaName}.raster_overviews.raster.");
        }
    }

    private static void VerifyLateRasterProvisioningConsistency(SchemaState state)
    {
        if (state.IsApplied(RasterLateProvisioningMigration))
        {
            if (state.CanAwaitConfiguredSchemaAdoption &&
                !_rasterOverviewsTables.Concat(_rasterFootprintsTables).Any(state.Tables.Contains))
            {
                return;
            }

            VerifyRequiredLateRasterProvisioningMigration(state);
            return;
        }

        // Migration 063/064 may already own either complete table: both were intentionally
        // journaled as no-ops while raster was disabled. Migration 005 adopts those complete
        // objects into its provider receipt and creates only the missing tables. It cannot
        // safely repair a malformed object hidden behind CREATE TABLE IF NOT EXISTS.
        VerifyPendingLateRasterTable(
            state,
            _rasterOverviewsTables,
            _rasterOverviewsColumns,
            _rasterOverviewsIndexes);
        VerifyPendingLateRasterTable(
            state,
            _rasterFootprintsTables,
            _rasterFootprintsColumns,
            _rasterFootprintsIndexes);

        if (state.Tables.Contains("raster_overviews") && state.MissingOverviewExternalStorageEffect)
        {
            throw CreateFailure(
                RasterLateProvisioningMigration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal,
                $"migration adoption candidate is incomplete: EXTERNAL storage is absent for {state.SchemaName}.raster_overviews.raster.");
        }
    }

    private static void VerifyPendingLateRasterTable(
        SchemaState state,
        string[] requiredTables,
        (string Table, string Column)[] requiredColumns,
        string[] requiredIndexes)
    {
        if (!requiredTables.Any(state.Tables.Contains))
        {
            return;
        }

        var missing = FindMissingPhysicalState(state, requiredTables, requiredColumns, requiredIndexes);
        if (missing.Length > 0)
        {
            throw CreateFailure(
                RasterLateProvisioningMigration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal,
                $"migration adoption candidate is incomplete: {string.Join(", ", missing)}.");
        }
    }

    private static void VerifyRequiredRasterTablesMigration(SchemaState state)
    {
        if (!state.IsApplied(RasterTablesMigration))
        {
            var kind = state.HasAnyRasterBaselineTable
                ? DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal
                : DatabaseSchemaFloorFailureKind.MigrationNotApplied;
            throw CreateFailure(
                RasterTablesMigration,
                kind,
                "the raster provider baseline migration is not recorded in public.schema_versions.");
        }

        var missingTables = state.MissingRasterBaselineTables();
        if (missingTables.Count > 0)
        {
            throw CreateFailure(
                RasterTablesMigration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the provider baseline is applied, but required raster table(s) are absent: {string.Join(", ", missingTables)}.");
        }
    }

    private static void VerifyExclusiveMigrationConsistency(
        SchemaState state,
        string migration,
        string[] requiredTables,
        (string Table, string Column)[] requiredColumns,
        string[] requiredIndexes,
        bool allowCompletelyMissingUntilConfiguredSchemaAdoption = false)
    {
        var present = requiredTables.Where(state.Tables.Contains).ToArray();
        var applied = state.IsApplied(migration);

        if (applied)
        {
            var missing = FindMissingPhysicalState(state, requiredTables, requiredColumns, requiredIndexes);
            if (missing.Length > 0)
            {
                if (allowCompletelyMissingUntilConfiguredSchemaAdoption && present.Length == 0)
                {
                    return;
                }

                throw CreateFailure(
                    migration,
                    DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                    $"journal claims the migration is applied, but required physical object(s) are absent: {string.Join(", ", missing)}.");
            }
        }

        if (!applied && present.Length > 0)
        {
            throw CreateFailure(
                migration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal,
                $"migration-owned table(s) exist without a journal row: {string.Join(", ", present)}.");
        }
    }

    private static string[] FindMissingPhysicalState(
        SchemaState state,
        string[] requiredTables,
        (string Table, string Column)[] requiredColumns,
        string[] requiredIndexes)
    {
        var missingTables = requiredTables
            .Where(table => !state.Tables.Contains(table))
            .Select(table => $"table {table}");
        var missingColumns = requiredColumns
            .Where(column => !state.Columns.Contains(column))
            .Select(column => $"column {column.Table}.{column.Column}");
        var missingIndexes = requiredIndexes
            .Where(index => !state.Indexes.Contains(index))
            .Select(index => $"index {index}");
        return missingTables
            .Concat(missingColumns)
            .Concat(missingIndexes)
            .ToArray();
    }

    private async Task<SchemaState> ReadStateAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var appliedScripts = new HashSet<string>(StringComparer.Ordinal);
        await using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = """
                SELECT scriptname
                FROM public.schema_versions
                """;
            try
            {
                await using var reader = await journalCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    appliedScripts.Add(reader.GetString(0));
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // A genuinely fresh database has neither a journal nor guarded migration objects.
                // The consistency checks below distinguish that from unjournaled partial schema.
            }
        }

        var hasPostGisRaster = false;
        await using (var extensionCommand = connection.CreateCommand())
        {
            extensionCommand.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_extension
                    WHERE extname = 'postgis_raster')
                """;
            hasPostGisRaster = (bool)(
                await extensionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
        }

        var tables = new HashSet<string>(StringComparer.Ordinal);
        var columns = new HashSet<(string Table, string Column)>();
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        var storage = new Dictionary<(string Table, string Column), char>();
        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = """
                SELECT c.relname, a.attname, a.attstorage
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_catalog.pg_attribute a
                  ON a.attrelid = c.oid
                 AND a.attnum > 0
                 AND NOT a.attisdropped
                WHERE (
                    (n.nspname = @schema AND (
                        @schema = @canonical_schema OR
                        (c.relname <> ALL(@lineage_tables) AND c.relname <> ALL(@lineage_indexes))))
                    OR
                    (n.nspname = @canonical_schema AND
                        (c.relname = ANY(@lineage_tables) OR c.relname = ANY(@lineage_indexes)))
                )
                  AND (c.relname = ANY(@tables) OR c.relname = ANY(@indexes))
                  AND c.relkind IN ('r', 'p', 'i', 'I')
                """;
            var schemaParameter = schemaCommand.CreateParameter();
            schemaParameter.ParameterName = "schema";
            schemaParameter.Value = _schemaName;
            schemaCommand.Parameters.Add(schemaParameter);

            var canonicalSchemaParameter = schemaCommand.CreateParameter();
            canonicalSchemaParameter.ParameterName = "canonical_schema";
            canonicalSchemaParameter.Value = PostgresSchemaConfiguration.DefaultMetadataSchema;
            schemaCommand.Parameters.Add(canonicalSchemaParameter);

            var tablesParameter = schemaCommand.CreateParameter();
            tablesParameter.ParameterName = "tables";
            tablesParameter.Value = _guardedTables;
            if (tablesParameter is NpgsqlParameter npgsqlTablesParameter)
            {
                npgsqlTablesParameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            }
            schemaCommand.Parameters.Add(tablesParameter);

            var indexesParameter = schemaCommand.CreateParameter();
            indexesParameter.ParameterName = "indexes";
            indexesParameter.Value = _guardedIndexes;
            if (indexesParameter is NpgsqlParameter npgsqlIndexesParameter)
            {
                npgsqlIndexesParameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            }
            schemaCommand.Parameters.Add(indexesParameter);

            var lineageTablesParameter = schemaCommand.CreateParameter();
            lineageTablesParameter.ParameterName = "lineage_tables";
            lineageTablesParameter.Value = _governedLineageTables;
            if (lineageTablesParameter is NpgsqlParameter npgsqlLineageTablesParameter)
            {
                npgsqlLineageTablesParameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            }
            schemaCommand.Parameters.Add(lineageTablesParameter);

            var lineageIndexesParameter = schemaCommand.CreateParameter();
            lineageIndexesParameter.ParameterName = "lineage_indexes";
            lineageIndexesParameter.Value = _governedLineageIndexes;
            if (lineageIndexesParameter is NpgsqlParameter npgsqlLineageIndexesParameter)
            {
                npgsqlLineageIndexesParameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            }
            schemaCommand.Parameters.Add(lineageIndexesParameter);

            await using var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                if (_guardedIndexes.Contains(table, StringComparer.Ordinal))
                {
                    indexes.Add(table);
                    continue;
                }

                tables.Add(table);
                if (!reader.IsDBNull(1))
                {
                    var column = reader.GetString(1);
                    columns.Add((table, column));
                    if ((table == "raster_data" && column == "raster") ||
                        (table == "raster_tiles" && column == "tile_data") ||
                        (table == "raster_overviews" && column == "raster"))
                    {
                        storage[(table, column)] = reader.GetFieldValue<char>(2);
                    }
                }
            }
        }

        return new SchemaState(
            appliedScripts,
            tables,
            columns,
            indexes,
            storage,
            _schemaName,
            hasPostGisRaster,
            _migrations.ConfiguredSchemaAdoptionMigration);
    }

    private static DatabaseSchemaFloorException CreateFailure(
        string migration,
        DatabaseSchemaFloorFailureKind kind,
        string detail)
        => new(migration, kind, detail);

    private sealed record SchemaState(
        HashSet<string> AppliedScripts,
        HashSet<string> Tables,
        HashSet<(string Table, string Column)> Columns,
        HashSet<string> Indexes,
        Dictionary<(string Table, string Column), char> Storage,
        string SchemaName,
        bool HasPostGisRaster,
        string ConfiguredSchemaAdoptionMigration)
    {
        public bool HasAnyRasterBaselineTable => _rasterBaselineTables.Any(Tables.Contains);

        public bool MissingOverviewExternalStorageEffect =>
            Tables.Contains("raster_overviews") &&
            (!Storage.TryGetValue(("raster_overviews", "raster"), out var storageKind) || storageKind != 'e');

        public bool RequiresRasterFloor =>
            HasPostGisRaster ||
            IsApplied(RasterTablesMigration) ||
            IsApplied(RasterLayerStatisticsMigration) ||
            _rasterTables.Any(Tables.Contains);

        public bool CanAwaitConfiguredSchemaAdoption =>
            !string.Equals(SchemaName, PostgresSchemaConfiguration.DefaultMetadataSchema, StringComparison.Ordinal) &&
            !IsApplied(ConfiguredSchemaAdoptionMigration);

        public bool IsApplied(string migration) => AppliedScripts.Contains(migration);

        public List<string> MissingRasterBaselineTables()
            => _rasterBaselineTables
                .Where(table => !Tables.Contains(table))
                .Select(table => $"{SchemaName}.{table}")
                .ToList();

        public List<string> MissingExternalStorageEffects()
        {
            var missing = new List<string>();
            AddMissing("raster_data", "raster", missing);
            AddMissing("raster_tiles", "tile_data", missing);
            return missing;
        }

        private void AddMissing(string table, string column, List<string> missing)
        {
            if (Tables.Contains(table) &&
                (!Storage.TryGetValue((table, column), out var storageKind) || storageKind != 'e'))
            {
                missing.Add($"{SchemaName}.{table}.{column}");
            }
        }
    }
}
