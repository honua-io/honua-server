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
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Db.Postgres.Features.Raster;
using Honua.Db.Postgres.Features.SensorThings;
using Honua.Server.Startup;
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
        var things = await observationStore.ListThingsAsync(0, 10, CancellationToken.None);
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
        (await observationStore.ListThingsAsync(0, 10, CancellationToken.None))
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

    [SkippableFact]
    public async Task Guard_WhenJournaledRasterDataTableIsMissing_FullVerificationFailsWithoutMutation()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is not available for the schema-divergence lane.");

        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGisRaster: true);
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        var migrationResult = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        migrationResult.Successful.Should().BeTrue(
            $"the test requires a canonical journal/schema baseline. Error: {migrationResult.ErrorMessage}");

        await ExecuteAsync(connectionString, "DROP TABLE honua.raster_data CASCADE;");
        var before = await CaptureStateAsync(connectionString);

        var act = () => guard.VerifyAsync(connectionString);
        var exception = await act.Should().ThrowAsync<DatabaseSchemaFloorException>();
        exception.Which.MigrationScript.Should().Be(PostgresCoreSchemaGuard.RasterTablesMigration);
        exception.Which.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema);
        exception.Which.Detail.Should().Contain("honua.raster_data");

        var after = await CaptureStateAsync(connectionString);
        after.Should().Be(before, "full startup/DR verification must be read-only when a restored table is missing");
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
                await observationStore.ListThingsAsync(0, 1, CancellationToken.None);
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
