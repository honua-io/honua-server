// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Infrastructure;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoPackagePreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_GeoPackage_ReturnsLayerMetadataAndAttributes()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.AvailableLayers.Should().Contain("sample_layer");
            preview.DetectedSrid.Should().Be(4326);
            preview.TotalFeatureCount.Should().Be(1);
            preview.SampleProperties.Should().ContainKey("name");
            preview.SampleProperties["name"].Should().Be("Test Feature");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackage_ReturnsAllAvailableLayers()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, includeSecondLayer: true);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.AvailableLayers.Should().BeEquivalentTo(["sample_layer", "second_layer"]);
            preview.TotalFeatureCount.Should().Be(1, "preview samples the first layer until source-layer selection is supported");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task ImportFileAsync_GeoPackageWithMultipleLayers_FailsWithClearLayerMessage()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, includeSecondLayer: true);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "sample.gpkg",
                TableName = "multi_layer_gpkg",
                TargetSrid = 4326,
                OverwriteExisting = true
            });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("multiple feature layers");
            result.ErrorMessage.Should().Contain("sample_layer");
            result.ErrorMessage.Should().Contain("second_layer");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    private static void CreateGeoPackage(string filePath, bool includeSecondLayer = false)
    {
        using var connection = new SqliteConnection($"Data Source={filePath}");
        connection.Open();

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

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition, description)
            VALUES ('WGS 84', 4326, 'EPSG', 4326, 'EPSG:4326', 'WGS 84');
            """);

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

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id)
            VALUES ('sample_layer', 'features', 'sample_layer', 'Sample layer', CURRENT_TIMESTAMP, 4326);
            """);

        if (includeSecondLayer)
        {
            ExecuteNonQuery(connection, """
                INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id)
                VALUES ('second_layer', 'features', 'second_layer', 'Second layer', CURRENT_TIMESTAMP, 4326);
                """);
        }

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m)
            VALUES ('sample_layer', 'geom', 'POINT', 4326, 0, 0);
            """);

        if (includeSecondLayer)
        {
            ExecuteNonQuery(connection, """
                INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m)
                VALUES ('second_layer', 'geom', 'POINT', 4326, 0, 0);
                """);
        }

        var geometry = new GeometryFactory(new PrecisionModel(), 4326)
            .CreatePoint(new Coordinate(-122.4, 37.6));
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

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

}
