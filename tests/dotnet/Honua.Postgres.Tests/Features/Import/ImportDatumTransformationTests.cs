// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Postgres.Features.FileImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Verifies the file-import reprojection path routes through the auditable
/// Esri-default datum-transformation catalog (#1501). Imports that resolve a curated
/// PROJ pipeline for the <c>(sourceSrid -&gt; targetSrid)</c> pair must apply it through
/// the explicit 3-argument <c>ST_Transform</c> overload of <c>honua.insert_import_feature</c>;
/// imports with no curated default keep PROJ's default (2-argument) behavior.
/// </summary>
[Collection("Database")]
public sealed class ImportDatumTransformationTests(PostgresFixture fixture)
{
    // honua.insert_import_feature now ships both the legacy 6-argument overload and the
    // 7-argument datum-pipeline overload (migration 053). The fixture does not run
    // migrations, so the functions are created inline here, mirroring the migration.
    private const string CreateImportFunctionsSql = """
        CREATE OR REPLACE FUNCTION honua.create_import_table(schema_name text, table_name text, target_srid integer DEFAULT 4326)
        RETURNS void
        LANGUAGE plpgsql
        AS $$
        BEGIN
            EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
            EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, table_name);
            EXECUTE format(
                'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
                schema_name, table_name, target_srid);
            EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || table_name || '_geometry', schema_name, table_name);
        END;
        $$;

        CREATE OR REPLACE FUNCTION honua.insert_import_feature(
            schema_name text,
            table_name text,
            wkb bytea,
            source_srid integer,
            target_srid integer,
            properties jsonb)
        RETURNS void
        LANGUAGE plpgsql
        AS $$
        BEGIN
            EXECUTE format(
                'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4)',
                schema_name, table_name)
            USING wkb, source_srid, target_srid, properties;
        END;
        $$;

        CREATE OR REPLACE FUNCTION honua.insert_import_feature(
            schema_name text,
            table_name text,
            wkb bytea,
            source_srid integer,
            target_srid integer,
            properties jsonb,
            datum_transformation_pipeline text)
        RETURNS void
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF datum_transformation_pipeline IS NULL OR length(datum_transformation_pipeline) = 0 THEN
                EXECUTE format(
                    'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4)',
                    schema_name, table_name)
                USING wkb, source_srid, target_srid, properties;
            ELSE
                EXECUTE format(
                    'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $4, $3), $5)',
                    schema_name, table_name)
                USING wkb, source_srid, target_srid, datum_transformation_pipeline, properties;
            END IF;
        END;
        $$;
        """;

    // A single NAD83 (EPSG:4269) point near Denver. NAD83 and WGS84 coincide to ~1 m,
    // so the Esri-default null transformation lands essentially on the input coordinate.
    private const double Nad83Lon = -104.9903;
    private const double Nad83Lat = 39.7392;
    private static readonly string PointGeoJson = $$"""
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [{{Nad83Lon}}, {{Nad83Lat}}] }, "properties": { "name": "denver" } }
          ]
        }
        """;

    [IntegrationTest]
    public async Task ImportFileAsync_Nad83ToWgs84_WithCatalog_AppliesEsriDefaultPipelineAndSucceeds()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();

