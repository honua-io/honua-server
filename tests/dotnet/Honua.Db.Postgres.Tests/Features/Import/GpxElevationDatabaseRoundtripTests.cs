// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Reflection;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class GpxElevationDatabaseRoundtripTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("create_import_table", false, false)]
    [InlineData("ensure_import_table", false, false)]
    [InlineData("create_import_staging_table", false, false)]
    [InlineData("create_import_table", true, false)]
    [InlineData("ensure_import_table", true, false)]
    [InlineData("create_import_staging_table", true, false)]
    [InlineData("create_import_table", false, true)]
    [InlineData("ensure_import_table", false, true)]
    [InlineData("create_import_staging_table", false, true)]
    [InlineData("create_import_table", true, true)]
    [InlineData("ensure_import_table", true, true)]
    [InlineData("create_import_staging_table", true, true)]
    public async Task ImportStoreExport_ElevationsAndTwoDimensionalControl_PreserveValues(string createFunction, bool route, bool missingElevation)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("gpx_z");
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            var root = FindRepositoryRoot();
            foreach (var migration in new[]
            {
                "004_CreateImportFunctions.sql", "070_AddImportLoadModes.sql",
                "071_FixImportStagingTableNameLength.sql", "111_PreserveImportOrdinateDimensions.sql"
            })
            {
                // Isolate the real helper functions too: do not replace other tests' global helpers.
                var sql = (await File.ReadAllTextAsync(Path.Join(root, "src", "Honua.Server", "Migrations", migration)))
                    .Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal)
                    .Replace("CREATE SCHEMA IF NOT EXISTS honua;", "", StringComparison.Ordinal)
                    .Replace("CREATE SCHEMA IF NOT EXISTS honua_data;", "", StringComparison.Ordinal);
                await using var migrate = new NpgsqlCommand(sql, connection);
                await migrate.ExecuteNonQueryAsync();
            }

            await using (var create = new NpgsqlCommand($"SELECT \"{schema}\".{createFunction}(@schema, 'profile', 4326)", connection))
            {
                create.Parameters.AddWithValue("schema", schema);
                await create.ExecuteNonQueryAsync();
            }

            var table = createFunction == "create_import_staging_table" ? "profile__staging" : "profile";
            var xml = route
                ? "<gpx><rte><name>Profile</name><rtept lat=\"21.25\" lon=\"-157.5\"><ele>30.125</ele></rtept><rtept lat=\"22.5\" lon=\"-156.25\"><ele>-40.25</ele></rtept></rte></gpx>"
                : "<gpx><trk><name>Profile</name><trkseg><trkpt lat=\"21.25\" lon=\"-157.5\"><ele>30.125</ele></trkpt><trkpt lat=\"22.5\" lon=\"-156.25\"><ele>-40.25</ele></trkpt></trkseg></trk></gpx>";
            if (missingElevation)
            {
                var pointEnd = route ? "</rtept>" : "</trkpt>";
                var insertion = xml.IndexOf(pointEnd, StringComparison.Ordinal) + pointEnd.Length;
                xml = xml.Insert(insertion, route ? "<rtept lat=\"21.5\" lon=\"-157\"/>" : "<trkpt lat=\"21.5\" lon=\"-157\"/>");
            }

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var imported = new List<IFeature>();
            await foreach (var feature in GpxFormatReader.ReadStreamingAsync(input, CancellationToken.None))
            {
                imported.Add(feature);
            }

            var source = Assert.Single(imported);
            var validate = typeof(StreamingFileImportService).GetMethod("ValidateGeometry", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Null(validate.Invoke(null, [source.Geometry]));
            var selectWriter = typeof(StreamingFileImportService).GetMethod("SelectWkbWriter", BindingFlags.NonPublic | BindingFlags.Static)!;
            var writer = (WKBWriter)selectWriter.Invoke(null, [source.Geometry, new WKBWriter()])!;
            var wkb = writer.Write(source.Geometry);
            await using (var insert = new NpgsqlCommand($"SELECT \"{schema}\".insert_import_feature(@schema, @table, @wkb, 4326, 4326, '{{\"name\":\"Profile\"}}'::jsonb)", connection))
            {
                insert.Parameters.AddWithValue("schema", schema);
                insert.Parameters.AddWithValue("table", table);
                insert.Parameters.AddWithValue("wkb", wkb);
                await insert.ExecuteNonQueryAsync();
            }

            // A new unconstrained dimension layout must retain XY without manufacturing Z/M.
            await using (var control = new NpgsqlCommand($"INSERT INTO \"{schema}\".\"{table}\" (geometry, properties) VALUES (ST_GeomFromText('POINT (1 2)', 4326), '{{}}')", connection))
            {
                await control.ExecuteNonQueryAsync();
            }

            await using var query = new NpgsqlCommand($"SELECT ST_AsBinary(geometry), properties->>'name', ST_SRID(geometry) FROM \"{schema}\".\"{table}\" ORDER BY id", connection);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var exported = new WKBReader().Read(reader.GetFieldValue<byte[]>(0));
            Assert.True(source.Geometry.EqualsExact(exported));
            Assert.Equal(missingElevation ? new[] { 30.125, double.NaN, -40.25 } : new[] { 30.125, -40.25 },
                exported.Coordinates.Select(c => c.Z));
            Assert.All(exported.Coordinates, c => Assert.True(double.IsNaN(c.M)));
            Assert.Equal("Profile", Assert.IsType<string>(reader.GetValue(1)));
            Assert.Equal(4326, reader.GetInt32(2));
            Assert.True(await reader.ReadAsync());
            var xy = new WKBReader().Read(reader.GetFieldValue<byte[]>(0));
            Assert.Equal("Point", xy.GeometryType);
            Assert.Equal(1, xy.Coordinate.X);
            Assert.Equal(2, xy.Coordinate.Y);
            Assert.True(double.IsNaN(xy.Coordinate.Z));
            Assert.True(double.IsNaN(xy.Coordinate.M));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "src", "Honua.Server", "Migrations", "004_CreateImportFunctions.sql")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find import migration sources.");
    }
}
