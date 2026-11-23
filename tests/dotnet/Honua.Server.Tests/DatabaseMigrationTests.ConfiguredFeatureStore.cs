// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests;

public sealed partial class DatabaseMigrationTests
{
    private const string FeatureStoreAdoptionMigration =
        "Honua.Server.Migrations.231_AdoptConfiguredFeatureStoreSchema.sql";

    // Every features table carrying the shared layer_id discriminator, as schema.table.
    private const string SharedFeatureTablesSql = """
        SELECT coalesce(string_agg(format('%s.%s', n.nspname, c.relname), ',' ORDER BY n.nspname), '')
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relname = 'features' AND c.relkind = 'r'
          AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
                      WHERE a.attrelid = c.oid AND a.attname = 'layer_id' AND NOT a.attisdropped);
        """;

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_OnFreshConfiguredSchema_CreatesFeatureStoreInConfiguredSchema()
    {
        var configuration = ConfiguredRegistryConfiguration();
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration),
            ServerCoreSchemaMigrations.Manifest, configuration: configuration);

        var result = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeTrue(result.ErrorMessage);
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        (await ScalarAsync(connection, SharedFeatureTablesSql)).Should().Be("custom_registry.features",
            "the publish check, managed writer and attachment store all address the configured schema");
        var restart = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        restart.Successful.Should().BeTrue(restart.ErrorMessage);
        restart.AppliedScripts.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_WithPublicAsConfiguredVectorSchema_KeepsFeatureStoreInPlace()
    {
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        // The PostGIS raster_overviews view shares its name with the Honua raster table in public.
        await ScalarAsync(connection, "DROP EXTENSION IF EXISTS postgis_raster CASCADE;");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Schema"] = "public" }).Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);

        var result = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeTrue(result.Error?.ToString() ?? result.ErrorMessage);
        result.AppliedScripts.Should().Contain(FeatureStoreAdoptionMigration);
        (await ScalarAsync(connection, SharedFeatureTablesSql)).Should().Be("public.features",
            "migration 001 already created the table in the configured schema");
        await guard.VerifyAsync(_connectionString);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_OnPopulatedDefaultFeatureStore_AdoptsConfiguredSchemaWithoutCopyingRows()
    {
        var baseline = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest), ServerCoreSchemaMigrations.Manifest);
        var installed = await baseline.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        installed.Successful.Should().BeTrue(installed.ErrorMessage);
        installed.AppliedScripts.Should().NotContain(FeatureStoreAdoptionMigration);
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var legacyTable = (string)(await ScalarAsync(connection, SharedFeatureTablesSql))!;
        var otherSchema = legacyTable == "public.features" ? "honua" : "public";
        await ScalarAsync(connection,
            $"CREATE VIEW {otherSchema}.features AS SELECT 1 AS layer_id, '{{{{}}}}'::jsonb AS attributes;");
        var originalOid = await SeedLegacyManagedLayersAsync(connection, legacyTable);

        var configuration = ConfiguredRegistryConfiguration();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var plan = await new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration).PlanMigrationsAsync(_connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(plan.ErrorMessage);
        plan.ContractScriptNames.Should().Contain(FeatureStoreAdoptionMigration);
        configuration[MigrationSafetyOptions.ApproveContractMigrationsKey] = plan.ContractApprovalNonce;
        var upgrade = await new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration).RunMigrationsAsync(_connectionString, typeof(Program).Assembly);

        upgrade.Successful.Should().BeTrue(upgrade.Error?.ToString() ?? upgrade.ErrorMessage);
        (await ScalarAsync(connection, SharedFeatureTablesSql)).Should().Be("custom_registry.features");
        (await ScalarAsync(connection, $"SELECT layer_id FROM {otherSchema}.features;"))
            .Should().Be(1, "the unrelated view stays in its original schema");
        (await ScalarAsync(connection, "SELECT 'custom_registry.features'::regclass::oid::bigint;"))
            .Should().Be(originalOid, "the table moves; its rows are not copied");
        (await ScalarAsync(connection,
            "INSERT INTO custom_registry.features(layer_id) VALUES (7) RETURNING objectid;"))
            .Should().Be(2L, "the owned objectid sequence moves with the table");
        (await ScalarAsync(connection, "SELECT table_schema FROM custom_registry.layers WHERE layer_name = 'moved';"))
            .Should().Be("custom_registry", "managed registry rows follow the moved table");
        (await ScalarAsync(connection, "SELECT table_schema FROM custom_registry.layers WHERE layer_name = 'source';"))
            .Should().Be(legacyTable.Split('.')[0], "a source-backed row names its own source table");
        await guard.VerifyAsync(_connectionString);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SchemaGuard_WithUnadoptedConfiguredFeatureStore_RejectsServingVerification()
    {
        var configuration = ConfiguredRegistryConfiguration();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var installed = await new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration).RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        installed.Successful.Should().BeTrue(installed.ErrorMessage);
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await ScalarAsync(connection, "ALTER TABLE custom_registry.features SET SCHEMA public;");
        var verify = () => guard.VerifyAsync(_connectionString);
        await verify.Should().ThrowAsync<DatabaseSchemaFloorException>(
            "the journal claims 231 while the configured schema has no features table")
            .WithMessage("*231_AdoptConfiguredFeatureStoreSchema*");

        await ScalarAsync(connection, $"DELETE FROM public.schema_versions WHERE scriptname = '{FeatureStoreAdoptionMigration}';");
        await verify.Should().ThrowAsync<DatabaseSchemaFloorException>(
            "a node started with migrations skipped must not serve before the table is adopted")
            .WithMessage("*231_AdoptConfiguredFeatureStoreSchema*");
    }

    [IntegrationTheory]
    [InlineData("bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY", "text")]
    [InlineData("bigint PRIMARY KEY", "geometry")]
    [InlineData("bigint GENERATED BY DEFAULT AS IDENTITY", "geometry")]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_AndSchemaGuard_RejectIncompatibleConfiguredFeatureStore(
        string objectIdDefinition,
        string geometryType)
    {
        var configuration = ConfiguredRegistryConfiguration();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var installed = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        installed.Successful.Should().BeTrue(installed.ErrorMessage);
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await ReplaceManagedFeatureTableAsync(connection, objectIdDefinition, geometryType);

        var verify = () => guard.VerifyAsync(_connectionString);
        await verify.Should().ThrowAsync<DatabaseSchemaFloorException>()
            .WithMessage("*231_AdoptConfiguredFeatureStoreSchema*incompatible*");

        await ScalarAsync(connection, $"DELETE FROM public.schema_versions WHERE scriptname = '{FeatureStoreAdoptionMigration}';");
        var plan = await runner.PlanMigrationsAsync(_connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(plan.ErrorMessage);
        plan.ContractScriptNames.Should().Contain(FeatureStoreAdoptionMigration);
        configuration[MigrationSafetyOptions.ApproveContractMigrationsKey] = plan.ContractApprovalNonce;
        var rerun = await new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration).RunMigrationsAsync(_connectionString, typeof(Program).Assembly);

        rerun.Successful.Should().BeFalse();
        rerun.Error.Should().NotBeNull();
        rerun.Error!.ToString().Should().Contain("Cannot adopt feature store schema")
            .And.Contain("incompatible with the managed feature store");
        (await ScalarAsync(connection,
            $"SELECT count(*) FROM public.schema_versions WHERE scriptname = '{FeatureStoreAdoptionMigration}';"))
            .Should().Be(0L);
    }

    private static Task<object?> SeedLegacyManagedLayersAsync(Npgsql.NpgsqlConnection connection, string legacyTable)
    {
        var legacySchema = legacyTable.Split('.')[0];
        return ScalarAsync(connection,
            $"INSERT INTO {legacyTable}(layer_id, attributes) VALUES (7, '{{}}'); " +
            "INSERT INTO honua.layers(layer_name, table_schema, table_name, geometry_type, storage_options) VALUES " +
            $"('moved', '{legacySchema}', 'features', 'Point', '{{\"managedStore\":\"true\"}}'), " +
            $"('source', '{legacySchema}', 'features', 'Point', '{{\"sourceBacked\":\"true\"}}'); " +
            $"SELECT '{legacyTable}'::regclass::oid::bigint;");
    }

    private static Task<object?> ReplaceManagedFeatureTableAsync(
        Npgsql.NpgsqlConnection connection,
        string objectIdDefinition,
        string geometryType)
        => ScalarAsync(connection, $"""
            ALTER TABLE custom_registry.features RENAME TO original_features;
            CREATE TABLE custom_registry.features (
                objectid {objectIdDefinition}, layer_id integer, geometry {geometryType}, attributes jsonb,
                created_at timestamptz, updated_at timestamptz
            );
            """);

    private static IConfigurationRoot ConfiguredRegistryConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Schema"] = "custom_registry" }).Build();

    private static async Task<object?> ScalarAsync(Npgsql.NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
