// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
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

    private static async Task RequireGeoPackageTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM pragma_table_list WHERE schema = 'main' AND name = $name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", tableName);
        var tableType = await command.ExecuteScalarAsync(cancellationToken);
        if (tableType is not "table")
        {
            throw new InvalidDataException("GeoPackage metadata and feature layers must use ordinary SQLite tables.");
        }
    }
}
