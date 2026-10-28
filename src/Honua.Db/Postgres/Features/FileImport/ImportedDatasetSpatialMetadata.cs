// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FileImport;

/// <summary>Transaction-bound storage facts for importer-owned datasets, scoped to the actual source database.</summary>
internal static class ImportedDatasetSpatialMetadata
{
    internal readonly record struct Identity(uint RelationOid, short GeometryAttnum);
    internal readonly record struct Facts(Identity Identity, int StorageSrid, double? CoordinateEpoch);

    internal static async Task<SessionLock> AcquireSessionLockAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var key = TargetLockKey(schema, table);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@lock_key, 0))", connection);
        command.CommandTimeout = 0;
        command.Parameters.AddWithValue("lock_key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new SessionLock(connection, key);
    }

    internal sealed class SessionLock(NpgsqlConnection connection, string key) : IAsyncDisposable
    {
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@lock_key, 0))", connection);
            command.Parameters.AddWithValue("lock_key", key);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    internal static async Task LockRelationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table,
        CancellationToken cancellationToken)
    {
        using var builder = new NpgsqlCommandBuilder();
        var target = $"{builder.QuoteIdentifier(schema)}.{builder.QuoteIdentifier(table)}";
        await using var command = new NpgsqlCommand($"LOCK TABLE {target} IN ACCESS SHARE MODE", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<bool> IsAvailableAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string metadataSchema, CancellationToken cancellationToken)
    {
        // Discover first: referencing a missing table in a guarded SELECT still fails at parse time.
        await using var command = new NpgsqlCommand("SELECT to_regclass(@registry_name) IS NOT NULL", connection, transaction);
        command.Parameters.AddWithValue("registry_name", RegistryTable(metadataSchema));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    internal static async Task<Identity> LockAndResolveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string schema, string table, string geometryColumn, CancellationToken cancellationToken)
    {
        // This is the same key as the importer's session lock. Reentrant on its own connection;
        // publication obtains it only after the importing connection releases the session lock.
        await using (var advisory = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))", connection, transaction))
        {
            advisory.Parameters.AddWithValue("lock_key", TargetLockKey(schema, table));
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        await LockRelationAsync(connection, transaction, schema, table, cancellationToken);

        await using var command = new NpgsqlCommand("""
            SELECT c.oid, a.attnum
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = @schema AND c.relname = @table
                AND a.attname = @geometry AND a.attnum > 0 AND NOT a.attisdropped
            """, connection, transaction);
        AddTarget(command, schema, table, geometryColumn);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("Imported dataset geometry identity is no longer available.");
        }
        return new Identity(reader.GetFieldValue<uint>(0), reader.GetInt16(1));
    }

    internal static async Task<Facts?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string metadataSchema,
        string schema, string table, string geometryColumn, Identity identity, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT storage_srid, coordinate_epoch
            FROM {RegistryTable(metadataSchema)}
            WHERE schema_name = @schema AND table_name = @table AND geometry_column = @geometry
                AND relation_oid = @relation_oid AND geometry_attnum = @attnum
            FOR UPDATE
            """, connection, transaction);
        AddTarget(command, schema, table, geometryColumn);
        AddIdentity(command, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new Facts(identity, reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetDouble(1));
    }

    internal static async Task WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string metadataSchema,
        string schema, string table, string geometryColumn, Identity identity,
        int storageSrid, double? coordinateEpoch, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {RegistryTable(metadataSchema)}
                (schema_name, table_name, geometry_column, relation_oid, geometry_attnum, storage_srid, coordinate_epoch)
            VALUES (@schema, @table, @geometry, @relation_oid, @attnum, @srid, @epoch)
            ON CONFLICT (schema_name, table_name, geometry_column) DO UPDATE SET
                relation_oid = EXCLUDED.relation_oid, geometry_attnum = EXCLUDED.geometry_attnum,
                storage_srid = EXCLUDED.storage_srid, coordinate_epoch = EXCLUDED.coordinate_epoch, updated_at = now()
            """, connection, transaction);
        AddTarget(command, schema, table, geometryColumn);
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("srid", storageSrid);
        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Double, (object?)coordinateEpoch ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static void ValidateAppend(Facts? facts, int storageSrid, double? coordinateEpoch)
    {
        if (facts is null)
        {
            if (coordinateEpoch.HasValue)
            {
                throw new InvalidDataException("Coordinate epoch is unknown for the existing target; append or upsert cannot adopt a source epoch.");
            }
            return;
        }
        if (facts.Value.StorageSrid != storageSrid || facts.Value.CoordinateEpoch != coordinateEpoch)
        {
            throw new InvalidDataException("Source coordinate epoch or storage CRS does not match the existing target.");
        }
    }

    private static string TargetLockKey(string schema, string table) => $"honua.file-import.replace:{schema}.{table}";

    private static string RegistryTable(string metadataSchema) =>
        SchemaSearchPath.QualifyTable("imported_dataset_spatial_metadata", metadataSchema);

    private static void AddTarget(NpgsqlCommand command, string schema, string table, string geometryColumn)
    {
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("geometry", geometryColumn);
    }

    private static void AddIdentity(NpgsqlCommand command, Identity identity)
    {
        command.Parameters.AddWithValue("relation_oid", NpgsqlDbType.Oid, identity.RelationOid);
        command.Parameters.AddWithValue("attnum", identity.GeometryAttnum);
    }
}
