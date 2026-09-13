// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

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

        CREATE OR REPLACE FUNCTION honua.ensure_import_table(schema_name text, table_name text, target_srid integer DEFAULT 4326)
        RETURNS void
        LANGUAGE plpgsql
        AS $$
        BEGIN
            EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
            EXECUTE format(
                'CREATE TABLE IF NOT EXISTS %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
                schema_name, table_name, target_srid);
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
            IF length(staging_name) > 63 THEN
                staging_name := 'stg_' || md5(table_name);
            END IF;
            EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
            EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, staging_name);
            EXECUTE format(
                'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
                schema_name, staging_name, target_srid);
            EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || staging_name || '_geometry', schema_name, staging_name);
            EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || staging_name || '_properties', schema_name, staging_name);
            RETURN staging_name;
        END;
        $$;

        -- Mirrors src/Honua.Server/Migrations/115_AddDropImportStagingTable.sql: a replace
        -- that must not promote an incomplete staging sibling (#4006) drops it instead.
        CREATE OR REPLACE FUNCTION honua.drop_import_staging_table(schema_name text, table_name text)
        RETURNS void
        LANGUAGE plpgsql
        AS $$
        DECLARE
            staging_name text;
        BEGIN
            staging_name := table_name || '__staging';
            IF length(staging_name) > 63 THEN
                staging_name := 'stg_' || md5(table_name);
            END IF;
            EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, staging_name);
        END;
        $$;

        CREATE OR REPLACE FUNCTION honua.import_index_name(table_name text, index_kind text)
        RETURNS text LANGUAGE plpgsql IMMUTABLE AS $$
        DECLARE index_name text;
        BEGIN
            index_name := 'idx_' || table_name || '_' || index_kind;
            IF length(index_name) > 63 THEN
                index_name := left(index_name, 46) || '_' || left(md5(index_name), 16);
            END IF;
            RETURN index_name;
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
            IF length(staging_name) > 63 THEN
                staging_name := 'stg_' || md5(table_name);
            END IF;
            EXECUTE format('DROP TABLE IF EXISTS %I.%I CASCADE', schema_name, table_name);
            EXECUTE format('ALTER TABLE %I.%I RENAME TO %I', schema_name, staging_name, table_name);
            EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
                schema_name, 'idx_' || staging_name || '_geometry', honua.import_index_name(table_name, 'geometry'));
            EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
                schema_name, 'idx_' || staging_name || '_properties', honua.import_index_name(table_name, 'properties'));
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

    [Theory]
    [InlineData(ImportLoadMode.Replace, 2)]
    [InlineData(ImportLoadMode.Append, 3)]
    public async Task ImportFileAsync_LongNameWithLegacyTable_PreservesPhysicalIdentity(
        ImportLoadMode loadMode,
        int expectedRowCount)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("legacy_names");
        const string logicalName = "legacy_identifier_imported_before_hash_suffix";
        var legacyName = "imported_" + logicalName;
        try
        {
            await EnsureImportFunctionsAsync();
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE TABLE "{schema}"."{legacyName}" (
                        id SERIAL PRIMARY KEY,
                        geometry GEOMETRY(Geometry, 4326),
                        properties JSONB,
                        created_at TIMESTAMPTZ DEFAULT NOW());
                    INSERT INTO "{schema}"."{legacyName}" (geometry, properties)
                    VALUES (ST_SetSRID(ST_MakePoint(0, 0), 4326), jsonb_build_object('name', 'legacy'));
                    """;
                await command.ExecuteNonQueryAsync();
            }

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
                TableName = logicalName,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = loadMode,
                OverwriteExisting = loadMode == ImportLoadMode.Replace,
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.PhysicalTableName.Should().Be(legacyName);

            await using var verificationConnection = await fixture.DataSource.OpenConnectionAsync();
            await using var verificationCommand = verificationConnection.CreateCommand();
            verificationCommand.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".\"{legacyName}\"";
            (await verificationCommand.ExecuteScalarAsync()).Should().Be(expectedRowCount);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    // honua-server#4002 fixture. The expected rows below are read off the fixture file and the
    // seed statements by hand, never captured from a run of the importer.
    private const string OverwriteGuardFixtureFileName = "overwrite-existing-false.geojson";

    private static readonly (string Name, int Code, string Wkt)[] SeededLiveRows =
    [
        ("live-one", 1001, "POINT(1.5 2.25)"),
        ("live-two", 1002, "POINT(-3.75 4.5)"),
    ];

    private static readonly (string Name, int Code, string Wkt)[] OverwriteGuardFixtureRows =
    [
        ("import-alpha", 4002, "POINT(11.25 -22.5)"),
        ("import-beta", 4003, "POINT(-33.75 44.125)"),
    ];

    /// <summary>
    /// honua-server#4002 (P0) — <c>ImportFileAsync</c> replaced a live target even when the
    /// caller explicitly declined the overwrite. The importer always took the replace path, so
    /// an import of two features with <c>OverwriteExisting = false</c> reported
    /// <c>Success = true</c> while every pre-existing row was silently destroyed.
    ///
    /// The request below is the exact reported shape: the legacy flag is set to
    /// <see langword="false"/> and <see cref="ImportRequest.LoadMode"/> is left at its default,
    /// so the reconciliation in <see cref="ImportRequest.EffectiveLoadMode"/> is what has to
    /// hold. Every expected value is computed from the seed statements and the fixture file:
    /// the seeded rows keep their ordinates, properties and surrogate ids, and the two fixture
    /// features are appended verbatim. <c>pg_class.oid</c> is the structural witness — append
    /// streams into the live relation, so its identity must survive rather than being replaced
    /// by a promoted staging sibling.
    /// </summary>
    [IntegrationTest]
    public async Task ImportFileAsync_ExistingTarget_WithOverwriteFalse_DoesNotReplaceLiveRows()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("overwrite_guard");
        try
        {
            await EnsureImportFunctionsAsync();
            await SeedOverwriteGuardTargetAsync(schema);
            var relationIdBefore = await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable);

            var result = await ImportOverwriteGuardFixtureAsync(schema, overwriteExisting: false);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(2);
            result.PhysicalTableName.Should().Be(OverwriteGuardPhysicalTable);

            // The live relation itself must be the one that was loaded: a replace promotes a
            // staging sibling with ALTER TABLE ... RENAME, which changes the relation's oid.
            (await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable))
                .Should().Be(relationIdBefore, "an explicit non-overwrite import must not replace the live relation");

            var rows = await ReadOverwriteGuardRowsAsync(schema);

            // Both seeded rows survive unchanged — same surrogate id, same ordinates, same
            // properties — and the fixture's two features are appended after them.
            rows.Should().HaveCount(4);
            rows.Take(2).Should().Equal(
                (1, SeededLiveRows[0].Name, SeededLiveRows[0].Code, SeededLiveRows[0].Wkt),
                (2, SeededLiveRows[1].Name, SeededLiveRows[1].Code, SeededLiveRows[1].Wkt));
            rows.Skip(2).Select(row => (row.Name, row.Code, row.Wkt)).Should().Equal(
                OverwriteGuardFixtureRows[0],
                OverwriteGuardFixtureRows[1]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    /// <summary>
    /// The discriminating control for
    /// <see cref="ImportFileAsync_ExistingTarget_WithOverwriteFalse_DoesNotReplaceLiveRows"/>:
    /// the same seed and the same fixture with <c>OverwriteExisting = true</c> must still
    /// replace the live rows and promote a new relation. Without this the preservation
    /// assertion above could be satisfied by an importer that never replaces anything.
    /// </summary>
    [IntegrationTest]
    public async Task ImportFileAsync_ExistingTarget_WithOverwriteTrue_ReplacesLiveRows()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("overwrite_guard_replace");
        try
        {
            await EnsureImportFunctionsAsync();
            await SeedOverwriteGuardTargetAsync(schema);
            var relationIdBefore = await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable);

            var result = await ImportOverwriteGuardFixtureAsync(schema, overwriteExisting: true);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(2);

            (await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable))
                .Should().NotBe(relationIdBefore, "replace promotes a freshly built staging sibling over the live table");

            var rows = await ReadOverwriteGuardRowsAsync(schema);
            rows.Should().HaveCount(2);
            rows.Select(row => (row.Name, row.Code, row.Wkt)).Should().Equal(
                OverwriteGuardFixtureRows[0],
                OverwriteGuardFixtureRows[1]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    /// <summary>
    /// honua-server#4006 — a replace whose load skipped an invalid feature under
    /// <c>ContinueOnError</c>/<c>SkipInvalidGeometry</c> still promoted the staging sibling over
    /// the live target, reporting <c>Success = true</c> even though the promoted dataset was
    /// missing the skipped row. A replace must be complete-for-complete or a no-op: it must
    /// never swap in a dataset that dropped input rows, and it must report the loss rather than
    /// claiming success.
    /// </summary>
    [IntegrationTest]
    public async Task ImportFileAsync_ReplaceWithSkippedHostileFeature_DoesNotPromotePartialDataset()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests) + "_partial_replace");
        try
        {
            await EnsureImportFunctionsAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var strictSkipLimits = ImportLimits.Default with
            {
                GeometryValidityMode = Honua.Core.Configuration.ValidationMode.Strict,
                SkipInvalidGeometry = true,
                ContinueOnError = true,
            };
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance,
                strictSkipLimits);

            await using (var seed = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson)))
            {
                var seedResult = await service.ImportFileAsync(new ImportRequest
                {
                    FileStream = seed,
                    FileName = "seed.geojson",
                    TableName = "partial_replace_guard",
                    TargetSchema = schema,
                    SourceSrid = 4326,
                    TargetSrid = 4326,
                    OverwriteExisting = true,
                });
                seedResult.Success.Should().BeTrue(seedResult.ErrorMessage);
            }

            // All segments are fixed literals and can never be rooted, so Path.Join cannot drop
            // earlier segments here (cs/path-combine false positive).
            var fixturePath = Path.Join(
                AppContext.BaseDirectory, "Features", "Import", "Fixtures", "LoadMode", "mixed-validity-replace.geojson");
            await using var hostile = File.OpenRead(fixturePath);
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = hostile,
                FileName = "mixed-validity-replace.geojson",
                TableName = "partial_replace_guard",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                OverwriteExisting = true,
            });

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            int liveRowCount;
            int seededRowCount;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT COUNT(*)::int, COUNT(*) FILTER (WHERE properties->>'name' IN ('a', 'b'))::int FROM \"{schema}\".imported_partial_replace_guard";
                await using var reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                liveRowCount = reader.GetInt32(0);
                seededRowCount = reader.GetInt32(1);
            }

            // The never-promoted staging sibling must be gone: honua.drop_import_staging_table
            // cleans it up so a blocked replace does not leave a full second copy of the dataset
            // behind. A missing helper would surface here rather than as a silent leak.
            bool stagingSurvived;
            await using (var stagingCommand = connection.CreateCommand())
            {
                stagingCommand.CommandText = """
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_class AS relation
                        INNER JOIN pg_catalog.pg_namespace AS namespace
                            ON namespace.oid = relation.relnamespace
                        WHERE namespace.nspname = @schema_name
                          AND relation.relname = 'imported_partial_replace_guard__staging')
                    """;
                var schemaParameter = stagingCommand.CreateParameter();
                schemaParameter.ParameterName = "schema_name";
                schemaParameter.Value = schema;
                stagingCommand.Parameters.Add(schemaParameter);
                stagingSurvived = (bool)(await stagingCommand.ExecuteScalarAsync())!;
            }

            using (new AssertionScope())
            {
                result.Success.Should().BeFalse("a replace with skipped input rows must not claim a complete successful replacement");
                result.Warnings.Should().Contain(w => w.Contains("skipped", StringComparison.OrdinalIgnoreCase));
                liveRowCount.Should().Be(2, "a partial replacement must leave the prior complete target in place");
                seededRowCount.Should().Be(2, "the prior rows must survive the skipped hostile feature");
                stagingSurvived.Should().BeFalse("the never-promoted staging sibling must be dropped, not left behind as a second copy");
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    /// <summary>
    /// honua-server#4422 — the streaming loop's only <c>finally</c> released the advisory lock;
    /// nothing dropped the <c>&lt;table&gt;__staging</c> sibling when a Replace load THREW (as
    /// opposed to completing with a counted failure, which
    /// <see cref="ImportFileAsync_ReplaceWithSkippedHostileFeature_DoesNotPromotePartialDataset"/>
    /// already covers). Strict validity mode with <c>SkipInvalidGeometry=false</c> and
    /// <c>ContinueOnError=false</c> makes the invalid geometry throw
    /// <see cref="InvalidOperationException"/> out of <c>InsertBatchFastAsync</c>, uncaught,
    /// after <c>CreateStagingTableAsync</c> already created the sibling — exactly the leak the
    /// issue reports. Every expected value here is a fixed, independently-known ground truth (no
    /// table of any kind, staging or live, may exist for a target that was never successfully
    /// created), not a snapshot of whatever the importer happens to leave behind.
    /// </summary>
    [IntegrationTest]
    public async Task ImportFileAsync_ReplaceThrowsMidStream_DropsOrphanedStagingTable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests) + "_throw_cleanup");
        const string logicalTable = "cleanup_on_throw";
        const string physicalTable = "imported_" + logicalTable;
        const string stagingTable = physicalTable + "__staging";
        try
        {
            await EnsureImportFunctionsAsync();

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var strictLimits = ImportLimits.Default with
            {
                GeometryValidityMode = Honua.Core.Configuration.ValidationMode.Strict,
                SkipInvalidGeometry = false,
                ContinueOnError = false,
            };
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance,
                strictLimits);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SelfIntersectingPolygonGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "bowtie_throw.geojson",
                TableName = logicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.Replace,
                OverwriteExisting = true,
            });

            bool stagingExists = await TableExistsAsync(schema, stagingTable);
            bool liveExists = await TableExistsAsync(schema, physicalTable);

            using (new AssertionScope())
            {
                result.Success.Should().BeFalse("Strict mode with SkipInvalidGeometry=false must reject the invalid geometry rather than storing or repairing it");
                stagingExists.Should().BeFalse("a Replace load that throws mid-stream must not leave its <table>__staging sibling behind");
                liveExists.Should().BeFalse("the target was never successfully created, so nothing may exist under its live name either");
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    public async Task ImportFileAsync_ReplaceInterruptedAfterCommittedBatch_PreservesLiveRowsAndReclaimsAllStagingRelations(bool cancel)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("replace_interrupt");
        try
        {
            await EnsureImportFunctionsAsync();
            await SeedOverwriteGuardTargetAsync(schema);
            var liveOid = await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable);
            using var cancellation = new CancellationTokenSource();
            var committedBatches = 0;
            var progress = new InlineImportProgress(value =>
            {
                if (value.BatchesCommitted > 0)
                {
                    committedBatches = value.BatchesCommitted;
                    if (cancel)
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    }
                    throw new IOException("fixture failure after committed batch");
                }
            });
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(), new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance, ImportLimits.Default with { BatchSize = 1 });
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
            var request = new ImportRequest
            {
                FileStream = stream, FileName = "interrupted.geojson", TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema, SourceSrid = 4326, TargetSrid = 4326,
                LoadMode = ImportLoadMode.Replace, OverwriteExisting = true
            };
            var action = () => service.ImportFileAsync(request, progress, cancellation.Token);
            if (cancel)
            {
                await action.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                (await action()).Success.Should().BeFalse();
            }
            committedBatches.Should().Be(1, "interrupt only after a batch has committed to staging");
            (await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable)).Should().Be(liveOid);
            var rows = await ReadOverwriteGuardRowsAsync(schema);
            rows.Select(row => (row.Name, row.Code, row.Wkt)).Should().Equal(SeededLiveRows);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            // Include indexes and sequences, not just tables: no staging relation may remain.
            command.CommandText = "SELECT relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND relname LIKE '%__staging%'";
            command.Parameters.AddWithValue("schema", schema);
            (await command.ExecuteScalarAsync()).Should().BeNull();
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

    private async Task<bool> TableExistsAsync(string schema, string tableName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                INNER JOIN pg_catalog.pg_namespace AS namespace
                    ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = @schema_name
                  AND relation.relname = @table_name)
            """;
        var schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "schema_name";
        schemaParameter.Value = schema;
        command.Parameters.Add(schemaParameter);
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table_name";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private const string OverwriteGuardLogicalTable = "overwrite_guard";
    private const string OverwriteGuardPhysicalTable = "imported_" + OverwriteGuardLogicalTable;

    private async Task SeedOverwriteGuardTargetAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE "{schema}"."{OverwriteGuardPhysicalTable}" (
                id SERIAL PRIMARY KEY,
                geometry GEOMETRY(Geometry, 4326),
                properties JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW());
            INSERT INTO "{schema}"."{OverwriteGuardPhysicalTable}" (geometry, properties)
            VALUES
                (ST_GeomFromText(@wkt_one, 4326), jsonb_build_object('name', @name_one, 'code', @code_one)),
                (ST_GeomFromText(@wkt_two, 4326), jsonb_build_object('name', @name_two, 'code', @code_two));
            """;
        command.Parameters.AddWithValue("wkt_one", SeededLiveRows[0].Wkt);
        command.Parameters.AddWithValue("name_one", SeededLiveRows[0].Name);
        command.Parameters.AddWithValue("code_one", SeededLiveRows[0].Code);
        command.Parameters.AddWithValue("wkt_two", SeededLiveRows[1].Wkt);
        command.Parameters.AddWithValue("name_two", SeededLiveRows[1].Name);
        command.Parameters.AddWithValue("code_two", SeededLiveRows[1].Code);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ImportResult> ImportOverwriteGuardFixtureAsync(string schema, bool overwriteExisting)
    {
        var provider = new TestConnectionProvider(fixture.DataSource, schema);
        var service = new StreamingFileImportService(
            provider,
            new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
            new TestFileFormatDetectionService(),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance);

        // All segments are fixed literals and can never be rooted, so Path.Join cannot drop
        // earlier segments here (cs/path-combine false positive).
        var fixturePath = Path.Join(
            AppContext.BaseDirectory,
            "Features",
            "Import",
            "Fixtures",
            "LoadMode",
            OverwriteGuardFixtureFileName);

        await using var stream = File.OpenRead(fixturePath);
        return await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = OverwriteGuardFixtureFileName,
            TableName = OverwriteGuardLogicalTable,
            TargetSchema = schema,
            SourceSrid = 4326,
            TargetSrid = 4326,
            // The reported shape: the legacy flag alone, with LoadMode left at its default.
            OverwriteExisting = overwriteExisting,
        });
    }

    private async Task<long> ReadRelationIdAsync(string schema, string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.oid::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema_name AND c.relname = @table_name
            """;
        command.Parameters.AddWithValue("schema_name", schema);
        command.Parameters.AddWithValue("table_name", table);
        var relationId = await command.ExecuteScalarAsync();
        relationId.Should().NotBeNull($"{schema}.{table} must exist");
        return (long)relationId!;
    }

    private async Task<List<(int Id, string Name, int Code, string Wkt)>> ReadOverwriteGuardRowsAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, properties->>'name', (properties->>'code')::int, ST_AsText(geometry)
            FROM "{schema}"."{OverwriteGuardPhysicalTable}"
            ORDER BY id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(int, string, int, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return rows;
    }

    [Fact]
    public async Task ImportFileAsync_TruncatedLegacyPrefix_DoesNotClaimDifferentLogicalName()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("legacy_collision");
        const string sharedPrefix = "legacy_identifier_with_a_shared_prefix_that_is_long_eno_";
        var firstLogicalName = sharedPrefix + "first";
        var secondLogicalName = sharedPrefix + "second";
        var truncatedLegacyName = ("imported_" + firstLogicalName)[..63];
        try
        {
            await EnsureImportFunctionsAsync();
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"CREATE TABLE \"{schema}\".\"{truncatedLegacyName}\" (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, 4326), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())";
                await command.ExecuteNonQueryAsync();
            }

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
                TableName = secondLogicalName,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.Append,
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.PhysicalTableName.Should().NotBe(truncatedLegacyName);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_ConcurrentReplaceImportsToSameTarget_IsolatedUntilPromotion()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("concurrent_replace");
        GateStream? firstStream = null;
        Task<ImportResult>? firstTask = null;
        try
        {
            await EnsureImportFunctionsAsync();
            var firstProvider = new TestConnectionProvider(fixture.DataSource, schema);
            var secondProvider = new TestConnectionProvider(fixture.DataSource, schema);
            var firstService = new StreamingFileImportService(
                firstProvider,
                new CrsDetectionService(firstProvider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance);
            var secondService = new StreamingFileImportService(
                secondProvider,
                new CrsDetectionService(secondProvider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance);

            firstStream = new GateStream(Encoding.UTF8.GetBytes("POINT(10 10)"));
            firstTask = firstService.ImportFileAsync(new ImportRequest
            {
                FileStream = firstStream,
                FileName = "first.wkt",
                TableName = "concurrent_replace_guard",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.Replace,
                OverwriteExisting = true,
            });
            await firstStream.WaitForReadStartedAsync();

            await using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes("POINT(20 20)"));
            var secondTask = secondService.ImportFileAsync(new ImportRequest
            {
                FileStream = secondStream,
                FileName = "second.wkt",
                TableName = "concurrent_replace_guard",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.Replace,
                OverwriteExisting = true,
            });

            // Give the second request a chance to contend while the first owns the target's
            // staging state. Release the first only after the overlap is established.
            await Task.Delay(100);
            firstStream.Release();
            var firstResult = await firstTask;
            var secondResult = await secondTask;

            firstResult.Success.Should().BeTrue(firstResult.ErrorMessage);
            secondResult.Success.Should().BeTrue(secondResult.ErrorMessage);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".imported_concurrent_replace_guard";
            (await command.ExecuteScalarAsync()).Should().Be(1);
        }
        finally
        {
            firstStream?.Release();
            if (firstTask is not null)
            {
                // Observe completion without allowing cleanup to replace the primary assertion.
                await ((Task)firstTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await fixture.DropSchemaAsync(schema);
        }
    }

    private sealed class GateStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public Task WaitForReadStartedAsync() => _readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }
    }

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

    // One valid point plus the invalid bowtie polygon, for proving that a Strict+skip gate
    // excludes only the invalid feature (and excludes it entirely, not as a null-geometry row).
    private const string MixedValidityGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [1, 2] }, "properties": { "name": "valid" } },
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
    public async Task ImportFileAsync_WithSelfIntersectingPolygon_StrictMode_FailsWithoutStoringGeometry()
    {
        // With GeometryValidityMode=Strict, SkipInvalidGeometry=false, and ContinueOnError=false the
        // shared validity gate rejects the invalid geometry and the import fails rather than storing
        // or repairing it. Reachable end-to-end now that the config binding honors the mode (#2743).
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests));
        try
        {
            await EnsureImportFunctionsAsync();

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var strictLimits = ImportLimits.Default with
            {
                GeometryValidityMode = Honua.Core.Configuration.ValidationMode.Strict,
                SkipInvalidGeometry = false,
                ContinueOnError = false,
            };
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance,
                strictLimits);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SelfIntersectingPolygonGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "bowtie_strict.geojson",
                TableName = "strict_demo",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
            });

            result.Success.Should().BeFalse();
            result.RepairedGeometryCount.Should().Be(0);
            result.ErrorMessage.Should().StartWith("Import failed");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_WithSelfIntersectingPolygon_StrictModeSkip_ExcludesFeatureEntirely()
    {
        // With GeometryValidityMode=Strict and SkipInvalidGeometry=true the invalid feature must be
        // excluded from the insert entirely — not degraded to a null-geometry row that still imports
        // its properties (a null WKB is a legal null-geometry row, so the gate needs a distinct skip
        // signal). The valid sibling feature still imports and the loss is surfaced as a warning.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests));
        try
        {
            await EnsureImportFunctionsAsync();

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var strictSkipLimits = ImportLimits.Default with
            {
                GeometryValidityMode = Honua.Core.Configuration.ValidationMode.Strict,
                SkipInvalidGeometry = true,
            };
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance,
                strictSkipLimits);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(MixedValidityGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "bowtie_strict_skip.geojson",
                TableName = "strict_skip_demo",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1, "only the valid feature may import");
            result.RepairedGeometryCount.Should().Be(0);
            result.Warnings.Should().Contain(w => w.Contains("skipped", StringComparison.OrdinalIgnoreCase));

            // The invalid feature must not exist at all — previously it imported as a
            // null-geometry row carrying its properties.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(*)::int, COUNT(*) FILTER (WHERE geometry IS NULL)::int FROM \"{schema}\".imported_strict_skip_demo;";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(1, "the skipped feature must not become a row");
            reader.GetInt32(1).Should().Be(0, "no null-geometry row may be created for the skipped feature");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_WithSelfIntersectingPolygon_AcceptMode_StoresGeometryAsIs()
    {
        // With GeometryValidityMode=Accept the validity gate is bypassed: the invalid geometry is
        // stored as-is (no repair, no rejection). Reachable now that the config binding honors the
        // mode (#2743).
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests));
        try
        {
            await EnsureImportFunctionsAsync();

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var acceptLimits = ImportLimits.Default with
            {
                GeometryValidityMode = Honua.Core.Configuration.ValidationMode.Accept,
            };
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance,
                acceptLimits);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SelfIntersectingPolygonGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "bowtie_accept.geojson",
                TableName = "accept_demo",
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
            result.RepairedGeometryCount.Should().Be(0);

            // The geometry is stored unrepaired, so it remains topologically invalid.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT bool_and(ST_IsValid(geometry))::boolean FROM \"{schema}\".imported_accept_demo;";
            var allValid = (bool)(await command.ExecuteScalarAsync())!;
            allValid.Should().BeFalse("Accept mode must store the invalid geometry without repair");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_GeoPackageWithLocalSrsId_ResolvesEpsgAndTransformsGeometry()
    {
        // A spec-legal GeoPackage may number srs_id locally (srs_id=1 mapping to EPSG:27700 via
        // gpkg_spatial_ref_sys) and real writers stamp that local id into every geometry blob
        // header. The import must georeference rows with the resolved EPSG code — never the local
        // id — end to end: CRS detection resolves 27700, the reader stamps it over the blob header
        // SRID, and the insert transforms 27700 -> 4326 (#2743).
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StreamingFileImportStagingTableTests));
        // The filename segment is a fixed literal + GUID token and can never be rooted, so
        // Path.Combine cannot drop the temp-path segment here (cs/path-combine false positive).
        var filePath = Path.Join(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");
        try
        {
            await EnsureImportFunctionsAsync();

            // A point near Charing Cross, London, in British National Grid (EPSG:27700) metres.
            GeoPackageTestFiles.Create(
                filePath,
                srsId: 1,
                organization: "EPSG",
                organizationCoordsysId: 27700,
                blobSrid: 1,
                x: 530000,
                y: 180000);

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new TestFileFormatDetectionService(),
                new NoopPerformanceMonitor(),
                NullLogger<StreamingFileImportService>.Instance);

            await using var stream = File.OpenRead(filePath);
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "local_srs.gpkg",
                TableName = "local_srs_demo",
                TargetSchema = schema,
                // No explicit SourceSrid: detection must resolve the local srs_id to 27700.
                TargetSrid = 4326,
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
            result.DetectedSrid.Should().Be(27700);

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT ST_X(geometry), ST_Y(geometry) FROM \"{schema}\".imported_local_srs_demo;";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetDouble(0).Should().BeApproximately(-0.129, 0.05, "easting 530000 must transform as BNG metres, not the local srs_id");
            reader.GetDouble(1).Should().BeApproximately(51.503, 0.05, "northing 180000 must transform as BNG metres, not the local srs_id");
        }
        finally
        {
            await GeoPackageTestFiles.DeleteAsync(filePath);
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
                OverwriteExisting = true,
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
