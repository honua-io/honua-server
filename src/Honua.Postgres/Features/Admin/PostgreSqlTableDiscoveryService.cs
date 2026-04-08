// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// PostgreSQL implementation of table discovery service.
/// </summary>
/// <remarks>
/// Initialize the PostgreSQL table discovery service.
/// </remarks>
internal sealed class PostgreSqlTableDiscoveryService(
    ILogger<PostgreSqlTableDiscoveryService> logger,
    ISchemaContext? schemaContext = null) : ITableDiscoveryService
{
    private readonly ILogger<PostgreSqlTableDiscoveryService> _logger = logger;
    private readonly ISchemaContext? _schemaContext = schemaContext;

    /// <inheritdoc />
    public async Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            return await DiscoverPostGisTablesCoreAsync(connection, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TableDiscoveryLog.PostGisDiscoveryError(_logger, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            // Unwrap provider wrappers (e.g., SemaphoreReleasingConnection) so
            // this overload accepts both raw NpgsqlConnection and connections
            // handed out by the gated provider.
            var npgsqlConnection = connection.RequireNpgsqlConnection();

            return await DiscoverPostGisTablesCoreAsync(npgsqlConnection, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TableDiscoveryLog.PostGisDiscoveryError(_logger, ex);
            throw;
        }
    }

    private async Task<List<TableInfo>> DiscoverPostGisTablesCoreAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await SchemaSearchPath.ApplyAsync(connection, _schemaContext?.CurrentSchema, cancellationToken: cancellationToken).ConfigureAwait(false);

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

        var discoveredTables = new Dictionary<string, (string Schema, string Table, string GeometryColumn, string GeometryType, int Srid)>();

        await using (var command = new NpgsqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string schema = reader.GetString(0);
                string tableName = reader.GetString(1);
                string geometryColumn = reader.GetString(2);
                string geometryType = reader.GetString(3);
                int srid = reader.GetInt32(4);

                string qualifiedName = $"{schema}.{tableName}";

                if (discoveredTables.ContainsKey(qualifiedName))
                    continue;

                discoveredTables[qualifiedName] = (schema, tableName, geometryColumn, geometryType, srid);
            }
        }

        var tables = new List<TableInfo>();

        foreach (var table in discoveredTables.Values)
        {
            long? rowCount = await GetEstimatedRowCountAsync(connection, table.Schema, table.Table, cancellationToken);
            List<ColumnInfo> columns = await GetTableColumnsAsync(connection, table.Schema, table.Table, cancellationToken);

            tables.Add(new TableInfo
            {
                Schema = table.Schema,
                Table = table.Table,
                GeometryColumn = table.GeometryColumn,
                GeometryType = table.GeometryType,
                Srid = table.Srid,
                EstimatedRows = rowCount,
                Columns = columns
            });
        }

        TableDiscoveryLog.PostGisTablesDiscovered(_logger, tables.Count);
        return tables;
    }

    /// <summary>
    /// Get estimated row count for a table using PostgreSQL statistics.
    /// </summary>
    private async Task<long?> GetEstimatedRowCountAsync(
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TableDiscoveryLog.RowCountEstimateFailed(_logger, schema, tableName, ex);
            return null;
        }
    }

    /// <summary>
    /// Get all non-geometry columns for a table.
    /// </summary>
    private async Task<List<ColumnInfo>> GetTableColumnsAsync(
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
                  AND c.udt_name NOT IN ('geometry', 'geography')
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TableDiscoveryLog.ColumnDiscoveryFailed(_logger, schema, tableName, ex);
        }

        return columns;
    }
}
