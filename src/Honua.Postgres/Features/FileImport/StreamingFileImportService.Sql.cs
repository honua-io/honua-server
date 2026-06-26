// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static string GetAllowedTableName(string tableName)
    {
        ValidateTableName(tableName);
        var sanitized = System.Text.RegularExpressions.Regex.Replace(tableName, @"[^a-zA-Z0-9_]", "_");
        return "imported_" + sanitized.ToLowerInvariant();
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

            await transaction.CommitAsync(cancellationToken);
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
