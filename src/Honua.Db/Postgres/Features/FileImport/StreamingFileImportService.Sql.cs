// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Migration;
using NetTopologySuite.Features;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    // Reserve enough identifier space for the replace path's `__staging` suffix and
    // its longest `idx_<table>__staging_properties` index name. PostgreSQL silently
    // truncates longer identifiers, which can otherwise make the geometry and
    // properties indexes collide with SQLSTATE 42P07.
    private const int MaxImportTableNameLength = 40;
    private const string PromoteImportTypedColumnsSql =
        "SELECT column_name, postgres_type FROM honua.promote_import_typed_columns(@schema_name, @table_name, @columns::jsonb)";
    private const string ImportTableExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_class AS relation
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = @schema_name
              AND relation.relname = @table_name)
        """;

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static string GetAllowedTableName(string tableName)
    {
        ValidateTableName(tableName);
        var sanitized = System.Text.RegularExpressions.Regex.Replace(tableName, @"[^a-zA-Z0-9_]", "_");
        var physicalName = "imported_" + sanitized.ToLowerInvariant();
        if (physicalName.Length <= MaxImportTableNameLength)
        {
            return physicalName;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physicalName)))[..12];
        var prefixLength = MaxImportTableNameLength - hash.Length - 1;
        return $"{physicalName[..prefixLength]}_{hash}";
    }

    private static async Task<string> ResolvePhysicalTableNameAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        ValidateTableName(tableName);
        var sanitized = System.Text.RegularExpressions.Regex.Replace(tableName, @"[^a-zA-Z0-9_]", "_");
        var legacyName = "imported_" + sanitized.ToLowerInvariant();
        if (legacyName.Length <= MaxImportTableNameLength)
        {
            return legacyName;
        }

        // Preserve an existing pre-hash table only when its complete physical name
        // fits in PostgreSQL. A longer identifier would have been truncated, and
        // distinct logical names with the same 63-character prefix are then
        // indistinguishable; existence alone cannot prove ownership.
        if (legacyName.Length > 63)
        {
            return GetAllowedTableName(tableName);
        }

        await using var command = new NpgsqlCommand(ImportTableExistsSql, connection);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", legacyName);
        if ((bool?)await command.ExecuteScalarAsync(cancellationToken) == true)
        {
            return legacyName;
        }

        return GetAllowedTableName(tableName);
    }

    /// <summary>
    /// Reports whether the live physical target already exists. A replace whose load drops
    /// rows must only refuse to promote when this is <see langword="true"/> — a first-ever
    /// replace into a brand-new target has no prior complete dataset to lose, so a partial
    /// result is a normal partial import (#4006), not data loss.
    /// </summary>
    private static async Task<bool> ImportTableExistsAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(ImportTableExistsSql, connection, transaction);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        return (bool?)await command.ExecuteScalarAsync(cancellationToken) == true;
    }

    /// <summary>
    /// Projects inferred attributes onto nullable typed columns of an import-owned table.
    /// The properties jsonb document is left intact. A missing ownership comment makes the
    /// database function return without changing the table.
    /// </summary>
    private static async Task<IReadOnlyList<ImportTypedColumn>> PromoteTypedColumnsAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        IReadOnlyList<ImportTypedColumn> columns,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PromoteImportTypedColumnsSql, connection);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.Add(new NpgsqlParameter("columns", NpgsqlDbType.Jsonb)
        {
            Value = BuildTypedColumnPayload(columns)
        });

        var promoted = new List<ImportTypedColumn>(columns.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            promoted.Add(new ImportTypedColumn
            {
                Name = reader.GetString(0),
                PostgresType = reader.GetString(1)
            });
        }

        return promoted;
    }

    private static string BuildTypedColumnPayload(IReadOnlyList<ImportTypedColumn> columns)
    {
        if (columns.Count == 0)
        {
            return "[]";
        }

        var payload = new StringBuilder(columns.Count * 48);
        payload.Append('[');
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                payload.Append(',');
            }

            payload.Append("{\"name\":\"");
            payload.Append(columns[i].Name);
            payload.Append("\",\"pgType\":\"");
            payload.Append(columns[i].PostgresType);
            payload.Append("\"}");
        }

        payload.Append(']');
        return payload.ToString();
    }

    private static void ObserveTypedColumns(ImportTypedColumnInference inference, IFeature feature)
    {
        if (feature.Attributes is null)
        {
            return;
        }

        var names = feature.Attributes.GetNames();
        var values = feature.Attributes.GetValues();
        for (var i = 0; i < names.Length; i++)
        {
            inference.Observe(names[i], values[i]);
        }
    }

    private static async Task CreateTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CreateImportTableSql, connection);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("target_srid", targetSrid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Create-if-not-exists variant used by the append and upsert load modes, which must
    /// not drop an existing target. Builds the same fixed import shape only when the table
    /// is missing, leaving any existing rows untouched.
    /// </summary>
    private static async Task EnsureTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        int targetSrid,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(EnsureImportTableSql, connection, transaction);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("target_srid", targetSrid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Builds an empty <c>&lt;table&gt;__staging</c> sibling for a transactional replace.
    /// Returns the physical staging table name the load streams into; the live target is
    /// untouched until <see cref="SwapStagingTableAsync"/> renames the staging table over it.
    /// </summary>
    private static async Task<string> CreateStagingTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        int targetSrid,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(CreateImportStagingTableSql, connection, transaction);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("target_srid", targetSrid);
        var stagingName = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return stagingName ?? tableName + "__staging";
    }

    /// <summary>
    /// Atomically replaces the live target with the freshly-loaded staging sibling inside a
    /// single transaction, so the drop+rename is all-or-nothing.
    /// </summary>
    private async Task SwapStagingTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        int featureCount,
        ImportCommitParticipant? commitParticipant,
        int storageSrid,
        double? coordinateEpoch,
        ImportedDatasetSpatialMetadata.Identity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ValidatePromotionIdentityAsync(
                connection, transaction, schemaName, tableName, expectedIdentity, cancellationToken);
            await using (var command = new NpgsqlCommand(SwapImportTableSql, connection, transaction))
            {
                command.Parameters.AddWithValue("schema_name", schemaName);
                command.Parameters.AddWithValue("table_name", tableName);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                connection, transaction, schemaName, tableName, "geometry", cancellationToken);
            await ImportedDatasetSpatialMetadata.WriteAsync(
                connection, transaction, _schemaConfiguration.MetadataSchema, schemaName, tableName,
                "geometry", identity, storageSrid, coordinateEpoch, cancellationToken);

            await InvokeCommitParticipantAsync(
                commitParticipant, transaction, schemaName, tableName, featureCount, cancellationToken);

            await transaction.CommitSafelyAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Copies every staged row into the live target and drops the staging sibling in one
    /// transaction. A failure rolls the insert back and leaves the live table unchanged.
    /// </summary>
    private async Task AppendStagingIntoTargetAsync(
        NpgsqlConnection connection,
        string schemaName,
        string liveTableName,
        string stagingTableName,
        int featureCount,
        ImportCommitParticipant? commitParticipant,
        int storageSrid,
        double? coordinateEpoch,
        ImportedDatasetSpatialMetadata.Identity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (expectedIdentity is not { } identity)
            {
                throw new InvalidDataException("Import target identity is not available for append.");
            }
            await ValidateDirectImportAsync(connection, transaction, schemaName, liveTableName,
                storageSrid, new DirectImportGuard(identity, coordinateEpoch), cancellationToken);
            var insertSql =
                $"INSERT INTO {QuoteIdentifier(schemaName)}.{QuoteIdentifier(liveTableName)} (geometry, properties) " +
                $"SELECT geometry, properties FROM {QuoteIdentifier(schemaName)}.{QuoteIdentifier(stagingTableName)}";
            await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var drop = new NpgsqlCommand(DropImportStagingTableSql, connection, transaction))
            {
                drop.Parameters.AddWithValue("schema_name", schemaName);
                drop.Parameters.AddWithValue("table_name", liveTableName);
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }

            await InvokeCommitParticipantAsync(
                commitParticipant, transaction, schemaName, liveTableName, featureCount, cancellationToken);

            await transaction.CommitSafelyAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task InvokeCommitParticipantAsync(
        ImportCommitParticipant? participant,
        NpgsqlTransaction transaction,
        string schemaName,
        string physicalTable,
        int featureCount,
        CancellationToken cancellationToken)
    {
        if (participant is null)
        {
            return;
        }

        await participant(
            new ImportCommitScope
            {
                Transaction = transaction,
                Schema = schemaName,
                PhysicalTable = physicalTable,
                FeatureCount = featureCount,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="tableName"/> exists and carries the importer ownership marker.
    /// A missing table and a table the importer does not own both return false; the latter is not
    /// an error here because append still has a direct write path for those tables.
    /// This is a read of the ownership comment only. <see cref="ImportTableOwnership.AssertOwnedAsync"/>
    /// takes <c>LOCK TABLE</c>, which PostgreSQL rejects outside a transaction (SQLSTATE 25P01),
    /// and this check runs before the load transaction starts.
    /// </summary>
    private static async Task<bool> TargetIsImporterOwnedAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            "SELECT obj_description(to_regclass(@relation), 'pg_class')",
            connection, transaction);
        command.Parameters.AddWithValue(
            "relation",
            $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string marker
            && marker == ImportTableOwnership.Marker;
    }

    /// <summary>
    /// Drops the never-promoted <c>&lt;table&gt;__staging</c> sibling of <paramref name="tableName"/>.
    /// Used when a replace's load left features out (skip/continue) and the live target must not
    /// be swapped for an incomplete dataset (#4006); the staging table is otherwise dead weight
    /// until the next replace's <see cref="CreateStagingTableAsync"/> drops it anyway.
    /// </summary>
    private static async Task DropStagingTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(DropImportStagingTableSql, connection);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Ensures a stable unique index over the requested property key columns so the keyed
    /// upsert path has a valid <c>ON CONFLICT</c> target.
    /// </summary>
    private static async Task EnsureUpsertKeyAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(EnsureImportUpsertKeySql, connection, transaction);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.Add("key_columns", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            .Value = keyColumns.ToArray();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AnalyzeTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresSqlSafety.CreateAnalyzeCommand(connection, schemaName, tableName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ResolveTargetSchema(string? requestedSchema)
        => _schemaConfiguration.ResolveImportTargetSchema(requestedSchema);

    private Task<NpgsqlConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken)
        => _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken);
}
