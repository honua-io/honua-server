// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    // Reserve enough identifier space for the replace path's `__staging` suffix and
    // its longest `idx_<table>__staging_properties` index name. PostgreSQL silently
    // truncates longer identifiers, which can otherwise make the geometry and
    // properties indexes collide with SQLSTATE 42P07.
    private const int MaxImportTableNameLength = 40;
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
    /// Atomically reserves a new import target. CREATE without IF NOT EXISTS rejects both
    /// existing targets and concurrent creators before any live row or metadata is changed.
    /// The append helper subsequently installs the normal import indexes.
    /// </summary>
    private static async Task CreateNewTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schemaName)};
            CREATE TABLE {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} (
                id SERIAL PRIMARY KEY,
                geometry GEOMETRY(Geometry, {targetSrid.ToString(System.Globalization.CultureInfo.InvariantCulture)}),
                properties JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW());
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitSafelyAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(EnsureImportTableSql, connection);
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
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CreateImportStagingTableSql, connection);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("target_srid", targetSrid);
        var stagingName = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return stagingName ?? tableName + "__staging";
    }

    private static async Task<ImportAdvisoryLock> AcquireImportLockAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var lockKey = $"honua.file-import.replace:{schemaName}.{tableName}";
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@lock_key, 0))",
            connection);
        // The request cancellation token remains the upper bound. A command timeout must not
        // turn a long-running import into a false concurrency failure while waiting its turn.
        command.CommandTimeout = 0;
        command.Parameters.AddWithValue("lock_key", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new ImportAdvisoryLock(connection, lockKey);
    }

    private sealed class ImportAdvisoryLock(NpgsqlConnection connection, string lockKey) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@lock_key, 0))",
                connection);
            command.Parameters.AddWithValue("lock_key", lockKey);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Atomically replaces the live target with the freshly-loaded staging sibling inside a
    /// single transaction, so the drop+rename is all-or-nothing.
    /// </summary>
    private static async Task SwapStagingTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = new NpgsqlCommand(SwapImportTableSql, connection, transaction))
            {
                command.Parameters.AddWithValue("schema_name", schemaName);
                command.Parameters.AddWithValue("table_name", tableName);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitSafelyAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(EnsureImportUpsertKeySql, connection);
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
    {
        var schema = string.IsNullOrWhiteSpace(requestedSchema)
            ? _schemaConfiguration.DefaultOperationalSchema
            : requestedSchema.Trim();

        if (!SchemaSearchPath.IsValidIdentifier(schema))
        {
            throw new ArgumentException("Target schema contains invalid characters.", nameof(requestedSchema));
        }

        return schema;
    }

    private Task<NpgsqlConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken)
        => _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken);
}
