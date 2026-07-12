// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
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
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackage_ResolvesLocalSrsIdViaSpatialRefSys()
    {
        // A spec-legal GeoPackage may number its srs_id locally (here srs_id=1) while the
        // gpkg_spatial_ref_sys row maps it to EPSG:27700 via organization_coordsys_id. Reading
        // srs_id as the EPSG code directly (the old behavior) would mis-georeference every
        // feature; the resolver must return 27700, not 1 (#2743).
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, srsId: 1, organization: "EPSG", organizationCoordsysId: 27700);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.DetectedSrid.Should().Be(27700);
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackage_NonEpsgOrganizationIsUndetected()
    {
        // A non-EPSG authority (or custom WKT-only CRS) must not be guessed as an EPSG code;
        // the resolver returns null so the import requires an explicit source SRID (#2743).
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, srsId: 4001, organization: "VENDOR", organizationCoordsysId: 4001);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.DetectedSrid.Should().BeNull();
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackageWithoutSpatialRefSysTable_FallsBackToRawSrsId()
    {
        // A malformed GeoPackage missing the (spec-required) gpkg_spatial_ref_sys table must not
        // throw "no such table" when enumerating layers. The reader probes for the table and, when
        // absent, falls back to a join-less query that treats the raw srs_id as a best-effort EPSG
        // code; import-path SRID validation still guards nonsense codes downstream (#2743).
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, srsId: 4326, includeSpatialRefSys: false);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.AvailableLayers.Should().Contain("sample_layer");
            preview.TotalFeatureCount.Should().Be(1);
            preview.DetectedSrid.Should().Be(4326);
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
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
            await DeleteGeoPackageAsync(filePath);
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
            result.ErrorMessage.Should().Be("Invalid GeoPackage import request.");
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    private static void CreateGeoPackage(
        string filePath,
        bool includeSecondLayer = false,
        int srsId = 4326,
        string organization = "EPSG",
        int organizationCoordsysId = 4326,
        bool includeSpatialRefSys = true)
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

    private static async Task DeleteGeoPackageAsync(string filePath)
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
}
