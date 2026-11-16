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
using Honua.Core.Queries.Filters;
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
    private const int TypedColumnBackfillBatchSize = 50_000;
    private const string PromoteImportTypedColumnsSql =
        "SELECT column_name, postgres_type FROM honua.promote_import_typed_columns(@schema_name, @table_name, @columns::jsonb)";
    private const string BackfillImportTypedColumnsSql =
        "SELECT honua.backfill_import_typed_columns(@schema_name, @table_name, @columns::jsonb, @after_id, @batch_size)";
    private const string ImportTableExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_class AS relation
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = @schema_name
              AND relation.relname = @table_name)
        """;

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

    private static async Task<IReadOnlyList<ImportTypedColumn>> PromoteTypedColumnsAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        IReadOnlyList<ImportTypedColumn> columns,
        CancellationToken cancellationToken)
    {
        List<ImportTypedColumn> promoted;
        await using (var command = new NpgsqlCommand(PromoteImportTypedColumnsSql, connection))
        {
            // A wide table's catalog pass can exceed the 30-second default.
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue("schema_name", schemaName);
            command.Parameters.AddWithValue("table_name", tableName);
            command.Parameters.Add(new NpgsqlParameter("columns", NpgsqlDbType.Jsonb)
            {
                Value = BuildTypedColumnPayload(columns)
            });

            promoted = new List<ImportTypedColumn>(columns.Count);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                promoted.Add(new ImportTypedColumn
                {
                    Name = reader.GetString(0),
                    PostgresType = reader.GetString(1)
                });
            }
        }

        if (promoted.Count == 0)
        {
            return promoted;
        }

        // Promotion also returns columns an earlier import already created. Backfill
        // that full set so an upsert that drops a key clears the typed value.
        var payload = BuildTypedColumnPayload(promoted);
        long afterId = 0;
        while (true)
        {
            await using var command = new NpgsqlCommand(BackfillImportTypedColumnsSql, connection);
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue("schema_name", schemaName);
            command.Parameters.AddWithValue("table_name", tableName);
            command.Parameters.Add(new NpgsqlParameter("columns", NpgsqlDbType.Jsonb) { Value = payload });
            command.Parameters.AddWithValue("after_id", afterId);
            command.Parameters.AddWithValue("batch_size", TypedColumnBackfillBatchSize);
            var written = await command.ExecuteScalarAsync(cancellationToken);
            if (written is null or DBNull)
            {
                break;
            }

            var nextId = Convert.ToInt64(written, System.Globalization.CultureInfo.InvariantCulture);
            if (nextId <= afterId)
            {
                break;
            }

            afterId = nextId;
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

    private sealed record StagingPromotion(string Schema, string Table, string StagingTable)
    {
        internal required ImportLoadMode LoadMode { get; init; }

        internal required int FeatureCount { get; init; }
        internal ImportCommitParticipant? CommitParticipant { get; init; }
        internal required int StorageSrid { get; init; }
        internal double? CoordinateEpoch { get; init; }
        internal ImportedDatasetSpatialMetadata.Identity? ExpectedIdentity { get; init; }
    }

    private async Task SwapStagingTableAsync(
        NpgsqlConnection connection, StagingPromotion promotion, CancellationToken cancellationToken)
    {
        var (schemaName, tableName, _) = promotion;
        var expectedIdentity = promotion.ExpectedIdentity;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ValidatePromotionIdentityAsync(
                connection, transaction, schemaName, tableName, expectedIdentity, cancellationToken);
            var previousType = expectedIdentity is null ? null : await ReadImportedGeometryTypeAsync(
                connection, transaction, schemaName, tableName, cancellationToken);
            // Only create-only promotes a uniquely named table; replace keeps the shared swap,
            // including when its target does not exist yet.
            if (promotion.LoadMode == ImportLoadMode.CreateOnly)
            {
                await PromoteFreshStagingTableAsync(transaction, promotion, cancellationToken);
            }
            else
            {
                await using var command = new NpgsqlCommand(SwapImportTableSql, connection, transaction);
                command.Parameters.AddWithValue("schema_name", schemaName);
                command.Parameters.AddWithValue("table_name", tableName);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (expectedIdentity is null || previousType is not null)
            {
                await RefreshImportedGeometryTypeAsync(
                    connection, schemaName, tableName, cancellationToken, transaction, previousType);
            }

            var identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                connection, transaction, schemaName, tableName, "geometry", cancellationToken);
            await ImportedDatasetSpatialMetadata.WriteAsync(
                connection, transaction, _schemaConfiguration.MetadataSchema, schemaName, tableName,
                "geometry", identity, promotion.StorageSrid, promotion.CoordinateEpoch, cancellationToken);

            await InvokeCommitParticipantAsync(
                promotion.CommitParticipant, transaction, schemaName, tableName, promotion.FeatureCount, cancellationToken);

            await transaction.CommitSafelyAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

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
        bool isNewTarget,
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
            var previousType = await ReadImportedGeometryTypeAsync(
                connection, transaction, schemaName, liveTableName, cancellationToken);
            if (previousType is not null)
            {
                await ClearImportedGeometryTypeAsync(
                    connection, transaction, schemaName, liveTableName, cancellationToken);
            }
            var insertSql =
                $"INSERT INTO {SqlIdentifier.QuoteIdentifier(schemaName)}.{SqlIdentifier.QuoteIdentifier(liveTableName)} (geometry, properties) " +
                $"SELECT geometry, properties FROM {SqlIdentifier.QuoteIdentifier(schemaName)}.{SqlIdentifier.QuoteIdentifier(stagingTableName)}";
            await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            if (isNewTarget || previousType is not null)
            {
                await RefreshImportedGeometryTypeAsync(
                    connection, schemaName, liveTableName, cancellationToken, transaction, previousType);
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
            $"{SqlIdentifier.QuoteIdentifier(schemaName)}.{SqlIdentifier.QuoteIdentifier(tableName)}");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string marker
            && marker == ImportTableOwnership.Marker;
    }

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
