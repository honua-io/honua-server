// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// PostgreSQL implementation of table discovery service.
/// </summary>
/// <remarks>
/// Initialize the PostgreSQL table discovery service.
/// </remarks>
internal sealed class PostgreSqlTableDiscoveryService(ILogger<PostgreSqlTableDiscoveryService> logger) : ITableDiscoveryService
{
    private readonly ILogger<PostgreSqlTableDiscoveryService> _logger = logger;

    /// <inheritdoc />
    public async Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var tables = new List<TableInfo>();

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Query both geometry_columns and geography_columns for spatial tables
            const string sql = """
                SELECT DISTINCT
                    f_table_schema as schema,
                    f_table_name as table_name,
                    f_geometry_column as geometry_column,
                    type as geometry_type,
                    srid,
                    'geometry' as column_type
                FROM geometry_columns
                WHERE f_table_schema NOT IN ('pg_catalog', 'information_schema', 'topology')

                UNION ALL

                SELECT DISTINCT
                    f_table_schema as schema,
                    f_table_name as table_name,
                    f_geography_column as geometry_column,
                    type as geometry_type,
                    srid,
                    'geography' as column_type
                FROM geography_columns
                WHERE f_table_schema NOT IN ('pg_catalog', 'information_schema', 'topology')

                ORDER BY schema, table_name
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            var discoveredTables = new Dictionary<string, TableInfo>();

            while (await reader.ReadAsync(cancellationToken))
            {
                string schema = reader.GetString(0);
                string tableName = reader.GetString(1);
                string geometryColumn = reader.GetString(2);
                string geometryType = reader.GetString(3);
                int srid = reader.GetInt32(4);

                string qualifiedName = $"{schema}.{tableName}";

                // If we already processed this table, skip (could happen if table has multiple geometry columns)
                if (discoveredTables.ContainsKey(qualifiedName))
                    continue;

                // Get estimated row count
                long? rowCount = await GetEstimatedRowCountAsync(connection, schema, tableName, cancellationToken);

                // Get all columns
                List<ColumnInfo> columns = await GetTableColumnsAsync(connection, schema, tableName, cancellationToken);

                var tableInfo = new TableInfo
                {
                    Schema = schema,
                    Table = tableName,
                    GeometryColumn = geometryColumn,
                    GeometryType = geometryType,
                    Srid = srid,
                    EstimatedRows = rowCount,
                    Columns = columns
                };

                discoveredTables[qualifiedName] = tableInfo;
            }

            tables.AddRange(discoveredTables.Values);

            TableDiscoveryLog.PostGisTablesDiscovered(_logger, tables.Count);
        }
        catch (Exception ex)
        {
            TableDiscoveryLog.PostGisDiscoveryError(_logger, ex);
            throw;
        }

        return tables;
    }

    /// <inheritdoc />
    public async Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var tables = new List<TableInfo>();

        try
        {
            // Use the existing connection - no need to open/close it
            NpgsqlConnection npgsqlConnection = connection as NpgsqlConnection
                ?? throw new ArgumentException("Connection must be an NpgsqlConnection", nameof(connection));

            // Query both geometry_columns and geography_columns for spatial tables
            const string sql = """
                SELECT DISTINCT
                    f_table_schema as schema,
                    f_table_name as table_name,
                    f_geometry_column as geometry_column,
                    type as geometry_type,
                    srid,
                    'geometry' as column_type
                FROM geometry_columns
                WHERE f_table_schema NOT IN ('pg_catalog', 'information_schema', 'topology')

                UNION ALL

                SELECT DISTINCT
                    f_table_schema as schema,
                    f_table_name as table_name,
                    f_geography_column as geometry_column,
                    type as geometry_type,
                    srid,
                    'geography' as column_type
                FROM geography_columns
                WHERE f_table_schema NOT IN ('pg_catalog', 'information_schema', 'topology')

                ORDER BY schema, table_name
                """;

            await using var command = new NpgsqlCommand(sql, npgsqlConnection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            var discoveredTables = new Dictionary<string, TableInfo>();

            while (await reader.ReadAsync(cancellationToken))
            {
                string schema = reader.GetString(0);
                string tableName = reader.GetString(1);
                string geometryColumn = reader.GetString(2);
                string geometryType = reader.GetString(3);
                int srid = reader.GetInt32(4);

                string qualifiedName = $"{schema}.{tableName}";

                // If we already processed this table, skip (could happen if table has multiple geometry columns)
                if (discoveredTables.ContainsKey(qualifiedName))
                    continue;

                // Get estimated row count
                long? rowCount = await GetEstimatedRowCountAsync(npgsqlConnection, schema, tableName, cancellationToken);

                // Get all columns
                List<ColumnInfo> columns = await GetTableColumnsAsync(npgsqlConnection, schema, tableName, cancellationToken);

                var tableInfo = new TableInfo
                {
                    Schema = schema,
                    Table = tableName,
                    GeometryColumn = geometryColumn,
                    GeometryType = geometryType,
                    Srid = srid,
                    EstimatedRows = rowCount,
                    Columns = columns
                };

                discoveredTables[qualifiedName] = tableInfo;
            }

            tables.AddRange(discoveredTables.Values);

            TableDiscoveryLog.PostGisTablesDiscovered(_logger, tables.Count);
        }
        catch (Exception ex)
        {
            TableDiscoveryLog.PostGisDiscoveryError(_logger, ex);
            throw;
        }

        return tables;
    }

    /// <summary>
    /// Get estimated row count for a table using PostgreSQL statistics.
    /// </summary>
    private static async Task<long?> GetEstimatedRowCountAsync(
        NpgsqlConnection connection,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        try
        {
            const string sql = """
                SELECT reltuples::bigint
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @tableName
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            _ = command.Parameters.AddWithValue("schema", schema);
            _ = command.Parameters.AddWithValue("tableName", tableName);

            object? result = await command.ExecuteScalarAsync(cancellationToken);
            return result != DBNull.Value ? Convert.ToInt64(result, CultureInfo.InvariantCulture) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get all non-geometry columns for a table.
    /// </summary>
    private static async Task<List<ColumnInfo>> GetTableColumnsAsync(
        NpgsqlConnection connection,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<ColumnInfo>();

        try
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
                      AND cl.relname = @tableName
                ) pk ON pk.column_name = c.column_name
                WHERE c.table_schema = @schema
                  AND c.table_name = @tableName
                  AND c.data_type NOT IN ('geometry', 'geography')
                ORDER BY c.ordinal_position
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            _ = command.Parameters.AddWithValue("schema", schema);
            _ = command.Parameters.AddWithValue("tableName", tableName);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var columnInfo = new ColumnInfo
                {
                    Name = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetString(2) == "YES",
                    IsPrimaryKey = reader.GetBoolean(4),
                    MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3)
                };

                columns.Add(columnInfo);
            }
        }
        catch
        {
            // If we can't get column info, return empty list
        }

        return columns;
    }
}
