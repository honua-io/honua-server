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
    public async Task ImportFileAsync_WhenImportFunctionMissing_SurfacesUnderlyingPostgresMessage()
    {
        // No EnsureImportFunctionsAsync() call: honua.create_import_table is absent, so the
        // staging-table creation raises a PostgresException. The service must surface the
        // server message instead of collapsing to the generic "Import failed.".
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
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task EnsureImportFunctionsAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS honua;\n" + CreateImportFunctionsSql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DropImportFunctionsAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DROP FUNCTION IF EXISTS honua.create_import_table(text, text, integer);
            DROP FUNCTION IF EXISTS honua.insert_import_feature(text, text, bytea, integer, integer, jsonb);
            """;
        await cmd.ExecuteNonQueryAsync();
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
