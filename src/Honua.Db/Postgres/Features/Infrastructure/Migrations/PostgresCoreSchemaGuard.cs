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
    internal const string RasterLayerStatisticsMigration =
        "Honua.Postgres.Migrations.003_CreateRasterLayerStatistics.sql";
    internal static readonly string MetadataV2SnapshotMigration =
        BuildServerMigrationName("031_CreateMetadataV2Snapshot.sql");
    internal static readonly string RasterExternalStorageMigration =
        BuildServerMigrationName("055_SetRasterDataExternalStorage.sql");
    internal static readonly string SensorThingsMigration =
        BuildServerMigrationName("059_CreateSensorThings.sql");

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

    private static readonly string[] _guardedIndexes =
    [
        "raster_layer_statistics_pkey",
        .. _metadataV2Indexes,
        .. _sensorThingsIndexes,
    ];

    private static readonly string[] _guardedTables =
    [
        .. _metadataV2Tables,
        .. _sensorThingsTables,
        "raster_layer_statistics",
        "raster_data",
        "raster_tiles",
    ];

    private readonly string _schemaName;

    public PostgresCoreSchemaGuard(IConfiguration? configuration = null)
    {
        var configuredSchema = configuration?["Database:Schema"];
        _schemaName = string.IsNullOrWhiteSpace(configuredSchema)
            ? PostgresSchemaConfiguration.DefaultMetadataSchema
            : configuredSchema.Trim();

        _ = SchemaSearchPath.ValidateAndQuote(_schemaName);
    }

    private static string BuildServerMigrationName(string fileName)
        => string.Concat("Honua", ".", "Server", ".Migrations.", fileName);

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

        VerifyRequiredMigration(
            state,
            RasterLayerStatisticsMigration,
            ["raster_layer_statistics"],
            _rasterLayerStatisticsColumns,
            ["raster_layer_statistics_pkey"]);
        VerifyRequiredMigration(
            state,
            MetadataV2SnapshotMigration,
            _metadataV2Tables,
            _metadataV2Columns,
            _metadataV2Indexes);
        VerifyRequiredMigration(
            state,
            SensorThingsMigration,
            _sensorThingsTables,
            _sensorThingsColumns,
            _sensorThingsIndexes);
        VerifyRequiredExternalStorageMigration(state);
    }

    public async Task VerifyConsistencyAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var state = await ReadStateAsync(connection, cancellationToken).ConfigureAwait(false);

        VerifyExclusiveMigrationConsistency(
            state,
            RasterLayerStatisticsMigration,
            ["raster_layer_statistics"],
            _rasterLayerStatisticsColumns,
            ["raster_layer_statistics_pkey"]);
        VerifyExclusiveMigrationConsistency(
            state,
            MetadataV2SnapshotMigration,
            _metadataV2Tables,
            _metadataV2Columns,
            _metadataV2Indexes);
        VerifyExclusiveMigrationConsistency(
            state,
            SensorThingsMigration,
            _sensorThingsTables,
            _sensorThingsColumns,
            _sensorThingsIndexes);

        // Migration 055 was deliberately a no-op when raster support had not been provisioned.
        // When either target table exists, however, a journal row claiming 055 must agree with
        // the physical storage policy. Absence of the journal row is a normal pending migration:
        // provider migration 001 establishes the same storage policy before 055 journals it.
        if (state.IsApplied(RasterExternalStorageMigration) && state.HasAnyRasterStorageTarget)
        {
            var missingEffects = state.MissingExternalStorageEffects();
            if (missingEffects.Count > 0)
            {
                throw CreateFailure(
                    RasterExternalStorageMigration,
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
                (MetadataV2SnapshotMigration, _metadataV2Tables, _metadataV2Columns, _metadataV2Indexes),
            DatabaseSchemaRequirement.SensorThings =>
                (SensorThingsMigration, _sensorThingsTables, _sensorThingsColumns, _sensorThingsIndexes),
            DatabaseSchemaRequirement.RasterExternalStorage =>
                (RasterExternalStorageMigration, Array.Empty<string>(), Array.Empty<(string, string)>(), Array.Empty<string>()),
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
            if (!state.HasRasterData)
            {
                throw CreateFailure(
                    migration,
                    DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                    $"required table '{_schemaName}.raster_data' is absent.");
            }

            var missingEffects = state.MissingExternalStorageEffects();
            if (missingEffects.Count > 0)
            {
                throw CreateFailure(
                    migration,
                    DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                    $"EXTERNAL storage is absent for {string.Join(", ", missingEffects)}.");
            }

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

    private static void VerifyRequiredExternalStorageMigration(SchemaState state)
    {
        if (!state.IsApplied(RasterExternalStorageMigration))
        {
            throw CreateFailure(
                RasterExternalStorageMigration,
                DatabaseSchemaFloorFailureKind.MigrationNotApplied,
                "the required numbered migration is not recorded in public.schema_versions.");
        }

        if (!state.HasRasterData)
        {
            throw CreateFailure(
                RasterExternalStorageMigration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the migration is applied, but required table '{state.SchemaName}.raster_data' is absent.");
        }

        var missingEffects = state.MissingExternalStorageEffects();
        if (missingEffects.Count > 0)
        {
            throw CreateFailure(
                RasterExternalStorageMigration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the migration is applied, but EXTERNAL storage is absent for {string.Join(", ", missingEffects)}.");
        }
    }

    private static void VerifyExclusiveMigrationConsistency(
        SchemaState state,
        string migration,
        string[] requiredTables,
        (string Table, string Column)[] requiredColumns,
        string[] requiredIndexes)
    {
        var present = requiredTables.Where(state.Tables.Contains).ToArray();
        var applied = state.IsApplied(migration);

        if (applied)
        {
            var missing = FindMissingPhysicalState(state, requiredTables, requiredColumns, requiredIndexes);
            if (missing.Length > 0)
            {
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
                WHERE n.nspname = @schema
                  AND (c.relname = ANY(@tables) OR c.relname = ANY(@indexes))
                  AND c.relkind IN ('r', 'p', 'i', 'I')
                """;
            var schemaParameter = schemaCommand.CreateParameter();
            schemaParameter.ParameterName = "schema";
            schemaParameter.Value = _schemaName;
            schemaCommand.Parameters.Add(schemaParameter);

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
                        (table == "raster_tiles" && column == "tile_data"))
                    {
                        storage[(table, column)] = reader.GetFieldValue<char>(2);
                    }
                }
            }
        }

        return new SchemaState(appliedScripts, tables, columns, indexes, storage, _schemaName);
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
        string SchemaName)
    {
        public bool HasRasterData => Tables.Contains("raster_data");

        public bool HasAnyRasterStorageTarget => HasRasterData || Tables.Contains("raster_tiles");

        public bool IsApplied(string migration) => AppliedScripts.Contains(migration);

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
