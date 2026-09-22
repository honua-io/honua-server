// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Db.Postgres.Features.Raster;
using Honua.Db.Postgres.Features.SensorThings;
using Honua.Server.Startup;
using Honua.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-PostgreSQL receipts for the migration-integrity mismatches named by #3899. Each case
/// proves both preflight/startup and runtime checks fail closed without changing physical schema
/// or advancing the journal.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
public sealed class CoreSchemaDivergenceGuardTests(LocalSubstratePostgresFixture postgres)
    : IClassFixture<LocalSubstratePostgresFixture>
{
    public static TheoryData<string, int, int, string, DatabaseSchemaFloorFailureKind> DivergenceCases =>
        new()
        {
            {
                """
                CREATE SCHEMA honua;
                CREATE TABLE public.schema_versions (scriptname text NOT NULL);
                CREATE TABLE honua.raster_layer_statistics (layer_id integer NOT NULL);
                """,
                (int)DatabaseSchemaRequirement.RasterLayerStatistics,
                (int)StoreOperation.RasterStatisticsRead,
                PostgresCoreSchemaGuard.RasterLayerStatisticsMigration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal
            },
            {
                """
                CREATE SCHEMA honua;
                CREATE TABLE public.schema_versions (scriptname text NOT NULL);
                CREATE TABLE honua.metadata_v2_snapshots (environment text NOT NULL);
                """,
                (int)DatabaseSchemaRequirement.MetadataV2Snapshot,
                (int)StoreOperation.MetadataRead,
                ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal
            },
            {
                """
                CREATE SCHEMA honua;
                CREATE TABLE public.schema_versions (scriptname text NOT NULL);
                CREATE TABLE honua.raster_data (raster text NOT NULL);
                INSERT INTO public.schema_versions (scriptname)
                VALUES ('Honua.Server.Migrations.055_SetRasterDataExternalStorage.sql');
                """,
                (int)DatabaseSchemaRequirement.RasterExternalStorage,
                (int)StoreOperation.RasterImportWrite,
                ServerCoreSchemaMigrations.Manifest.RasterExternalStorageMigration,
                DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema
            },
            {
                """
                CREATE SCHEMA honua;
                CREATE TABLE public.schema_versions (scriptname text NOT NULL);
                CREATE TABLE honua.sta_thing (id bigint NOT NULL);
                """,
                (int)DatabaseSchemaRequirement.SensorThings,
                (int)StoreOperation.SensorThingsRead,
                ServerCoreSchemaMigrations.Manifest.SensorThingsMigration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal
            },
            {
                """
                CREATE SCHEMA honua;
                CREATE TABLE public.schema_versions (scriptname text NOT NULL);
                CREATE TABLE honua.metadata_v2_release_packages (package_id uuid NOT NULL);
                """,
                (int)DatabaseSchemaRequirement.MetadataV2ReleasePackages,
                (int)StoreOperation.MetadataReleasePackageRead,
                ServerCoreSchemaMigrations.Manifest.MetadataV2ReleasePackagesMigration,
                DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal
            },
        };

    [SkippableTheory]
    [MemberData(nameof(DivergenceCases))]
    public async Task Guard_WhenJournalAndPhysicalSchemaDiverge_FailsClosedWithoutMutation(
        string arrangeSql,
        int requirementValue,
        int storeOperationValue,
        string expectedMigration,
        DatabaseSchemaFloorFailureKind expectedFailureKind)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the schema-divergence lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync();
        await ExecuteAsync(connectionString, arrangeSql);
        var before = await CaptureStateAsync(connectionString);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Schema"] = "honua" })
            .Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var requirement = (DatabaseSchemaRequirement)requirementValue;
        var storeOperation = (StoreOperation)storeOperationValue;

        // Upgrade/preflight receipt: planning cannot report a usable migration plan over a
        // journal/schema mismatch, and the typed cause remains available to the caller.
        var runner = new PostgresDatabaseMigrationRunner(
            guard,
            ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeFalse("upgrade preflight must reject divergent schema state");
        plan.Error.Should().BeOfType<DatabaseSchemaFloorException>();

        // Runtime receipt: exercise the ordinary production read/write entry point that used
        // to replay this migration fragment. The store must surface the same typed terminal
        // error rather than reaching its former CREATE/ALTER fallback.
        var act = () => ExecuteOrdinaryStoreOperationAsync(connectionString, guard, storeOperation);
        var exception = await act.Should().ThrowAsync<DatabaseSchemaFloorException>();
        exception.Which.MigrationScript.Should().Be(expectedMigration);
        exception.Which.FailureKind.Should().Be(expectedFailureKind);

        // Pin the requirement mapping as well as the store wiring.
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var verify = () => guard.VerifyRequirementAsync(connection, requirement);
            await verify.Should().ThrowAsync<DatabaseSchemaFloorException>();
        }

        var after = await CaptureStateAsync(connectionString);
        after.Should().Be(before, "fail-closed verification must not repair schema or advance readiness/journal state");
    }

    [SkippableFact]
    public async Task CanonicalRunner_WhenJournalTableIsMissingButGuardedSchemaExists_FailsClosed()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the schema-divergence lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);
        var baseline = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        baseline.Successful.Should().BeTrue($"the test requires a canonical schema baseline. Error: {baseline.ErrorMessage}");

        await ExecuteAsync(connectionString, "DROP TABLE public.schema_versions;");

        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeFalse("a missing journal over physical guarded schema must fail preflight");
        plan.Error.Should().BeOfType<DatabaseSchemaFloorException>();

        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        result.Successful.Should().BeFalse("migration execution must not replay scripts over an unjournaled schema");
        result.Error.Should().BeOfType<DatabaseSchemaFloorException>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.schema_versions'), to_regclass('honua.metadata_v2_snapshots');";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.IsDBNull(0).Should().BeTrue("the failed run must not recreate the deleted journal");
        reader.IsDBNull(1).Should().BeFalse("the physical guarded schema must remain present for diagnosis");
    }

    // A fixture seed that pre-creates part of the migration-owned schema before the first boot
    // (#4900): two of migration 031's seven metadata tables with a row, the raster baseline table
    // without its migration-owned EXTERNAL storage, and migration 063's overview table.
    private const string PreSeededSchemaSql = """
        CREATE SCHEMA honua;
        CREATE TABLE honua.raster_data (
            id BIGSERIAL PRIMARY KEY,
            layer_id INTEGER NOT NULL,
            name VARCHAR(255) NOT NULL,
            description TEXT,
            raster raster NOT NULL,
            acquisition_date TIMESTAMPTZ,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMPTZ,
            width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
            height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
            band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
            pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
            srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
        );
        CREATE TABLE honua.raster_overviews (
            id BIGSERIAL PRIMARY KEY,
            raster_data_id BIGINT NOT NULL REFERENCES honua.raster_data(id) ON DELETE CASCADE,
            overview_factor INTEGER NOT NULL,
            raster raster NOT NULL,
            ground_resolution DOUBLE PRECISION NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            CONSTRAINT raster_overviews_unique_factor UNIQUE (raster_data_id, overview_factor)
        );
        ALTER TABLE honua.raster_overviews ALTER COLUMN raster SET STORAGE EXTERNAL;
        CREATE TABLE honua.metadata_v2_snapshots (
            environment TEXT NOT NULL,
            revision BIGINT NOT NULL,
            schema_version TEXT NOT NULL,
            api_version TEXT NOT NULL,
            document JSONB NOT NULL,
            etag TEXT NOT NULL,
            generated_at TIMESTAMPTZ NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            PRIMARY KEY (environment, revision)
        );
        CREATE TABLE honua.metadata_v2_current (
            environment TEXT NOT NULL PRIMARY KEY,
            revision BIGINT NOT NULL,
            etag TEXT NOT NULL,
            activated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            FOREIGN KEY (environment, revision)
                REFERENCES honua.metadata_v2_snapshots(environment, revision) ON DELETE RESTRICT
        );
        INSERT INTO honua.metadata_v2_snapshots (environment, revision, schema_version, api_version, document, etag, generated_at)
        VALUES ('seed-4900', 1, '2', 'v2', '{}'::jsonb, 'seed-etag', NOW());
        INSERT INTO honua.metadata_v2_current (environment, revision, etag) VALUES ('seed-4900', 1, 'seed-etag');
        """;

    [SkippableFact]
    public async Task CanonicalRunner_OnNeverMigratedSeededDatabase_FailsClosedNamingEveryUnjournaledFamily()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the seeded-schema lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        await ExecuteAsync(connectionString, PreSeededSchemaSql);
        var before = await CaptureUnmigratedStateAsync(connectionString);
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);

        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeFalse("adoption of seed-created schema is opt-in");
        var failure = result.Error.Should().BeOfType<DatabaseSchemaFloorException>().Which;
        failure.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        failure.MigrationScript.Should().Be(ServerCoreSchemaMigrations.Manifest.RasterOverviewsMigration);
        failure.Detail.Should().Contain(ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration,
            "the failure names every unjournaled family, not only the first one in check order")
            .And.Contain("metadata_v2_snapshots, metadata_v2_current")
            .And.Contain("Database:AdoptSeededSchemaOnFirstMigration=true");
        (await CaptureUnmigratedStateAsync(connectionString)).Should().Be(before,
            "a refused first run must not create the journal or any table");
    }

    [SkippableFact]
    public async Task CanonicalRunner_OnNeverMigratedSeededDatabase_WhenAdoptionIsOptedIn_AdoptsAndVerifiesTheFloor()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the seeded-schema lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        await ExecuteAsync(connectionString, PreSeededSchemaSql);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:AdoptSeededSchemaOnFirstMigration"] = "true" })
            .Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest, configuration: configuration);

        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue($"preflight accepts complete seed-created tables. Error: {plan.Error?.Message}");
        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeTrue($"the pending migrations adopt the seeded tables. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().Contain(
        [
            ServerCoreSchemaMigrations.Manifest.RasterOverviewsMigration,
            ServerCoreSchemaMigrations.Manifest.RasterExternalStorageMigration,
            ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration,
        ]);
        var verify = () => guard.VerifyAsync(connectionString);
        await verify.Should().NotThrowAsync("the complete floor holds after adoption");
        (await CountTablesAsync(
                connectionString,
                "honua",
                "metadata_v2_snapshots",
                "metadata_v2_current",
                "metadata_v2_resources_idx",
                "metadata_v2_services_idx",
                "metadata_v2_publications_idx",
                "metadata_v2_storage_bindings_idx",
                "metadata_v2_connections_idx"))
            .Should().Be(7, "migration 031 creates the five tables the seed did not");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT etag FROM honua.metadata_v2_current WHERE environment = 'seed-4900'),
                (SELECT a.attstorage::text FROM pg_catalog.pg_attribute a
                 WHERE a.attrelid = 'honua.raster_data'::regclass AND a.attname = 'raster')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("seed-etag", "adoption keeps the seeded rows");
        reader.GetString(1).Should().Be("e", "migration 055 applies its storage policy to the adopted table");
    }

    [SkippableFact]
    public async Task CanonicalRunner_WhenAdoptionIsOptedIn_RejectsAnIncompleteSeededTableWithoutMutation()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the seeded-schema lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        await ExecuteAsync(connectionString, """
            CREATE SCHEMA honua;
            CREATE TABLE honua.metadata_v2_snapshots (environment text NOT NULL, revision bigint NOT NULL);
            """);
        var before = await CaptureUnmigratedStateAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:AdoptSeededSchemaOnFirstMigration"] = "true" })
            .Build();
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration),
            ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);

        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeFalse("CREATE TABLE IF NOT EXISTS cannot complete a table that already exists");
        var failure = result.Error.Should().BeOfType<DatabaseSchemaFloorException>().Which;
        failure.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        failure.MigrationScript.Should().Be(ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration);
        failure.Detail.Should().Contain("adoption candidate(s) are incomplete")
            .And.Contain("column metadata_v2_snapshots.schema_version")
            .And.Contain("column metadata_v2_snapshots.document");
        (await CaptureUnmigratedStateAsync(connectionString)).Should().Be(before,
            "an incomplete candidate is rejected before any migration runs");
    }

    [SkippableFact]
    public async Task CanonicalRunner_AdoptionOptIn_DoesNotApplyOnceAnyMigrationIsJournaled()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the seeded-schema lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:AdoptSeededSchemaOnFirstMigration"] = "true" })
            .Build();
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration),
            ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var baseline = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        baseline.Successful.Should().BeTrue($"the test requires a canonical schema baseline. Error: {baseline.ErrorMessage}");
        await ExecuteAsync(connectionString, $"""
            DELETE FROM public.schema_versions
            WHERE scriptname = '{ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration}';
            """);

        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeFalse("on a journaled database a missing journal row is divergence, not a seed");
        var failure = result.Error.Should().BeOfType<DatabaseSchemaFloorException>().Which;
        failure.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        failure.MigrationScript.Should().Be(ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration);
        failure.Detail.Should().NotContain("AdoptSeededSchemaOnFirstMigration");
    }

    private static async Task<string> CaptureUnmigratedStateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jsonb_build_object(
                'journal', to_regclass('public.schema_versions') IS NOT NULL,
                'tables', COALESCE((
                    SELECT jsonb_agg(n.nspname || '.' || c.relname ORDER BY n.nspname, c.relname)
                    FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname IN ('public', 'honua') AND c.relkind IN ('r', 'p', 'S')
                ), '[]'::jsonb)
            )::text;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [SkippableFact]
    public async Task CanonicalRunner_WhenMetadataSchemaIsConfigured_AppliesAndVerifiesGuardedFloorThere()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the configured-schema lane.");

        const string schema = "honua_guard_custom";
        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Schema"] = schema })
            .Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(
            guard,
            ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);

        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeTrue(
            $"the canonical migration roots and guard must use the configured schema. Error: {result.ErrorMessage}");
        var verify = () => guard.VerifyAsync(connectionString);
        await verify.Should().NotThrowAsync();

        var observationStore = new PostgresObservationStore(
            new TestConnectionProvider(connectionString),
            guard,
            schema);
        var things = await observationStore.ListThingsAsync(CatalogQuery.Page(0, 10), CancellationToken.None);
        things.Should().ContainSingle(thing => thing.Id == 1,
            "runtime SensorThings SQL must read from the same configured schema the guard verified");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)::int
            FROM information_schema.tables
            WHERE table_schema = @schema
              AND table_name IN (
                  'raster_data',
                  'raster_tiles',
                  'raster_layer_statistics',
                  'metadata_v2_snapshots',
                  'metadata_v2_release_packages',
                  'sta_thing')
            """;
        command.Parameters.AddWithValue("schema", schema);
        (await command.ExecuteScalarAsync()).Should().Be(6);

        (await CountTablesAsync(
                connectionString,
                "honua",
                "feature_change_outbox",
                "feature_changes",
                "alert_events"))
            .Should().Be(3, "migration 110 owns governed lineage in the canonical honua schema");
    }

    [SkippableFact]
    public async Task CanonicalRunner_WithoutOptionalRasterExtension_DoesNotInstallOrJournalRasterRoot()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the optional-raster lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);

        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeTrue(
            $"a vector-only PostgreSQL deployment must not require postgis_raster. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().NotContain(PostgresCoreSchemaGuard.RasterTablesMigration);
        await guard.Awaiting(instance => instance.VerifyAsync(connectionString)).Should().NotThrowAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_extension WHERE extname = 'postgis_raster'),
                   EXISTS (
                    SELECT 1 FROM public.schema_versions
                    WHERE scriptname LIKE 'Honua.Postgres.Migrations.%'),
                   to_regclass('honua.raster_data') IS NOT NULL;
            """;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetBoolean(0).Should().BeFalse(
                "the application migration root must preserve postgis_raster as an infrastructure choice");
            reader.GetBoolean(1).Should().BeFalse("the skipped provider root must not be journaled");
            reader.GetBoolean(2).Should().BeFalse("the skipped provider root must not create raster tables");
        }

        var rasterRequirement = async () =>
            await guard.VerifyRequirementAsync(connection, DatabaseSchemaRequirement.RasterExternalStorage);
        await rasterRequirement.Should().ThrowAsync<DatabaseSchemaFloorException>(
            "full startup readiness may omit raster, but a raster operation must still fail closed");
    }

    [SkippableFact]
    public async Task CanonicalRunner_WhenRasterIsEnabledAfterVectorOnlyBoot_CompletesJournaledNoOpTables()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the late-raster lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);

        var vectorBaseline = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        vectorBaseline.Successful.Should().BeTrue(
            $"the vector-only baseline must migrate cleanly. Error: {vectorBaseline.ErrorMessage}");
        vectorBaseline.AppliedScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.RasterOverviewsMigration);
        vectorBaseline.AppliedScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.RasterFootprintsMigration);
        vectorBaseline.AppliedScripts.Should().NotContain(PostgresCoreSchemaGuard.RasterLateProvisioningMigration);
        (await CountTablesAsync(connectionString, "honua", "raster_overviews", "raster_footprints"))
            .Should().Be(0, "server migrations 063/064 are intentional no-ops without postgis_raster");

        await ExecuteAsync(connectionString, "CREATE EXTENSION postgis_raster;");

        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(
            $"the provider root must be plannable over journaled server no-ops. Error: {plan.ErrorMessage}");
        plan.PendingScripts.Should().Contain(PostgresCoreSchemaGuard.RasterLateProvisioningMigration);
        plan.HasContractScripts.Should().BeFalse();

        var rasterUpgrade = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        rasterUpgrade.Successful.Should().BeTrue(
            $"late raster enablement must provision every guarded table. Error: {rasterUpgrade.ErrorMessage}");
        rasterUpgrade.AppliedScripts.Should().Contain(PostgresCoreSchemaGuard.RasterLateProvisioningMigration);
        (await CountTablesAsync(connectionString, "honua", "raster_overviews", "raster_footprints"))
            .Should().Be(2);
        await guard.Awaiting(instance => instance.VerifyAsync(connectionString)).Should().NotThrowAsync();

        await ExecuteAsync(connectionString, $"""
            DELETE FROM public.schema_versions
            WHERE scriptname = '{PostgresCoreSchemaGuard.RasterLateProvisioningMigration}';
            """);
        var beforeDivergenceCheck = await CaptureStateAsync(connectionString);
        var verifyDivergence = () => guard.VerifyAsync(connectionString);
        var exception = await verifyDivergence.Should().ThrowAsync<DatabaseSchemaFloorException>();
        exception.Which.MigrationScript.Should().Be(PostgresCoreSchemaGuard.RasterLateProvisioningMigration);
        exception.Which.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        (await CaptureStateAsync(connectionString)).Should().Be(beforeDivergenceCheck,
            "late-raster verification must never recreate a journal receipt");
    }

    [SkippableFact]
    public async Task CanonicalRunner_OnLegacyConfiguredSchema_AdoptsJournaledGuardedFamiliesForward()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the configured-schema upgrade lane.");

        const string schema = "honua_guard_adopted";
        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var baselineGuard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var baselineRunner = new PostgresDatabaseMigrationRunner(
            baselineGuard,
            ServerCoreSchemaMigrations.Manifest);
        var baseline = await baselineRunner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        baseline.Successful.Should().BeTrue(
            $"the test requires a canonical legacy baseline. Error: {baseline.ErrorMessage}");

        var legacyPackageId = Guid.Parse("72c1fbc0-e898-4e04-99d5-a16534e8f714");
        await ExecuteAsync(connectionString, $"""
            INSERT INTO honua.metadata_v2_release_packages (
                package_id,
                package_key,
                package_namespace,
                status,
                source_environment,
                source_revision,
                source_etag,
                target_environments,
                entries,
                package_metadata,
                created_by)
            VALUES (
                '{legacyPackageId}'::uuid,
                'legacy-package',
                'ops',
                'ready',
                'legacy',
                42,
                'etag-42',
                jsonb_build_array('production'),
                '[]'::jsonb,
                jsonb_build_object('id', '{legacyPackageId}', 'name', 'legacy-package', 'namespace', 'ops'),
                'upgrade-regression');
            """);

        // Reproduce a pre-#3899 deployment: server migrations 031/059 were journaled in
        // public while their guarded tables landed in honua, and the provider migration
        // root plus the forward adoption migration were not yet part of that journal.
        await ExecuteAsync(connectionString, $"""
            DELETE FROM public.schema_versions
            WHERE scriptname LIKE 'Honua.Postgres.Migrations.%'
               OR scriptname = '{ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration}';
            """);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Schema"] = schema })
            .Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(
            guard,
            ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);

        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(
            $"the one forward adoption migration must be reachable before the configured-schema floor is enforced. Error: {plan.ErrorMessage}");
        plan.PendingScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration);
        plan.HasContractScripts.Should().BeTrue("moving guarded tables is a contract-phase operation");
        plan.ContractScriptNames.Should().Contain(ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration);

        var beforeBlockedRun = await CaptureStateAsync(connectionString);
        var blockedResult = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        blockedResult.Successful.Should().BeFalse(
            "schema adoption must not run while older nodes may still target the legacy schema");
        blockedResult.ErrorMessage.Should().Contain(MigrationSafetyOptions.ApproveContractMigrationsKey);
        (await CaptureStateAsync(connectionString)).Should().Be(beforeBlockedRun,
            "the upgrade gate must block before moving tables or advancing either migration root");

        var approvalNonce = MigrationSafetyClassifier.ComputeContractApprovalNonce(
            [ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration]);
        var approvedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Schema"] = schema,
                [MigrationSafetyOptions.ApproveContractMigrationsKey] = approvalNonce,
            })
            .Build();
        var approvedGuard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, approvedConfiguration);
        var approvedRunner = new PostgresDatabaseMigrationRunner(
            approvedGuard,
            ServerCoreSchemaMigrations.Manifest,
            configuration: approvedConfiguration);

        var result = await approvedRunner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeTrue(
            $"the journaled forward migration must adopt complete legacy families. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration);
        await approvedGuard.Awaiting(instance => instance.VerifyAsync(connectionString)).Should().NotThrowAsync();

        var observationStore = new PostgresObservationStore(
            new TestConnectionProvider(connectionString),
            approvedGuard,
            schema);
        (await observationStore.ListThingsAsync(CatalogQuery.Page(0, 10), CancellationToken.None))
            .Should().ContainSingle(thing => thing.Id == 1,
                "the adopted SensorThings rows must remain available through configured-schema runtime SQL");

        var releasePackageStore = new PostgresMetadataReleasePackageStore(
            new TestConnectionProvider(connectionString),
            approvedGuard,
            schema);
        var adoptedPackage = await releasePackageStore.GetAsync(legacyPackageId);
        adoptedPackage.Should().NotBeNull();
        adoptedPackage!.Metadata.Name.Should().Be("legacy-package",
            "migration 034 package rows must move with their journaled table");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (
                    SELECT COUNT(*)
                    FROM information_schema.tables
                    WHERE table_schema = @schema
                      AND table_name IN (
                          'raster_data',
                          'raster_tiles',
                          'raster_layer_statistics',
                          'metadata_v2_snapshots',
                          'metadata_v2_release_packages',
                          'sta_thing'))::int,
                   (
                    SELECT COUNT(*)
                    FROM information_schema.tables
                    WHERE table_schema = 'honua'
                      AND table_name IN (
                          'raster_data',
                          'raster_tiles',
                          'raster_layer_statistics',
                          'metadata_v2_snapshots',
                          'metadata_v2_release_packages',
                          'sta_thing'))::int,
                   EXISTS (
                    SELECT 1 FROM public.schema_versions WHERE scriptname = @adoption);
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("adoption", ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(6);
        reader.GetInt32(1).Should().Be(0);
        reader.GetBoolean(2).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Guard_WhenRasterBaselineJournalEntryIsMissing_FullVerificationFailsWithoutMutation()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the schema-divergence lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        var migrationResult = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        migrationResult.Successful.Should().BeTrue(
            $"the test requires a canonical journal/schema baseline. Error: {migrationResult.ErrorMessage}");

        await ExecuteAsync(connectionString, $"""
            DELETE FROM public.schema_versions
            WHERE scriptname = '{PostgresCoreSchemaGuard.RasterTablesMigration}';
            """);
        var before = await CaptureStateAsync(connectionString);

        var act = () => guard.VerifyAsync(connectionString);
        var exception = await act.Should().ThrowAsync<DatabaseSchemaFloorException>();
        exception.Which.MigrationScript.Should().Be(PostgresCoreSchemaGuard.RasterTablesMigration);
        exception.Which.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);

        (await CaptureStateAsync(connectionString)).Should().Be(before,
            "startup/DR verification must not repair a provider baseline journal gap");
    }

    [SkippableFact]
    public async Task Guard_WhenJournaledMetadataReleasePackagesTableIsMissing_FullVerificationFailsWithoutMutation()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the schema-divergence lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        var migrationResult = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        migrationResult.Successful.Should().BeTrue(
            $"the test requires a canonical journal/schema baseline. Error: {migrationResult.ErrorMessage}");

        await ExecuteAsync(connectionString, "DROP TABLE honua.metadata_v2_release_packages;");
        var before = await CaptureStateAsync(connectionString);

        var act = () => guard.VerifyAsync(connectionString);
        var exception = await act.Should().ThrowAsync<DatabaseSchemaFloorException>();
        exception.Which.MigrationScript.Should().Be(
            ServerCoreSchemaMigrations.Manifest.MetadataV2ReleasePackagesMigration);
        exception.Which.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema);
        exception.Which.Detail.Should().Contain("metadata_v2_release_packages");

        (await CaptureStateAsync(connectionString)).Should().Be(before,
            "startup/DR verification must not repair a missing release-package table");
    }

    [SkippableFact]
    public async Task CanonicalRunner_OnPartialConfiguredAdoptionTarget_FailsClosedWithoutMutation()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the configured-schema divergence lane.");

        const string schema = "honua_guard_partial";
        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var baselineRunner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);
        var baseline = await baselineRunner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        baseline.Successful.Should().BeTrue();
        await ExecuteAsync(connectionString, $"""
            DELETE FROM public.schema_versions
            WHERE scriptname = '{ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration}';
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.sta_thing (id bigint PRIMARY KEY, name text, description text);
            """);
        var before = await CaptureStateAsync(connectionString, schema);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Schema"] = schema })
            .Build();
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration),
            ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);

        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);

        plan.Successful.Should().BeFalse("a partial adoption target is ambiguous and must require operator repair");
        plan.Error.Should().BeOfType<DatabaseSchemaFloorException>();
        (await CaptureStateAsync(connectionString, schema)).Should().Be(before,
            "adoption preflight must neither move tables nor advance the journal when target state is partial");
    }

    [SkippableTheory]
    [InlineData("raster_data")]
    [InlineData("raster_statistics")]
    public async Task Guard_WhenJournaledRasterBaselineTableIsMissing_FullVerificationFailsWithoutMutation(
        string missingTable)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the schema-divergence lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        var migrationResult = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        migrationResult.Successful.Should().BeTrue(
            $"the test requires a canonical journal/schema baseline. Error: {migrationResult.ErrorMessage}");

        await ExecuteAsync(connectionString, $"DROP TABLE honua.{missingTable} CASCADE;");
        var before = await CaptureStateAsync(connectionString);

        var act = () => guard.VerifyAsync(connectionString);
        var exception = await act.Should().ThrowAsync<DatabaseSchemaFloorException>();
        exception.Which.MigrationScript.Should().Be(PostgresCoreSchemaGuard.RasterTablesMigration);
        exception.Which.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema);
        exception.Which.Detail.Should().Contain($"honua.{missingTable}");

        var after = await CaptureStateAsync(connectionString);
        after.Should().Be(before, "full startup/DR verification must be read-only when a restored table is missing");
    }

    [SkippableFact]
    public async Task CanonicalRunner_WhenSeedCreatesRasterTablesAfterVectorOnlyBoot_AdoptsThemOnRestartAndServesStatistics()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the late-raster seed lane.");

        // honua-esri-compat scripts/up.sh + scripts/seed.sh (#4744): Honua boots and migrates
        // before postgis_raster exists, the seed then enables the extension and creates the
        // raster tables itself, and the harness restarts Honua.
        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);

        var firstBoot = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        firstBoot.Successful.Should().BeTrue(
            $"the vector-only first boot must migrate cleanly. Error: {firstBoot.ErrorMessage}");
        firstBoot.AppliedScripts.Should().NotContain(PostgresCoreSchemaGuard.RasterLayerStatisticsMigration);

        await ExecuteAsync(
            connectionString,
            await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "base-schema.sql")));
        (await CountTablesAsync(connectionString, "honua", "raster_data", "raster_layer_statistics"))
            .Should().Be(2, "the seed creates the raster tables directly, without journal rows");

        // A 3x2 8BUI band whose zero cell is nodata. Expected statistics are derived from the
        // literal pixel values below, not from anything the server computes.
        int[][] pixels = [[1, 2, 3], [4, 0, 6]];
        const int noData = 0;
        var probeRasterId = await InsertProbeRasterAsync(connectionString, pixels, noData);
        var valid = pixels.SelectMany(row => row).Where(value => value != noData).ToArray();
        var expectedMean = valid.Average();
        var expectedStdDev = Math.Sqrt(valid.Sum(value => (value - expectedMean) * (value - expectedMean)) / valid.Length);

        var rasterStore = new PostgresRasterStore(
            new TestConnectionProvider(connectionString),
            NullLogger<PostgresRasterStore>.Instance,
            guard,
            schemaName: "honua");

        // Until migrations run again the request path still fails closed, and says what heals it.
        var beforeRestart = () => rasterStore.GetMosaicStatisticsAsync(0, [probeRasterId], RasterMergeStrategy.Newest);
        var floor = await beforeRestart.Should().ThrowAsync<DatabaseSchemaFloorException>();
        floor.Which.MigrationScript.Should().Be(PostgresCoreSchemaGuard.RasterLayerStatisticsMigration);
        floor.Which.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        floor.Which.Detail.Should().Contain("restart the server");

        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(
            $"a complete seed-created statistics table must be adoptable by the pending provider root. Error: {plan.ErrorMessage}");
        plan.PendingScripts.Should().Contain(new[]
        {
            PostgresCoreSchemaGuard.RasterTablesMigration,
            PostgresCoreSchemaGuard.RasterLayerStatisticsMigration,
            PostgresCoreSchemaGuard.RasterLateProvisioningMigration,
        });
        plan.HasContractScripts.Should().BeFalse();

        var restart = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        restart.Successful.Should().BeTrue(
            $"the restart after late raster enablement must journal the provider root. Error: {restart.ErrorMessage}");
        restart.AppliedScripts.Should().Contain(PostgresCoreSchemaGuard.RasterLayerStatisticsMigration);
        await guard.Awaiting(instance => instance.VerifyAsync(connectionString)).Should().NotThrowAsync();

        var statistics = await rasterStore.GetMosaicStatisticsAsync(0, [probeRasterId], RasterMergeStrategy.Newest);
        var band = statistics.Should().ContainSingle().Which;
        band.Band.Should().Be(1);
        band.MinValue.Should().Be((double)valid.Min());
        band.MaxValue.Should().Be((double)valid.Max());
        band.MeanValue.Should().BeApproximately(expectedMean, 1e-9);
        band.StandardDeviation.Should().BeApproximately(expectedStdDev, 1e-9);
        band.ValidPixelCount.Should().Be(valid.Length, "the nodata cell is excluded from the statistics");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT min_value, max_value, mean_value, std_dev, valid_pixel_count
            FROM honua.raster_layer_statistics
            WHERE layer_id = 0 AND merge_strategy = 'Newest' AND band_number = 1
            """;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue("the adopted table must persist the computed mosaic statistics");
            reader.GetDouble(0).Should().Be((double)valid.Min());
            reader.GetDouble(1).Should().Be((double)valid.Max());
            reader.GetDouble(2).Should().BeApproximately(expectedMean, 1e-9);
            reader.GetDouble(3).Should().BeApproximately(expectedStdDev, 1e-9);
            reader.GetInt64(4).Should().Be(valid.Length);
            (await reader.ReadAsync()).Should().BeFalse();
        }
    }

    [SkippableTheory]
    [InlineData(LateRasterStatisticsCase.IncompleteTableWithRasterExtension)]
    [InlineData(LateRasterStatisticsCase.CompleteTableWithoutRasterExtension)]
    [InlineData(LateRasterStatisticsCase.ProviderRootJournaledWithoutStatisticsRow)]
    public async Task CanonicalRunner_WhenUnjournaledRasterStatisticsTableIsNotAdoptable_FailsClosedWithoutMutation(
        LateRasterStatisticsCase scenario)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the late-raster seed lane.");

        const string completeTable = """
            CREATE TABLE honua.raster_layer_statistics (
                layer_id INTEGER NOT NULL,
                merge_strategy VARCHAR(32) NOT NULL,
                raster_signature TEXT NOT NULL,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT,
                computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (layer_id, merge_strategy, raster_signature, band_number));
            """;

        var rasterAtBoot = scenario == LateRasterStatisticsCase.ProviderRootJournaledWithoutStatisticsRow;
        var connectionString = await postgres.CreateFreshDatabaseAsync(
            enablePostGis: true,
            enablePostGisRaster: rasterAtBoot);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        var boot = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        boot.Successful.Should().BeTrue($"the test requires a clean first boot. Error: {boot.ErrorMessage}");

        switch (scenario)
        {
            case LateRasterStatisticsCase.IncompleteTableWithRasterExtension:
                await ExecuteAsync(connectionString, $"""
                    CREATE EXTENSION postgis_raster;
                    {completeTable}
                    ALTER TABLE honua.raster_layer_statistics DROP CONSTRAINT raster_layer_statistics_pkey;
                    """);
                break;
            case LateRasterStatisticsCase.CompleteTableWithoutRasterExtension:
                await ExecuteAsync(connectionString, completeTable);
                break;
            case LateRasterStatisticsCase.ProviderRootJournaledWithoutStatisticsRow:
                await ExecuteAsync(connectionString, $"""
                    DELETE FROM public.schema_versions
                    WHERE scriptname = '{PostgresCoreSchemaGuard.RasterLayerStatisticsMigration}';
                    """);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        (await CountTablesAsync(connectionString, "honua", "raster_layer_statistics")).Should().Be(1);
        var before = await CaptureStateAsync(connectionString);

        var plan = await runner.PlanMigrationsAsync(connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeFalse("only a complete table awaiting the provider root is adoptable");
        var planError = plan.Error.Should().BeOfType<DatabaseSchemaFloorException>().Which;
        planError.MigrationScript.Should().Be(PostgresCoreSchemaGuard.RasterLayerStatisticsMigration);
        planError.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        if (scenario == LateRasterStatisticsCase.IncompleteTableWithRasterExtension)
        {
            planError.Detail.Should().Contain("index raster_layer_statistics_pkey");
        }

        var restart = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        restart.Successful.Should().BeFalse();
        restart.Error.Should().BeOfType<DatabaseSchemaFloorException>();

        (await CaptureStateAsync(connectionString)).Should().Be(before,
            "a rejected adoption must neither create tables nor advance the journal");
    }

    public enum LateRasterStatisticsCase
    {
        IncompleteTableWithRasterExtension,
        CompleteTableWithoutRasterExtension,
        ProviderRootJournaledWithoutStatisticsRow,
    }

    private static async Task<long> InsertProbeRasterAsync(string connectionString, int[][] pixels, int noData)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.raster_data (layer_id, name, description, raster)
            VALUES (
                0,
                'late-raster-adoption-probe',
                'honua-server#4744 receipt',
                ST_SetValues(
                    ST_AddBand(
                        ST_MakeEmptyRaster(@width, @height, -122.5, 37.84, 0.001, -0.001, 0, 0, 4326),
                        '8BUI'::text,
                        @nodata,
                        @nodata),
                    1, 1, 1,
                    @pixels))
            RETURNING id
            """;
        command.Parameters.AddWithValue("width", pixels[0].Length);
        command.Parameters.AddWithValue("height", pixels.Length);
        command.Parameters.AddWithValue("nodata", (double)noData);
        var grid = new double[pixels.Length, pixels[0].Length];
        for (var row = 0; row < pixels.Length; row++)
        {
            for (var column = 0; column < pixels[row].Length; column++)
            {
                grid[row, column] = pixels[row][column];
            }
        }

        command.Parameters.AddWithValue("pixels", grid);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountTablesAsync(
        string connectionString,
        string schema,
        params string[] tables)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)::int
            FROM information_schema.tables
            WHERE table_schema = @schema
              AND table_name = ANY(@tables)
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("tables", tables);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteOrdinaryStoreOperationAsync(
        string connectionString,
        PostgresCoreSchemaGuard guard,
        StoreOperation operation)
    {
        var provider = new TestConnectionProvider(connectionString);
        switch (operation)
        {
            case StoreOperation.RasterStatisticsRead:
                var rasterStore = new PostgresRasterStore(
                    provider,
                    NullLogger<PostgresRasterStore>.Instance,
                    guard,
                    schemaName: "honua");
                await rasterStore.GetMosaicStatisticsAsync(1, [1], RasterMergeStrategy.Newest);
                return;

            case StoreOperation.MetadataRead:
                var metadataStore = new PostgresMetadataV2GraphStore(
                    provider,
                    environment: "dr-restore",
                    schemaName: "honua",
                    schemaGuard: guard);
                await metadataStore.GetCurrentAsync();
                return;

            case StoreOperation.RasterImportWrite:
                var filePath = Path.Join(Path.GetTempPath(), $"honua-schema-floor-{Guid.NewGuid():N}.png");
                try
                {
                    await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
                    var importService = new PostgresRasterImportService(
                            provider,
                            new StubCrsDetectionService(),
                            NullLogger<PostgresRasterImportService>.Instance,
                            guard,
                            schemaName: "honua");
                    await importService.ImportAsync(new RasterImportRequest
                    {
                        LayerId = 1,
                        Name = "schema-floor-probe",
                        FilePath = filePath,
                        FileName = Path.GetFileName(filePath),
                        Format = SupportedRasterFormat.PngWorldFile,
                        Srid = 4326,
                        WorldFileContent = "1\n0\n0\n-1\n0.5\n0.5",
                        TileZoomLevels = [],
                        OverviewFactors = [],
                    });
                }
                finally
                {
                    File.Delete(filePath);
                }

                return;

            case StoreOperation.SensorThingsRead:
                var observationStore = new PostgresObservationStore(provider, guard);
                await observationStore.ListThingsAsync(CatalogQuery.Page(0, 1), CancellationToken.None);
                return;

            case StoreOperation.MetadataReleasePackageRead:
                var releasePackageStore = new PostgresMetadataReleasePackageStore(provider, guard);
                await releasePackageStore.GetAsync(Guid.Empty, CancellationToken.None);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static async Task<string> CaptureStateAsync(string connectionString, string schemaName = "honua")
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jsonb_build_object(
                'schema_exists', to_regnamespace(@schema) IS NOT NULL,
                'tables', COALESCE((
                    SELECT jsonb_agg(c.relname ORDER BY c.relname)
                    FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @schema AND c.relkind IN ('r', 'p')
                ), '[]'::jsonb),
                'journal', COALESCE((
                    SELECT jsonb_agg(scriptname ORDER BY scriptname)
                    FROM public.schema_versions
                ), '[]'::jsonb),
                'storage', COALESCE((
                    SELECT jsonb_agg(c.relname || '.' || a.attname || '=' || a.attstorage::text ORDER BY c.relname, a.attname)
                    FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
                    WHERE n.nspname = @schema
                      AND a.attnum > 0
                      AND NOT a.attisdropped
                ), '[]'::jsonb)
            )::text;
            """;
        command.Parameters.AddWithValue("schema", schemaName);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private enum StoreOperation
    {
        RasterStatisticsRead,
        MetadataRead,
        RasterImportWrite,
        SensorThingsRead,
        MetadataReleasePackageRead,
    }

    private sealed class TestConnectionProvider(string connectionString) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => connectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
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

    private sealed class StubCrsDetectionService : ICrsDetectionService
    {
        public Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public int? DetectFromEpsgCode(string epsgCode) => null;

        public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
