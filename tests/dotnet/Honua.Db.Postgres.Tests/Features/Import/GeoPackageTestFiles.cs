// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Builds minimal single/dual-layer GeoPackage files on disk for import and preview tests,
/// including spec-legal local <c>srs_id</c> numbering resolved through
/// <c>gpkg_spatial_ref_sys</c> (#2743). The geometry blob header SRID is controlled
/// independently of the declared layer <c>srs_id</c> so tests can mirror real writers, which
/// stamp the file-local id (not an EPSG code) into every blob.
/// </summary>
internal static class GeoPackageTestFiles
{
    public static void Create(
        string filePath,
        bool includeSecondLayer = false,
        int srsId = 4326,
        string organization = "EPSG",
        int organizationCoordsysId = 4326,
        bool includeSpatialRefSys = true,
        int blobSrid = 4326,
        double x = -122.4,
        double y = 37.6)
    {
        using var connection = new SqliteConnection($"Data Source={filePath};Pooling=False");
        connection.Open();

        if (includeSpatialRefSys)
        {
            ExecuteNonQuery(connection, """
                CREATE TABLE gpkg_spatial_ref_sys (
                    srs_name TEXT NOT NULL,
                    srs_id INTEGER NOT NULL PRIMARY KEY,
                    organization TEXT NOT NULL,
                    organization_coordsys_id INTEGER NOT NULL,
                    definition TEXT NOT NULL,
                    description TEXT
                );
                """);

            using var srsInsert = connection.CreateCommand();
            srsInsert.CommandText = """
                INSERT INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition, description)
                VALUES ($srs_name, $srs_id, $organization, $organization_coordsys_id, 'undefined', 'test srs');
                """;
            srsInsert.Parameters.AddWithValue("$srs_name", $"srs-{srsId}");
            srsInsert.Parameters.AddWithValue("$srs_id", srsId);
            srsInsert.Parameters.AddWithValue("$organization", organization);
            srsInsert.Parameters.AddWithValue("$organization_coordsys_id", organizationCoordsysId);
            srsInsert.ExecuteNonQuery();
        }

        ExecuteNonQuery(connection, """
            CREATE TABLE gpkg_contents (
                table_name TEXT NOT NULL PRIMARY KEY,
                data_type TEXT NOT NULL,
                identifier TEXT,
                description TEXT DEFAULT '',
                last_change DATETIME NOT NULL,
                min_x DOUBLE,
                min_y DOUBLE,
                max_x DOUBLE,
                max_y DOUBLE,
                srs_id INTEGER
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE gpkg_geometry_columns (
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                geometry_type_name TEXT NOT NULL,
                srs_id INTEGER NOT NULL,
                z TINYINT NOT NULL,
                m TINYINT NOT NULL,
                PRIMARY KEY (table_name, column_name)
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE sample_layer (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                geom BLOB,
                name TEXT
            );
            """);

        if (includeSecondLayer)
        {
            ExecuteNonQuery(connection, """
                CREATE TABLE second_layer (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    geom BLOB,
                    name TEXT
                );
                """);
        }

        ExecuteNonQuery(connection, $"""
            INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id)
            VALUES ('sample_layer', 'features', 'sample_layer', 'Sample layer', CURRENT_TIMESTAMP, {srsId});
            """);

        if (includeSecondLayer)
        {
            ExecuteNonQuery(connection, """
                INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id)
                VALUES ('second_layer', 'features', 'second_layer', 'Second layer', CURRENT_TIMESTAMP, 4326);
                """);
        }

        ExecuteNonQuery(connection, $"""
            INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m)
            VALUES ('sample_layer', 'geom', 'POINT', {srsId}, 0, 0);
            """);

        if (includeSecondLayer)
        {
            ExecuteNonQuery(connection, """
                INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m)
                VALUES ('second_layer', 'geom', 'POINT', 4326, 0, 0);
                """);
        }

        var geometry = new GeometryFactory(new PrecisionModel(), blobSrid)
            .CreatePoint(new Coordinate(x, y));
        var writer = new GeoPackageGeoWriter();
        var geometryBytes = writer.Write(geometry);

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO sample_layer (geom, name) VALUES ($geom, $name);";
        insert.Parameters.AddWithValue("$geom", geometryBytes);
        insert.Parameters.AddWithValue("$name", "Test Feature");
        insert.ExecuteNonQuery();

        if (includeSecondLayer)
        {
            using var secondInsert = connection.CreateCommand();
            secondInsert.CommandText = "INSERT INTO second_layer (geom, name) VALUES ($geom, $name);";
            secondInsert.Parameters.AddWithValue("$geom", geometryBytes);
            secondInsert.Parameters.AddWithValue("$name", "Second Feature");
            secondInsert.ExecuteNonQuery();
        }
    }

    public static async Task DeleteAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(filePath);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
