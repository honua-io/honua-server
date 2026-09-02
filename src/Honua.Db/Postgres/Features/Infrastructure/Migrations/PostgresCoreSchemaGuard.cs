// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

internal enum CoreSchemaRequirement
{
    RasterLayerStatistics = 0,
    MetadataV2Snapshot = 1,
    RasterExternalStorage = 2,
    SensorThings = 3,
}

/// <summary>
/// Read-only reconciliation between the DbUp journal and the physical objects owned by the
/// audited core migrations. This component never executes DDL: divergence is an operator-visible,
/// terminal failure rather than permission to replay part of a migration from a store.
/// </summary>
internal sealed class PostgresCoreSchemaGuard : IDatabaseSchemaGuard
{
    internal const string RasterLayerStatisticsMigration =
        "Honua.Postgres.Migrations.003_CreateRasterLayerStatistics.sql";
    internal const string MetadataV2SnapshotMigration =
        "Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql";
    internal const string RasterExternalStorageMigration =
        "Honua.Server.Migrations.055_SetRasterDataExternalStorage.sql";
    internal const string SensorThingsMigration =
        "Honua.Server.Migrations.059_CreateSensorThings.sql";

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

    public async Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyConsistencyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal async Task VerifyConsistencyAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var state = await ReadStateAsync(connection, cancellationToken).ConfigureAwait(false);

        VerifyExclusiveMigrationConsistency(
            state,
            RasterLayerStatisticsMigration,
            ["raster_layer_statistics"]);
        VerifyExclusiveMigrationConsistency(state, MetadataV2SnapshotMigration, _metadataV2Tables);
        VerifyExclusiveMigrationConsistency(state, SensorThingsMigration, _sensorThingsTables);

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

    internal async Task VerifyRequirementAsync(
        DbConnection connection,
        CoreSchemaRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var state = await ReadStateAsync(connection, cancellationToken).ConfigureAwait(false);

        var (migration, requiredTables) = requirement switch
        {
            CoreSchemaRequirement.RasterLayerStatistics =>
                (RasterLayerStatisticsMigration, new[] { "raster_layer_statistics" }),
            CoreSchemaRequirement.MetadataV2Snapshot =>
                (MetadataV2SnapshotMigration, _metadataV2Tables),
            CoreSchemaRequirement.SensorThings =>
                (SensorThingsMigration, _sensorThingsTables),
            CoreSchemaRequirement.RasterExternalStorage =>
                (RasterExternalStorageMigration, Array.Empty<string>()),
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

        if (requirement == CoreSchemaRequirement.RasterExternalStorage)
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

        var missingTables = requiredTables.Where(table => !state.Tables.Contains(table)).ToArray();
        if (missingTables.Length > 0)
        {
            throw CreateFailure(
                migration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"required table(s) are absent from schema '{_schemaName}': {string.Join(", ", missingTables)}.");
        }
    }

    private static void VerifyExclusiveMigrationConsistency(
        SchemaState state,
        string migration,
        string[] requiredTables)
    {
        var present = requiredTables.Where(state.Tables.Contains).ToArray();
        var applied = state.IsApplied(migration);

        if (applied && present.Length != requiredTables.Length)
        {
            var missing = requiredTables.Except(present, StringComparer.Ordinal).ToArray();
            throw CreateFailure(
                migration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                $"journal claims the migration is applied, but required table(s) are absent: {string.Join(", ", missing)}.");
        }

        if (!applied && present.Length > 0)
        {
            throw CreateFailure(
                migration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal,
                $"migration-owned table(s) exist without a journal row: {string.Join(", ", present)}.");
        }
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
                 AND ((c.relname = 'raster_data' AND a.attname = 'raster')
                   OR (c.relname = 'raster_tiles' AND a.attname = 'tile_data'))
                WHERE n.nspname = @schema
                  AND c.relname = ANY(@tables)
                  AND c.relkind IN ('r', 'p')
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

            await using var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                tables.Add(table);
                if (!reader.IsDBNull(1))
                {
                    storage[(table, reader.GetString(1))] = reader.GetFieldValue<char>(2);
                }
            }
        }

        return new SchemaState(appliedScripts, tables, storage, _schemaName);
    }

    private static DatabaseSchemaFloorException CreateFailure(
        string migration,
        DatabaseSchemaFloorFailureKind kind,
        string detail)
        => new(migration, kind, detail);

    private sealed record SchemaState(
        HashSet<string> AppliedScripts,
        HashSet<string> Tables,
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