            // The real catalog resolves NAD83->WGS84 to the Esri-default null transformation
            // (+proj=noop), so the import must succeed and land on the input coordinate.
            var service = CreateService(schema, EsriDatumTransformationCatalog.Create());
            var result = await ImportPointAsync(service, schema, "datum_real", sourceSrid: 4269, targetSrid: 4326);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);

            var (x, y, srid) = await ReadSinglePointAsync(schema, "imported_datum_real");
            srid.Should().Be(4326);
            x.Should().BeApproximately(Nad83Lon, 1e-5);
            y.Should().BeApproximately(Nad83Lat, 1e-5);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_RoutesCatalogPipelineIntoStTransform()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();

            // A catalog that returns a syntactically invalid PROJ pipeline for the pair.
            // The import can only fail if that pipeline string actually reaches PostGIS'
            // 3-argument ST_Transform — proving the import path honors the catalog selection
            // rather than silently using the 2-argument default.
            var service = CreateService(schema, new InvalidPipelineCatalog());
            var result = await ImportPointAsync(service, schema, "datum_routed", sourceSrid: 4269, targetSrid: 4326);

            result.Success.Should().BeFalse(
                "the catalog's pipeline must be routed into ST_Transform, and an invalid pipeline must surface as a failure");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_WithoutCatalog_KeepsDefaultPipelineBehavior()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();

            // No catalog wired: even the same invalid-pipeline pair would not be consulted,
            // so the import uses the unchanged 2-argument default path and succeeds.
            var service = CreateService(schema, datumTransformationCatalog: null);
            var result = await ImportPointAsync(service, schema, "datum_none", sourceSrid: 4269, targetSrid: 4326);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_EqualSourceAndTarget_DoesNotConsultCatalog()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();

            // Equal SRIDs need no reprojection; the invalid-pipeline catalog must never be
            // consulted, so the import succeeds.
            var service = CreateService(schema, new InvalidPipelineCatalog());
            var result = await ImportPointAsync(service, schema, "datum_equal", sourceSrid: 4326, targetSrid: 4326);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_ReverseDirectionSelection_FallsBackToDefaultPath()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();

            // A reverse-direction selection (TransformForward = false) carries the forward pipeline,
            // which must NOT be applied in reverse (it would corrupt coordinates). The import must skip
            // the explicit pipeline and use PROJ's default 2-argument path — so even though the catalog
            // hands back a syntactically invalid pipeline, the import succeeds because it is never used.
            var service = CreateService(schema, new ReverseDirectionPipelineCatalog());
            var result = await ImportPointAsync(service, schema, "datum_reverse", sourceSrid: 4269, targetSrid: 4326);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private StreamingFileImportService CreateService(string schema, IDatumTransformationCatalog? datumTransformationCatalog)
    {
        var provider = new TestConnectionProvider(fixture.DataSource, schema);
        return new StreamingFileImportService(
            provider,
            new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
            new TestFileFormatDetectionService(),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance,
            limits: null,
            cloudStorage: null,
            schemaConfiguration: null,
            datumTransformationCatalog: datumTransformationCatalog);
    }

    private static async Task<ImportResult> ImportPointAsync(
        StreamingFileImportService service,
        string schema,
        string tableName,
        int sourceSrid,
        int targetSrid)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
        return await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "points.geojson",
            TableName = tableName,
            TargetSchema = schema,
            SourceSrid = sourceSrid,
            TargetSrid = targetSrid,
        });
    }

    private async Task<(double X, double Y, int Srid)> ReadSinglePointAsync(string schema, string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ST_X(geometry), ST_Y(geometry), ST_SRID(geometry) FROM \"{schema}\".\"{table}\" LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the import must have written exactly one row");
        return (reader.GetDouble(0), reader.GetDouble(1), reader.GetInt32(2));
    }

    private async Task EnsureImportFunctionsAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS honua;\n" + CreateImportFunctionsSql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A catalog that always offers a syntactically invalid PROJ pipeline for any pair,
    /// used to prove the import path routes the catalog selection into ST_Transform.
    /// </summary>
    private sealed class InvalidPipelineCatalog : IDatumTransformationCatalog
    {
        public bool TryGetDefault(int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection)
        {
            selection = new DatumTransformationSelection
            {
                Name = "Honua_Test_Invalid_Pipeline",
                FromSrid = fromSrid,
                ToSrid = toSrid,
                ProjPipeline = "+proj=pipeline +step +proj=honua_not_a_real_operation",
            };
            return true;
        }

        public bool TryGetByWkid(int wkid, int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection)
        {
            selection = null;
            return false;
        }
    }

    /// <summary>
    /// A catalog that returns a reverse-direction selection (<c>TransformForward = false</c>) carrying a
    /// forward (and here deliberately invalid) pipeline, used to prove the import path does NOT apply the
    /// forward pipeline in reverse but falls back to PROJ's default path.
    /// </summary>
    private sealed class ReverseDirectionPipelineCatalog : IDatumTransformationCatalog
    {
        public bool TryGetDefault(int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection)
        {
            selection = new DatumTransformationSelection
            {
                Name = "Honua_Test_Reverse_Pipeline",
                FromSrid = fromSrid,
                ToSrid = toSrid,
                ProjPipeline = "+proj=pipeline +step +proj=honua_not_a_real_operation",
                TransformForward = false,
            };
            return true;
        }

        public bool TryGetByWkid(int wkid, int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection)
        {
            selection = null;
            return false;
        }
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schemaName}\", honua, public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
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
