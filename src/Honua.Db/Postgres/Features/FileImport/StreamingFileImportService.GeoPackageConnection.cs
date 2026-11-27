// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private const int GeoPackageMaxValueLengthBytes = 32 * 1024 * 1024;
    private const int GeoPackageMaxColumns = 1_000;
    private const int GeoPackageMaxExprDepth = 100;
    private const int GeoPackageMaxVdbeOps = 100_000;

    private static async Task<SqliteConnection> OpenGeoPackageReadOnlyAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            ConfigureGeoPackageConnection(connection, raw.SQLITE_DBCONFIG_DEFENSIVE, 1);
            ConfigureGeoPackageConnection(connection, raw.SQLITE_DBCONFIG_TRUSTED_SCHEMA, 0);
            ConfigureGeoPackageConnection(connection, raw.SQLITE_DBCONFIG_ENABLE_VIEW, 0);
            ConfigureGeoPackageConnection(connection, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 0);
            ConfigureGeoPackageLimits(connection);
            await EnableGeoPackageQueryOnlyAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void ConfigureGeoPackageConnection(SqliteConnection connection, int option, int value)
    {
        var result = raw.sqlite3_db_config(connection.Handle, option, value, out var configured);
        if (result != raw.SQLITE_OK || configured != value)
        {
            throw new InvalidDataException("GeoPackage reader could not apply its required SQLite settings.");
        }
    }

    private static void ConfigureGeoPackageLimits(SqliteConnection connection)
    {
        ApplyGeoPackageLimit(connection, raw.SQLITE_LIMIT_LENGTH, GeoPackageMaxValueLengthBytes);
        ApplyGeoPackageLimit(connection, raw.SQLITE_LIMIT_COLUMN, GeoPackageMaxColumns);
        ApplyGeoPackageLimit(connection, raw.SQLITE_LIMIT_EXPR_DEPTH, GeoPackageMaxExprDepth);
        ApplyGeoPackageLimit(connection, raw.SQLITE_LIMIT_VDBE_OP, GeoPackageMaxVdbeOps);
    }

    private static void ApplyGeoPackageLimit(SqliteConnection connection, int limitId, int newLimit)
    {
        var previous = raw.sqlite3_limit(connection.Handle, limitId, newLimit);
        if (previous < 0)
        {
            throw new InvalidDataException("GeoPackage reader could not apply its required SQLite resource limits.");
        }
    }

    private static async Task EnableGeoPackageQueryOnlyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> RequireGeoPackageTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await EnsureGeoPackageOrdinaryTableAsync(connection, tableName, cancellationToken);
        return await ReadOrdinaryGeoPackageColumnsAsync(connection, tableName, cancellationToken);
    }

    private static async Task EnsureGeoPackageOrdinaryTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA main.table_list";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) == "main" &&
                string.Equals(reader.GetString(1), tableName, StringComparison.OrdinalIgnoreCase) &&
                reader.GetString(2) == "table")
            {
                return;
            }
        }

        throw new InvalidDataException("GeoPackage metadata and feature layers must use ordinary SQLite tables.");
    }

    private static async Task<IReadOnlyList<string>> ReadOrdinaryGeoPackageColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA main.table_xinfo({SqlIdentifier.QuoteIdentifier(tableName)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hidden = reader.GetInt32(6);
            if (hidden is 2 or 3)
            {
                throw new InvalidDataException(
                    $"GeoPackage table '{tableName}' contains generated columns, which are not supported.");
            }

            if (hidden == 0)
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }
}
