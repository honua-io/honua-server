// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.FileImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Regression tests for honua-server#1400 — a streamed file import created the
/// staging table (<c>imported_&lt;table&gt;</c>) only when OverwriteExisting was set, so a
/// default upload streamed into a table that was never created and the first batch
/// failed with Npgsql 42P01 ("relation does not exist"). The staging table is now
/// always created on the same connection before the load, and a PostgresException
/// surfaces its server message instead of the generic "Import failed.".
/// </summary>
[Collection("Database")]
public sealed class StreamingFileImportStagingTableTests(PostgresFixture fixture)
{
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

        -- The default import path is Replace, which streams into a fresh
        -- <table>__staging sibling (honua.create_import_staging_table) and then
        -- atomically renames it over the live table (honua.swap_import_table).
        -- Mirrors src/Honua.Server/Migrations/070_AddImportLoadModes.sql so the
        -- skip-migration compat fixture has the staging-swap functions the
        -- StreamingFileImportService replace path now requires.
        CREATE OR REPLACE FUNCTION honua.create_import_staging_table(schema_name text, table_name text, target_srid integer DEFAULT 4326)
        RETURNS text
        LANGUAGE plpgsql
        AS $$
        DECLARE
            staging_name text;
        BEGIN
            staging_name := table_name || '__staging';
            EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
            EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, staging_name);
            EXECUTE format(
                'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
                schema_name, staging_name, target_srid);
            EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || staging_name || '_geometry', schema_name, staging_name);
            RETURN staging_name;
        END;
        $$;

        CREATE OR REPLACE FUNCTION honua.swap_import_table(schema_name text, table_name text)
        RETURNS void
        LANGUAGE plpgsql
        AS $$
        DECLARE
            staging_name text;
        BEGIN
            staging_name := table_name || '__staging';
            EXECUTE format('DROP TABLE IF EXISTS %I.%I CASCADE', schema_name, table_name);
            EXECUTE format('ALTER TABLE %I.%I RENAME TO %I', schema_name, staging_name, table_name);
            EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
                schema_name, 'idx_' || staging_name || '_geometry', 'idx_' || table_name || '_geometry');
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
        """;

    private const string PointGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [1, 2] }, "properties": { "name": "a" } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [3, 4] }, "properties": { "name": "b" } }
          ]
        }
        """;

    // A self-intersecting "bowtie" polygon: the exterior ring crosses itself, so the geometry is
    // topologically invalid but structurally well-formed (closed ring, finite coordinates). Under
    // the shared validity gate (default Repair) it is fixed with ST_MakeValid-equivalent repair
    // instead of failing the whole file the way the old GeoJSON topology preflight did (#2743).
    private const string SelfIntersectingPolygonGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature",
              "geometry": { "type": "Polygon", "coordinates": [[[0,0],[2,2],[2,0],[0,2],[0,0]]] },
              "properties": { "name": "bowtie" } }
          ]
        }
        """;

    [IntegrationTest]
    public async Task ImportFileAsync_WithoutOverwrite_CreatesStagingTableAndSucceeds()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests));
        try
        {
            await EnsureImportFunctionsAsync();

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "points.geojson",
                TableName = "demo",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                // The previously-broken default: do NOT request an overwrite.
                OverwriteExisting = false,
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(2);

            // The staging table is created as imported_<sanitized-lowercase-table>.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".imported_demo;";
            var rows = (int)(await command.ExecuteScalarAsync())!;
            rows.Should().Be(2, "the load must stream into a staging table that was created and visible");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_WithSelfIntersectingPolygon_RepairsAndReportsCount()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests));
        try
        {
            await EnsureImportFunctionsAsync();

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SelfIntersectingPolygonGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "bowtie.geojson",
                TableName = "repaired_demo",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
            });

            // The default validity gate repairs the invalid ring rather than failing the file.
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
            result.RepairedGeometryCount.Should().Be(1);
            result.Warnings.Should().Contain(w => w.Contains("repaired", StringComparison.OrdinalIgnoreCase));

            // The stored geometry must be topologically valid after repair.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT bool_and(ST_IsValid(geometry))::boolean FROM \"{schema}\".imported_repaired_demo;";
            var allValid = (bool)(await command.ExecuteScalarAsync())!;
            allValid.Should().BeTrue("the shared validity gate must repair invalid input geometry before insertion");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_WhenImportFunctionMissing_ReturnsSanitizedActionableError()
    {
        // No EnsureImportFunctionsAsync() call: honua.create_import_table is absent, so the
        // staging-table creation raises a PostgresException. The service must return a
        // sanitized, SQLSTATE-coded message instead of relaying the raw server message
        // (which leaks relation/function/schema names) or collapsing to "Import failed.".
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests));
        try
        {
            await DropImportFunctionsAsync();

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "points.geojson",
                TableName = "demo_missing_fn",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
            });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().StartWith("Import failed:");
            result.ErrorMessage.Should().NotBe("Import failed.");
            // The sanitized message must not leak provider internals.
            result.ErrorMessage.Should().NotContain("honua.create_import_table");
            result.ErrorMessage.Should().NotContain("does not exist");
            // But it must include the standardized SQLSTATE so operators can correlate.
            result.ErrorMessage.Should().Contain("SQL state");
        }
        finally
        {
            // Restore the shared import functions so later tests in the Database
            // collection are unaffected, then drop the isolated schema.
            await EnsureImportFunctionsAsync();
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task EnsureImportFunctionsAsync()
    {
        // honua-server#1568 (signature 2): this creates the literal, process-global honua schema
        // and import functions (search_path isolation does not scope them), so apply it under the
        // shared seed advisory lock to keep parallel [Collection("Database")] tests off the 40P01
        // deadlock path racing locks on the same global schema/pg_proc objects.
        await fixture.ApplyGlobalSeedSqlAsync("CREATE SCHEMA IF NOT EXISTS honua;\n" + CreateImportFunctionsSql);
    }

    private async Task DropImportFunctionsAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        // Drop the staging-swap functions too: the default import path is Replace,
        // which calls honua.create_import_staging_table FIRST, so the missing-function
        // assertion only fires if the staging creator is also absent.
        cmd.CommandText = """
            DROP FUNCTION IF EXISTS honua.create_import_staging_table(text, text, integer);
            DROP FUNCTION IF EXISTS honua.swap_import_table(text, text);
            DROP FUNCTION IF EXISTS honua.create_import_table(text, text, integer);
            DROP FUNCTION IF EXISTS honua.insert_import_feature(text, text, bytea, integer, integer, jsonb);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IAdoNetDatabaseConnectionProvider
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
