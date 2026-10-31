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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
                    UPDATE sample_layer SET population = 10, source_code = '001';
                    """;
                await alter.ExecuteNonQueryAsync();
            }

            var imported = await ImportAsync("gpkg_places", "places.gpkg", await File.ReadAllBytesAsync(path));
            imported.Result.TypedColumns.Should().Contain(column => column.Name == "name" && column.PostgresType == "text");
            imported.Result.TypedColumns.Should().Contain(column => column.Name == "population" && column.PostgresType == "bigint");
            imported.Result.TypedColumns.Should().Contain(column => column.Name == "source_code" && column.PostgresType == "text");
            imported.Result.TypedColumns.Should().NotContain(column => column.Name.Equals("id", StringComparison.OrdinalIgnoreCase));

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT name, population, properties->>'name', properties->>'population', source_code
                FROM "{imported.Schema}"."{imported.Table}"
                """;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("Test Feature");
            reader.GetInt64(1).Should().Be(10);
            reader.GetString(2).Should().Be("Test Feature");
            reader.GetString(3).Should().Be("10");
            reader.GetString(4).Should().Be("001");
        }
        finally
        {
            await GeoPackageTestFiles.DeleteAsync(path);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("IFileImportService.ImportFileAsync")]
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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
            SELECT name, population, active, founded, ST_X(geometry), ST_Y(geometry), properties->>'population'
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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
    [Endpoint("IFileImportService.ImportFileAsync")]
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
    [Endpoint("honua.backfill_import_typed_columns")]
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
    [Endpoint("honua.promote_import_typed_columns")]
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
        IReadOnlyList<string> keyColumns)
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
        result.Success.Should().BeTrue(result.ErrorMessage);
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
