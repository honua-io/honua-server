// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: Source-table introspection.
//
// All read-side discovery of the source PostgreSQL table — existence checks, estimated
// row count from pg_class, information_schema column enumeration, primary-key resolution,
// PostgreSQL-to-MetadataV2 type mapping, and the field-list builder used when materializing
// a published layer. Split out so the queries against information_schema / pg_class /
// pg_index live in one place and don't intermix with the publish-state mutations.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private async Task<TableInfo?> ResolveTableInfoAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        try
        {
            var tables = await _tableDiscoveryService
                .DiscoverPostGisTablesAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);

            return tables.FirstOrDefault(t =>
                string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.TableDiscoveryFailed(_logger, ex);
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Unknown,
                "Failed to discover tables for publishing.");
        }
    }

    private async Task<TableInfo?> ResolveTableInfoForValidationAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var discovered = await ResolveTableInfoAsync(connectionString, schema, table, cancellationToken)
            .ConfigureAwait(false);
        if (discovered != null)
        {
            return discovered;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, schema, table, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TableInfo
        {
            Schema = schema,
            Table = table,
            GeometryColumn = null,
            GeometryType = null,
            Srid = null,
            EstimatedRows = await GetEstimatedRowCountAsync(connection, schema, table, cancellationToken)
                .ConfigureAwait(false),
            Columns = await GetTableColumnsAsync(connection, schema, table, cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = @schema
                  AND table_name = @table
                  AND table_type IN ('BASE TABLE', 'FOREIGN TABLE')
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool exists && exists;
    }

    private static async Task<long?> GetEstimatedRowCountAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT reltuples::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result == null || result == DBNull.Value
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<List<ColumnInfo>> GetTableColumnsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                c.column_name,
                c.data_type,
                c.is_nullable,
                c.character_maximum_length,
                CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END as is_primary_key
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT a.attname as column_name
                FROM pg_index i
                JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                JOIN pg_class cl ON cl.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = cl.relnamespace
                WHERE i.indisprimary
                  AND n.nspname = @schema
                  AND cl.relname = @table
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = @schema
              AND c.table_name = @table
              AND c.data_type NOT IN ('geometry', 'geography')
              AND c.udt_name NOT IN ('geometry', 'geography')
            ORDER BY c.ordinal_position;
            """;

        var columns = new List<ColumnInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new ColumnInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetString(2) == "YES",
                MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                IsPrimaryKey = reader.GetBoolean(4)
            });
        }

        return columns;
    }

    private static List<ColumnInfo> ResolveSelectedColumns(
        List<ColumnInfo> columns,
        IReadOnlyList<string> selected)
    {
        if (selected == null || selected.Count == 0)
        {
            return columns;
        }

        var lookup = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<ColumnInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in selected)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            if (!lookup.TryGetValue(field, out var column))
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Validation,
                    $"Field '{field}' was not found on the source table.");
            }

            if (seen.Add(column.Name))
            {
                result.Add(column);
            }
        }

        return result;
    }

    private static string? ResolvePrimaryKeyName(
        List<ColumnInfo> selectedColumns,
        string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        var primaryKey = selectedColumns.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            return primaryKey;
        }

        return selectedColumns.FirstOrDefault(c => IsDefaultPrimaryKeyName(c.Name))?.Name;
    }

    private static bool IsDefaultPrimaryKeyName(string name)
    {
        return name.Equals("id", StringComparison.OrdinalIgnoreCase)
               || name.Equals("objectid", StringComparison.OrdinalIgnoreCase)
               || name.Equals("fid", StringComparison.OrdinalIgnoreCase);
    }

    private static List<LayerFieldInsert> BuildLayerFields(
        List<ColumnInfo> selectedColumns,
        ColumnInfo primaryKeyColumn,
        string geometryColumn)
    {
        var fields = new List<LayerFieldInsert>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var primaryKeyType = MapPostgresType(primaryKeyColumn.DataType);
        fields.Add(new LayerFieldInsert(
            primaryKeyColumn.Name,
            primaryKeyType,
            primaryKeyColumn.MaxLength,
            primaryKeyColumn.IsNullable,
            null));
        _ = added.Add(primaryKeyColumn.Name);

        foreach (var column in selectedColumns)
        {
            if (added.Contains(column.Name))
            {
                continue;
            }

            var fieldType = MapPostgresType(column.DataType);
            fields.Add(new LayerFieldInsert(
                column.Name,
                fieldType,
                column.MaxLength,
                column.IsNullable,
                null));
            _ = added.Add(column.Name);
        }

        if (!added.Contains(geometryColumn))
        {
            fields.Add(new LayerFieldInsert(
                geometryColumn,
                MetadataV2FieldType.Geometry,
                null,
                true,
                "Geometry"));
        }

        return fields;
    }

    private static MetadataV2FieldType MapPostgresType(string dataType)
    {
        var normalized = dataType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "smallint" => MetadataV2FieldType.Integer,
            "integer" => MetadataV2FieldType.Integer,
            "bigint" => MetadataV2FieldType.BigInteger,
            "real" => MetadataV2FieldType.Float,
            "double precision" => MetadataV2FieldType.Double,
            "numeric" => MetadataV2FieldType.Double,
            "decimal" => MetadataV2FieldType.Double,
            "boolean" => MetadataV2FieldType.Boolean,
            "date" => MetadataV2FieldType.Date,
            "timestamp without time zone" => MetadataV2FieldType.DateTime,
            "timestamp with time zone" => MetadataV2FieldType.DateTime,
            "time without time zone" => MetadataV2FieldType.Time,
            "time with time zone" => MetadataV2FieldType.Time,
            "uuid" => MetadataV2FieldType.Uuid,
            "json" => MetadataV2FieldType.Json,
            "jsonb" => MetadataV2FieldType.Json,
            "bytea" => MetadataV2FieldType.Binary,
            "character varying" => MetadataV2FieldType.String,
            "character" => MetadataV2FieldType.String,
            "text" => MetadataV2FieldType.String,
            _ => MetadataV2FieldType.String
        };
    }

    private static async Task<bool> HonuaTableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'honua'
                  AND table_name = @tableName
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool exists && exists;
    }
}
