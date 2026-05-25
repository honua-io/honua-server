// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using Honua.Postgres.Features.GeoETL.Services.Connectors;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Esri;

namespace Honua.Postgres.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the managed (GDAL-free) GeoETL file source connectors that live in
/// Honua.Postgres because they wrap this project's managed readers: the Esri shapefile
/// reader and the Microsoft.Data.Sqlite + NetTopologySuite.IO.GeoPackage path. Each test
/// writes a small fixture with the same managed libraries, then reads it back through the
/// connector — no native dependency and no live database.
/// </summary>
public sealed class ManagedFileSourceConnectorTests
{
    [Fact]
    public async Task Shapefile_ReadsPointFeaturesWithAttributes()
    {
        var dir = Directory.CreateTempSubdirectory("honua-geoetl-shp");
        var shpPath = Path.Combine(dir.FullName, "points.shp");

        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            var features = new IFeature[]
            {
                new Feature(factory.CreatePoint(new Coordinate(13.405, 52.52)),
                    new AttributesTable { { "name", "berlin" } }),
                new Feature(factory.CreatePoint(new Coordinate(-122.4, 37.6)),
                    new AttributesTable { { "name", "sf" } })
            };
            Shapefile.WriteAllFeatures(features, shpPath);

            var connector = new ShapefileSourceConnector();
            var config = new ConnectorConfig
            {
                Type = ShapefileSourceConnector.ConnectorType,
                Options = new Dictionary<string, string> { ["path"] = shpPath }
            };

            var read = new List<IFeature>();
            await foreach (var feature in connector.ReadAsync(config))
            {
                read.Add(feature);
            }

            read.Should().HaveCount(2);
            read.Should().AllSatisfy(f => f.Geometry.Should().BeOfType<Point>());
            read.Select(f => f.Attributes!.GetOptionalValue("name")?.ToString())
                .Should().BeEquivalentTo(["berlin", "sf"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Shapefile_WithoutPath_Throws()
    {
        var connector = new ShapefileSourceConnector();
        var config = new ConnectorConfig { Type = ShapefileSourceConnector.ConnectorType };

        var act = async () =>
        {
            await foreach (var _ in connector.ReadAsync(config))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GeoPackage_SingleLayer_ReadsFeaturesWithSridAndAttributes()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-geoetl-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath);

            var connector = new GeoPackageSourceConnector();
            var config = new ConnectorConfig
            {
                Type = GeoPackageSourceConnector.ConnectorType,
                Options = new Dictionary<string, string> { ["path"] = filePath }
            };

            var read = new List<IFeature>();
            await foreach (var feature in connector.ReadAsync(config))
            {
                read.Add(feature);
            }

            read.Should().HaveCount(1);
            var point = (Point)read[0].Geometry!;
            point.SRID.Should().Be(4326);
            read[0].Attributes!.GetOptionalValue("name").Should().Be("Test Feature");
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
    public async Task GeoPackage_MultipleLayersWithoutSelection_Throws()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-geoetl-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, includeSecondLayer: true);

            var connector = new GeoPackageSourceConnector();
            var config = new ConnectorConfig
            {
                Type = GeoPackageSourceConnector.ConnectorType,
                Options = new Dictionary<string, string> { ["path"] = filePath }
            };

            var act = async () =>
            {
                await foreach (var _ in connector.ReadAsync(config))
                {
                }
            };

            (await act.Should().ThrowAsync<InvalidDataException>())
                .Which.Message.Should().Contain("multiple feature layers");
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
    public async Task GeoPackage_MultipleLayersWithSelection_ReadsRequestedLayer()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-geoetl-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, includeSecondLayer: true);

            var connector = new GeoPackageSourceConnector();
            var config = new ConnectorConfig
            {
                Type = GeoPackageSourceConnector.ConnectorType,
                Options = new Dictionary<string, string>
                {
                    ["path"] = filePath,
                    ["layer"] = "second_layer"
                }
            };

            var read = new List<IFeature>();
            await foreach (var feature in connector.ReadAsync(config))
            {
                read.Add(feature);
            }

            read.Should().HaveCount(1);
            read[0].Attributes!.GetOptionalValue("name").Should().Be("Second Feature");
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

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id)
            VALUES ('sample_layer', 'features', 'sample_layer', 'Sample layer', CURRENT_TIMESTAMP, 4326);
            """);

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m)
            VALUES ('sample_layer', 'geom', 'POINT', 4326, 0, 0);
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

            ExecuteNonQuery(connection, """
                INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id)
                VALUES ('second_layer', 'features', 'second_layer', 'Second layer', CURRENT_TIMESTAMP, 4326);
                """);

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
}
