// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Imported vector tables gain nullable typed columns for source attributes while the
/// properties jsonb document stays complete (honua-server#6640).
/// </summary>
[Collection("Database")]
[Protocol(ProtocolNames.TestQuality)]
public sealed class ImportTypedColumnTests(PostgresFixture fixture)
{
    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportTypedBigint_ScaledIntegralNumeric_PreservesValueAndRejectsInvalidValues()
    {
        await InstallImportFunctionsAsync();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT honua.import_typed_bigint('{"value":2.00}'::jsonb, 'value'),
                   honua.import_typed_bigint('{"value":2.50}'::jsonb, 'value'),
                   honua.import_typed_bigint('{"value":9223372036854775808.00}'::jsonb, 'value'),
                   honua.import_typed_bigint('{"value":"not a number"}'::jsonb, 'value')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        reader.IsDBNull(1).Should().BeTrue();
        reader.IsDBNull(2).Should().BeTrue();
        reader.IsDBNull(3).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportTypedDouble_ExpandedJsonNumbers_PreservesFiniteValues()
    {
        await InstallImportFunctionsAsync();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT honua.import_typed_double('{"value":1e100}'::jsonb, 'value'),
                   honua.import_typed_double('{"value":1e-100}'::jsonb, 'value')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDouble(0).Should().Be(1e100);
        reader.GetDouble(1).Should().Be(1e-100);
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_GeoJsonAttributes_AreTypedFilterableAndSortable()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"source_id":"001","is_open":false,"count":2,"score":1.5,"opened":"2020-01-01"}},
            {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"source_id":"002","is_open":true,"count":10,"score":2.5,"opened":"2021-01-01"}}]}
            """;
        var imported = await ImportAsync("demo", "points.geojson", Encoding.UTF8.GetBytes(json));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        foreach (var (field, value) in new[]
        {
            ("source_id", "'002'"), ("is_open", "true"), ("count", "10"),
            ("score", "2.5"), ("opened", "DATE '2021-01-01'")
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT source_id FROM \"{imported.Schema}\".\"{imported.Table}\" WHERE \"{field}\" = {value} ORDER BY \"{field}\" DESC";
            (await command.ExecuteScalarAsync()).Should().Be("002", field);
            command.CommandText = $"SELECT string_agg(source_id, ',' ORDER BY \"{field}\") FROM \"{imported.Schema}\".\"{imported.Table}\"";
            (await command.ExecuteScalarAsync()).Should().Be("001,002", field);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_GeoJson_PromotesTypedColumnsAndKeepsJsonOverflow()
    {
        const string geoJson = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "geometry": { "type": "Point", "coordinates": [-122.4, 37.8] },
                  "properties": {
                    "id": 100,
                    "name": "Alpha",
                    "population": 2,
                    "active": true,
                    "founded": "2020-01-15",
                    "observed": "2020-01-15T12:00:00Z",
                    "notes": { "kind": "park" },
                    "case_key": "first", "CASE_KEY": "second",
                    "a b": "kept"
                  }
                },
                {
                  "type": "Feature",
                  "geometry": { "type": "Point", "coordinates": [-122.5, 37.9] },
                  "properties": {
                    "id": 200,
                    "name": "Bravo",
                    "population": 10,
                    "active": false,
                    "founded": "2021-06-01",
                    "observed": "2021-06-01T08:30:00Z",
                    "notes": { "kind": "dock" },
                    "a b": "kept"
                  }
                }
              ]
            }
            """;

        var imported = await ImportAsync("places", "places.geojson", Encoding.UTF8.GetBytes(geoJson));
        imported.Result.TypedColumns.Select(column => (column.Name, column.PostgresType)).Should().BeEquivalentTo(
        [
            ("name", "text"),
            ("population", "bigint"),
            ("active", "boolean"),
            ("founded", "date"),
            ("observed", "timestamp with time zone")
        ]);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, name, population, active, founded, observed,
                   properties->>'id', properties->>'name', properties->'notes'->>'kind', properties->>'a b'
            FROM "{imported.Schema}"."{imported.Table}"
            ORDER BY population
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(1);
        reader.GetString(1).Should().Be("Alpha");
        reader.GetInt64(2).Should().Be(2);
        reader.GetBoolean(3).Should().BeTrue();
        reader.GetFieldValue<DateOnly>(4).Should().Be(new DateOnly(2020, 1, 15));
        reader.GetFieldValue<DateTimeOffset>(5).Should().Be(new DateTimeOffset(2020, 1, 15, 12, 0, 0, TimeSpan.Zero));
        reader.GetString(6).Should().Be("100");
        reader.GetString(7).Should().Be("Alpha");
        reader.GetString(8).Should().Be("park");
        reader.GetString(9).Should().Be("kept");

        var columns = await ColumnNamesAsync(imported.Schema, imported.Table);
        columns.Should().Contain("id");
        columns.Should().NotContain("notes");
        columns.Should().NotContain("a b");
        columns.Should().NotContain("case_key");
        columns.Should().NotContain("CASE_KEY");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_GeoPackage_PromotesIntegerAndTextColumns()
    {
        var path = Path.Join(Path.GetTempPath(), $"honua-typed-{Guid.NewGuid():N}.gpkg");
        try
        {
            GeoPackageTestFiles.Create(path);
            await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await sqlite.OpenAsync();
                await using var alter = sqlite.CreateCommand();
                alter.CommandText = """
                    ALTER TABLE sample_layer ADD COLUMN population INTEGER;
                    ALTER TABLE sample_layer ADD COLUMN source_code TEXT;
                    ALTER TABLE sample_layer ADD COLUMN source_label TEXT;
                    UPDATE sample_layer SET population = 10, source_code = '001', source_label = '2020-01-15';
                    """;
                await alter.ExecuteNonQueryAsync();
            }

            var imported = await ImportAsync("gpkg_places", "places.gpkg", await File.ReadAllBytesAsync(path));
            imported.Result.TypedColumns.Should().Contain(column => column.Name == "name" && column.PostgresType == "text");
            imported.Result.TypedColumns.Should().Contain(column => column.Name == "population" && column.PostgresType == "bigint");
            imported.Result.TypedColumns.Should().Contain(column => column.Name == "source_code" && column.PostgresType == "text");
            imported.Result.TypedColumns.Should().Contain(column => column.Name == "source_label" && column.PostgresType == "text");
            imported.Result.TypedColumns.Should().NotContain(column => column.Name.Equals("id", StringComparison.OrdinalIgnoreCase));

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT name, population, properties->>'name', properties->>'population', source_code, source_label
                FROM "{imported.Schema}"."{imported.Table}"
                """;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("Test Feature");
            reader.GetInt64(1).Should().Be(10);
            reader.GetString(2).Should().Be("Test Feature");
            reader.GetString(3).Should().Be("10");
            reader.GetString(4).Should().Be("001");
            reader.GetString(5).Should().Be("2020-01-15");
        }
        finally
        {
            await GeoPackageTestFiles.DeleteAsync(path);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_Shapefile_PromotesAttributes()
    {
        var zip = CreateShapefileZip();
        var imported = await ImportAsync("shp_places", "places.zip", zip);
        var population = imported.Result.TypedColumns.Should().ContainSingle(column =>
            column.Name.Equals("population", StringComparison.OrdinalIgnoreCase)).Subject;
        population.PostgresType.Should().BeOneOf("bigint", "double precision");
        var name = imported.Result.TypedColumns.Should().ContainSingle(column =>
            column.Name.Equals("name", StringComparison.OrdinalIgnoreCase) && column.PostgresType == "text").Subject;

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "{name.Name}", "{population.Name}"::float8, properties->>'{name.Name}'
            FROM "{imported.Schema}"."{imported.Table}"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Alpha");
        reader.GetDouble(1).Should().Be(10);
        reader.GetString(2).Should().Be("Alpha");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_Csv_PromotesInferredStringColumns()
    {
        const string csv = """
            name,population,active,founded,observed,lon,lat
            Alpha,2,true,2020-01-15,2020-01-15T12:00:00Z,-122.4,37.8
            Bravo,10,false,2021-06-01,2021-06-01T08:30:00Z,-122.5,37.9
            """;

        var imported = await ImportAsync("csv_places", "places.csv", Encoding.UTF8.GetBytes(csv));
        imported.Result.TypedColumns.Select(column => (column.Name, column.PostgresType)).Should().BeEquivalentTo(
        [
            ("name", "text"),
            ("population", "bigint"),
            ("active", "boolean"),
            ("founded", "date"),
            ("observed", "timestamp with time zone")
        ]);
        imported.Result.TypedColumns.Should().NotContain(column => column.Name == "lat" || column.Name == "lon");

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT name, population, active, founded, ST_X(ST_GeometryN(geometry, 1)), ST_Y(ST_GeometryN(geometry, 1)), properties->>'population'
            FROM "{imported.Schema}"."{imported.Table}"
            ORDER BY population
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Alpha");
        reader.GetInt64(1).Should().Be(2);
        reader.GetBoolean(2).Should().BeTrue();
        reader.GetFieldValue<DateOnly>(3).Should().Be(new DateOnly(2020, 1, 15));
        reader.GetDouble(4).Should().BeApproximately(-122.4, 0.0001);
        reader.GetDouble(5).Should().BeApproximately(37.8, 0.0001);
        reader.GetString(6).Should().Be("2");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_GeoParquet_PromotesLongAndTextColumns()
    {
        await using var parquet = await GeoParquetTestFactory.CreateStreamAsync();
        var imported = await ImportAsync("parquet_places", "places.parquet", parquet.ToArray());
        imported.Result.TypedColumns.Should().Contain(column => column.Name == "objectid" && column.PostgresType == "bigint");
        imported.Result.TypedColumns.Should().Contain(column => column.Name == "name" && column.PostgresType == "text");

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT objectid, name, properties->>'name'
            FROM "{imported.Schema}"."{imported.Table}"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetString(1).Should().Be("Test Feature");
        reader.GetString(2).Should().Be("Test Feature");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_SystemColumnNames_StayInProperties()
    {
        const string geoJson = """
            {
              "type": "FeatureCollection",
              "features": [{
                "type": "Feature",
                "geometry": { "type": "Point", "coordinates": [-122.4, 37.8] },
                "properties": { "name": "Alpha", "xmin": 7, "ctid": "row" }
              }]
            }
            """;

        var imported = await ImportAsync("system_cols", "system.geojson", Encoding.UTF8.GetBytes(geoJson));
        imported.Result.TypedColumns.Select(column => column.Name).Should().Equal("name");

        var columns = await ColumnNamesAsync(imported.Schema, imported.Table);
        columns.Should().NotContain("xmin");
        columns.Should().NotContain("ctid");

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT properties->>'xmin', properties->>'ctid'
            FROM "{imported.Schema}"."{imported.Table}"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("7");
        reader.GetString(1).Should().Be("row");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_UpsertOmitsPromotedKey_ClearsTheTypedValue()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance,
                schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));

            var created = await ImportIntoAsync(service, schema, "places", """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.8]},"properties":{"name":"Alpha","population":2}},
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.5,37.9]},"properties":{"name":"Bravo","population":10}}]}
                """, ImportLoadMode.Replace, []);
            created.TypedColumns.Select(column => column.Name).Should().BeEquivalentTo(["name", "population"]);

            var updated = await ImportIntoAsync(service, schema, "places", """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.8]},"properties":{"name":"Alpha"}}]}
                """, ImportLoadMode.Upsert, ["name"]);
            updated.TypedColumns.Select(column => column.Name).Should().BeEquivalentTo(["name", "population"]);

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT name, population, properties ? 'population'
                FROM "{schema}"."{created.PhysicalTableName}"
                ORDER BY name
                """;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("Alpha");
            reader.IsDBNull(1).Should().BeTrue();
            reader.GetBoolean(2).Should().BeFalse();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("Bravo");
            reader.GetInt64(1).Should().Be(10);
            reader.GetBoolean(2).Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_InterruptedUpsert_RefreshesCommittedTypedValues()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
                new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
                ImportLimits.Default with { BatchSize = 1 },
                schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));
            var created = await ImportIntoAsync(service, schema, "places", """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"Alpha","population":2}}]}
                """, ImportLoadMode.Replace, []);
            await using (var legacy = await fixture.DataSource.OpenConnectionAsync())
            await using (var clear = legacy.CreateCommand())
            {
                clear.CommandText = $"ALTER TABLE \"{schema}\".\"{created.PhysicalTableName}\" DROP CONSTRAINT honua_import_geometry_type";
                await clear.ExecuteNonQueryAsync();
            }
            const string replacement = """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{"name":"Alpha","population":10}},
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"name":"Bravo","population":20}}]}
                """;
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(replacement));
            var progress = new InlineImportProgress(value =>
            {
                if (value.BatchesCommitted > 0)
                {
                    throw new IOException("interrupted after committed batch");
                }
            });
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "places.geojson",
                TableName = "places",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.Upsert,
                KeyColumns = ["name"]
            }, progress);
            result.Success.Should().BeFalse();
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT population FROM \"{schema}\".\"{created.PhysicalTableName}\" WHERE name = 'Alpha'";
            (await command.ExecuteScalarAsync()).Should().Be(10L);
            (await ImportedGeometryTypeAsync(schema, created.PhysicalTableName!)).Should().Be("GEOMETRY");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private sealed class InlineImportProgress(Action<ImportProgress> report) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => report(value);
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task BackfillImportTypedColumns_OneRowBatch_AdvancesByPrimaryKey()
    {
        var imported = await ImportAsync("batch_places", "batch.geojson", Encoding.UTF8.GetBytes("""
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.8]},"properties":{"name":"Alpha"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.5,37.9]},"properties":{"name":"Bravo"}}]}
            """));

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var clear = connection.CreateCommand())
        {
            clear.CommandText = $"""UPDATE "{imported.Schema}"."{imported.Table}" SET name = NULL""";
            (await clear.ExecuteNonQueryAsync()).Should().Be(2);
        }

        var first = await BackfillAsync(connection, imported.Schema, imported.Table, afterId: 0);
        first.Should().Be(1L);
        var second = await BackfillAsync(connection, imported.Schema, imported.Table, afterId: first!.Value);
        second.Should().Be(2L);
        var done = await BackfillAsync(connection, imported.Schema, imported.Table, afterId: second!.Value);
        done.Should().BeNull();

        await using var read = connection.CreateCommand();
        read.CommandText = $"""SELECT name FROM "{imported.Schema}"."{imported.Table}" ORDER BY id""";
        await using var reader = await read.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Alpha");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Bravo");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task PromoteImportTypedColumns_UnownedTable_DoesNotAddColumns()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $$"""
                    CREATE TABLE "{{schema}}".plain_places (id serial PRIMARY KEY, properties jsonb);
                    INSERT INTO "{{schema}}".plain_places (properties) VALUES ('{"name":"Legacy"}'::jsonb);
                    """;
                await create.ExecuteNonQueryAsync();
            }

            await using var promote = connection.CreateCommand();
            promote.CommandText = """
                SELECT column_name, postgres_type
                FROM honua.promote_import_typed_columns(@schema_name, 'plain_places', '[{"name":"name","pgType":"text"}]'::jsonb)
                """;
            promote.Parameters.AddWithValue("schema_name", schema);
            await using var reader = await promote.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeFalse();
            await reader.DisposeAsync();

            (await ColumnNamesAsync(schema, "plain_places")).Should().BeEquivalentTo(["id", "properties"]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Upsert)]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_LegacyTable_AdditionalGeometryAndReplace_StayUnconstrained(ImportLoadMode mode)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
                new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
                ImportLimits.Default with { GeometryValidityMode = Honua.Core.Configuration.ValidationMode.Strict },
                schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));
            const string points = """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"Alpha","population":2}},
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"name":"Bravo","population":10}}]}
                """;
            const string polygon = """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{"name":"Charlie","population":20}}]}
                """;
            var created = await ImportIntoAsync(service, schema, "places", points, ImportLoadMode.Replace, []);
            (await ImportedGeometryTypeAsync(schema, created.PhysicalTableName!)).Should().Be("POINT");
            await using (var legacy = await fixture.DataSource.OpenConnectionAsync())
            await using (var clear = legacy.CreateCommand())
            {
                clear.CommandText = $"ALTER TABLE \"{schema}\".\"{created.PhysicalTableName}\" DROP CONSTRAINT honua_import_geometry_type";
                await clear.ExecuteNonQueryAsync();
            }
            await ImportIntoAsync(service, schema, "places", polygon, mode, mode == ImportLoadMode.Upsert ? ["name"] : []);
            (await ImportedGeometryTypeAsync(schema, created.PhysicalTableName!)).Should().Be("GEOMETRY");
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT SUM(population) FROM \"{schema}\".\"{created.PhysicalTableName}\"";
            Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(32);
            await ImportIntoAsync(service, schema, "places", polygon, ImportLoadMode.Replace, []);
            (await ImportedGeometryTypeAsync(schema, created.PhysicalTableName!)).Should().Be("GEOMETRY");
            var invalidPolygon = polygon.Replace("[1,0],[1,1]", "[1,1],[1,0],[0,1]", StringComparison.Ordinal);
            var skipped = await ImportIntoAsync(service, schema, "places", invalidPolygon, ImportLoadMode.Upsert, ["name"], expectedSuccess: false);
            skipped.FeatureCount.Should().Be(0);
            (await ImportedGeometryTypeAsync(schema, created.PhysicalTableName!)).Should().Be("GEOMETRY");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_MixedOrdinatesAndNull_KeepsPointAndEveryOrdinate()
    {
        const string csv = "name,wkt\nxy,POINT (1 2)\nxyz,POINT Z (1 2 3)\nxym,POINT M (1 2 4)\nxyzm,POINT ZM (1 2 3 4)\nnone,\n";
        var imported = await ImportAsync("ordinates", "ordinates.csv", Encoding.UTF8.GetBytes(csv));
        try
        {
            imported.Result.FeatureCount.Should().Be(5);
            (await ImportedGeometryTypeAsync(imported.Schema, imported.Table)).Should().Be("POINT");
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT ST_Zmflag(geometry), ST_Z(ST_GeometryN(geometry, 1)), ST_M(ST_GeometryN(geometry, 1)), ST_SRID(geometry) FROM \"{imported.Schema}\".\"{imported.Table}\" ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync();
            foreach (var flags in new[] { 0, 2, 1, 3 })
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetInt16(0).Should().Be((short)flags);
                if ((flags & 2) != 0)
                {
                    reader.GetDouble(1).Should().Be(3);
                }
                if ((flags & 1) != 0)
                {
                    reader.GetDouble(2).Should().Be(4);
                }
                reader.GetInt32(3).Should().Be(4326);
            }
            (await reader.ReadAsync()).Should().BeTrue();
            reader.IsDBNull(0).Should().BeTrue();
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(imported.Schema);
        }
    }

    [IntegrationTheory]
    [InlineData("POINT (1 2)", "MULTIPOINT ((3 4))", "MULTIPOINT", "ST_MultiPoint")]
    [InlineData("LINESTRING (0 0, 1 1)", "MULTILINESTRING ((2 2, 3 3))", "MULTILINESTRING", "ST_MultiLineString")]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_SingleAndMultiParts_InferFamily(
        string partWkt, string multiWkt, string expectedType, string expectedStoredType)
    {
        var csv = $"name,wkt\nfirst,\"{partWkt}\"\nsecond,\"{multiWkt}\"\n";
        var imported = await ImportAsync("family", "family.csv", Encoding.UTF8.GetBytes(csv));
        try
        {
            (await ImportedGeometryTypeAsync(imported.Schema, imported.Table)).Should().Be(expectedType);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{imported.Schema}\".\"{imported.Table}\" WHERE ST_GeometryType(geometry) = @type";
            command.Parameters.AddWithValue("type", expectedStoredType);
            (await command.ExecuteScalarAsync()).Should().Be(2L);
        }
        finally
        {
            await fixture.DropSchemaAsync(imported.Schema);
        }
    }

    [IntegrationTheory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Upsert)]
    [InlineData(ImportLoadMode.Replace)]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_ExistingUnconstrainedTable_DoesNotAcquireGeometryType(ImportLoadMode mode)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
                new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
                schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));
            const string polygon = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{"name":"Alpha","population":2}}]}""";
            var created = await ImportIntoAsync(service, schema, "legacy", polygon, ImportLoadMode.Replace, []);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using (var legacy = connection.CreateCommand())
            {
                legacy.CommandText = $"ALTER TABLE \"{schema}\".\"{created.PhysicalTableName}\" DROP CONSTRAINT honua_import_geometry_type; UPDATE \"{schema}\".\"{created.PhysicalTableName}\" SET geometry = ST_GeometryN(geometry, 1)";
                await legacy.ExecuteNonQueryAsync();
            }
            await ImportIntoAsync(service, schema, "legacy", polygon, mode, mode == ImportLoadMode.Upsert ? ["name"] : []);
            (await ImportedGeometryTypeAsync(schema, created.PhysicalTableName!)).Should().Be("GEOMETRY");
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT count(*) FROM \"{schema}\".\"{created.PhysicalTableName}\" WHERE ST_GeometryType(geometry) <> 'ST_Polygon'";
            (await check.ExecuteScalarAsync()).Should().Be(0L);
        }
        finally { await fixture.DropSchemaAsync(schema); }
    }

    [IntegrationTheory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Upsert)]
    [InlineData(ImportLoadMode.Replace)]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_ExistingMultiPolygon_PromotesNewPartsAndKeepsType(ImportLoadMode mode)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
                new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
                schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));
            const string polygon = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{"name":"Alpha","population":2}}]}""";
            var created = await ImportIntoAsync(service, schema, "typed", polygon, ImportLoadMode.Replace, []);
            await ImportIntoAsync(service, schema, "typed", polygon, mode, mode == ImportLoadMode.Upsert ? ["name"] : []);
            (await ImportedGeometryTypeAsync(schema, created.PhysicalTableName!)).Should().Be("MULTIPOLYGON");
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT count(*) FROM \"{schema}\".\"{created.PhysicalTableName}\" WHERE ST_GeometryType(geometry) <> 'ST_MultiPolygon'";
            (await check.ExecuteScalarAsync()).Should().Be(0L);
        }
        finally { await fixture.DropSchemaAsync(schema); }
    }

    [IntegrationTheory]
    [InlineData("st_geometrytype")]
    [InlineData("geometrytype")]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_BulkGeometryProjection_UsesBulkTimeout(string functionName)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            // Make either inference or constraint validation exceed the connection's
            // one-second default without requiring a large or slow input fixture.
            await fixture.ExecuteDdlUnderLockAsync($$"""
                CREATE FUNCTION "{{schema}}".{{functionName}}(value public.geometry) RETURNS text
                LANGUAGE plpgsql AS $body$
                BEGIN
                    PERFORM pg_sleep(1.1);
                    RETURN public.{{functionName}}(value);
                END;
                $body$;
                """);
            var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { CommandTimeout = 1 };
            await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
            var provider = new TestConnectionProvider(dataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
                new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
                schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));
            var imported = await ImportIntoAsync(service, schema, "slow_geometry", """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"Alpha"}},
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"name":"Bravo"}}]}
                """, ImportLoadMode.Replace, []);
            imported.FeatureCount.Should().Be(2);
            (await ImportedGeometryTypeAsync(schema, imported.PhysicalTableName!)).Should().Be("POINT");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task<string?> ImportedGeometryTypeAsync(string schema, string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM public.geometry_columns WHERE f_table_schema = @schema AND f_table_name = @table";
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<ImportedTable> ImportAsync(string logicalName, string fileName, byte[] bytes)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        await InstallImportFunctionsAsync();
        var provider = new TestConnectionProvider(fixture.DataSource, schema);
        var service = new StreamingFileImportService(
            provider,
            new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
            new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance,
            schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));

        await using var stream = new MemoryStream(bytes);
        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = fileName,
            TableName = logicalName,
            TargetSchema = schema,
            SourceSrid = 4326,
            TargetSrid = 4326
        });
        result.Success.Should().BeTrue(result.ErrorMessage);
        return new ImportedTable(schema, result.PhysicalTableName!, result);
    }

    private static async Task<ImportResult> ImportIntoAsync(
        StreamingFileImportService service,
        string schema,
        string logicalName,
        string geoJson,
        ImportLoadMode mode,
        IReadOnlyList<string> keyColumns,
        bool expectedSuccess = true)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(geoJson));
        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = logicalName + ".geojson",
            TableName = logicalName,
            TargetSchema = schema,
            SourceSrid = 4326,
            TargetSrid = 4326,
            LoadMode = mode,
            OverwriteExisting = mode != ImportLoadMode.Append,
            KeyColumns = keyColumns
        });
        result.Success.Should().Be(expectedSuccess, result.ErrorMessage);
        return result;
    }

    private static async Task<long?> BackfillAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        long afterId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT honua.backfill_import_typed_columns(
                @schema_name, @table_name,
                '[{"name":"name","pgType":"text"}]'::jsonb,
                @after_id, 1)
            """;
        command.Parameters.AddWithValue("schema_name", schema);
        command.Parameters.AddWithValue("table_name", table);
        command.Parameters.AddWithValue("after_id", afterId);
        var written = await command.ExecuteScalarAsync();
        return written is null or DBNull ? null : Convert.ToInt64(written, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InstallImportFunctionsAsync()
    {
        // The Postgres fixture does not apply application migrations. 129 and 143 call
        // helpers introduced earlier: assert_import_identifier, import_staging_table_name,
        // import_index_name, and the 6-argument insert_import_feature.
        foreach (var name in new[]
        {
            "004_CreateImportFunctions.sql",
            "070_AddImportLoadModes.sql",
            "071_FixImportStagingTableNameLength.sql",
            "108_FixLongImportIndexNames.sql",
            "129_ProtectImportTableOwnership.sql",
            "143_PromoteImportTypedColumns.sql",
        })
        {
            await fixture.ApplyGlobalSeedSqlAsync(ReadMigration(name));
        }
    }

    private async Task<List<string>> ColumnNamesAsync(string schema, string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema_name AND table_name = @table_name
            ORDER BY ordinal_position
            """;
        command.Parameters.AddWithValue("schema_name", schema);
        command.Parameters.AddWithValue("table_name", table);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string ReadMigration(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(directory.FullName, "src", "Honua.Server", "Migrations", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Migration {name} was not found from the test output directory.");
    }

    private static byte[] CreateShapefileZip()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"honua-typed-shp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var shpPath = Path.Join(tempDir, "sample.shp");
            var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
            var features = new List<IFeature>
            {
                new Feature(
                    geometryFactory.CreatePoint(new Coordinate(-122.4, 37.8)),
                    new AttributesTable { { "name", "Alpha" }, { "population", 10 } })
            };
            const string projection = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";
            Shapefile.WriteAllFeatures(features, shpPath, projection, Encoding.UTF8);

            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var extension in new[] { ".shp", ".shx", ".dbf", ".prj" })
                {
                    var path = Path.ChangeExtension(shpPath, extension);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(path);
                    fileStream.CopyTo(entryStream);
                }
            }

            return zipStream.ToArray();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed record ImportedTable(string Schema, string Table, ImportResult Result);

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName)
        : Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", honua, public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}
