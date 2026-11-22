// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests;

public sealed partial class DatabaseMigrationTests
{
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_OnFreshConfiguredSchema_CreatesRegistryInConfiguredSchema()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Schema"] = "custom_registry" }).Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var result = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        result.Successful.Should().BeTrue(result.ErrorMessage);
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_catalog.pg_tables
            WHERE schemaname = 'custom_registry'
              AND tablename = ANY(ARRAY['services', 'layers', 'service_layers', 'layer_fields']);
            """;
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(4);
        await guard.VerifyAsync(_connectionString);
        var restart = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        restart.Successful.Should().BeTrue(restart.ErrorMessage);
        restart.AppliedScripts.Should().BeEmpty();
        command.CommandText = "ALTER TABLE custom_registry.layers SET SCHEMA honua;";
        await command.ExecuteNonQueryAsync();
        var verify = () => guard.VerifyAsync(_connectionString);
        await verify.Should().ThrowAsync<InvalidOperationException>(
            "a journaled adoption must require the registry in its configured schema");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_OnPopulatedRegistry_AdoptsConfiguredSchemaWithoutLosingIdentity()
    {
        var baseline = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest), ServerCoreSchemaMigrations.Manifest);
        var installed = await baseline.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        installed.Successful.Should().BeTrue(installed.ErrorMessage);
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var originalOid = await SeedLegacyRegistryAsync(connection);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Schema"] = "custom_registry" }).Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var plan = await runner.PlanMigrationsAsync(_connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(plan.ErrorMessage);
        configuration[MigrationSafetyOptions.ApproveContractMigrationsKey] = plan.ContractApprovalNonce;
        runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var upgrade = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        upgrade.Successful.Should().BeTrue(upgrade.Error?.ToString() ?? upgrade.ErrorMessage);
        await AssertAdoptedRegistryAsync(connection, originalOid);
        await guard.VerifyAsync(_connectionString);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_WithConflictingConfiguredRegistry_RejectsWithoutOverwritingTables()
    {
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA custom_registry;
            CREATE TABLE custom_registry.layers (sentinel integer);
            INSERT INTO custom_registry.layers VALUES (42);
            """;
        await command.ExecuteNonQueryAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Schema"] = "custom_registry" }).Build();
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration),
            ServerCoreSchemaMigrations.Manifest, configuration: configuration);
        var result = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        result.Successful.Should().BeFalse();
        command.CommandText = "SELECT sentinel FROM custom_registry.layers;";
        (await command.ExecuteScalarAsync()).Should().Be(42);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_WithUnadoptedRegistry_RejectsServingVerification()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Schema"] = "custom_registry" }).Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var installed = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        installed.Successful.Should().BeTrue(installed.ErrorMessage);
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE custom_registry.services SET SCHEMA honua;
            ALTER TABLE custom_registry.layers SET SCHEMA honua;
            ALTER TABLE custom_registry.service_layers SET SCHEMA honua;
            ALTER TABLE custom_registry.layer_fields SET SCHEMA honua;
            DELETE FROM public.schema_versions
            WHERE scriptname = 'Honua.Server.Migrations.227_AdoptConfiguredRegistrySchema.sql';
            """;
        await command.ExecuteNonQueryAsync();
        var verify = () => guard.VerifyAsync(_connectionString);
        await verify.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*227_AdoptConfiguredRegistrySchema*");
        var plan = await runner.PlanMigrationsAsync(_connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(plan.ErrorMessage);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CanonicalRunner_WithLateRasterExtension_ReferencesAdoptedRegistry()
    {
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP EXTENSION IF EXISTS postgis_raster CASCADE;";
        await command.ExecuteNonQueryAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Schema"] = "custom_registry" }).Build();
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var installed = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        installed.Successful.Should().BeTrue(installed.ErrorMessage);
        command.CommandText = "CREATE EXTENSION postgis_raster;";
        await command.ExecuteNonQueryAsync();
        var plan = await runner.PlanMigrationsAsync(_connectionString, typeof(Program).Assembly);
        plan.Successful.Should().BeTrue(plan.ErrorMessage);
        configuration[MigrationSafetyOptions.ApproveContractMigrationsKey] = plan.ContractApprovalNonce;
        runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);
        var upgrade = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        upgrade.Successful.Should().BeTrue(upgrade.Error?.ToString() ?? upgrade.ErrorMessage);
        command.CommandText = """
            SELECT confrelid = 'custom_registry.layers'::regclass
            FROM pg_constraint WHERE conname = 'raster_data_layer_id_fk';
            """;
        (await command.ExecuteScalarAsync()).Should().Be(true);
        await guard.VerifyAsync(_connectionString);
    }

    private static async Task<long> SeedLegacyRegistryAsync(Npgsql.NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.services(service_name) VALUES ('registry-test');
            INSERT INTO honua.layers(layer_name, table_name, geometry_type, primary_key_column)
            VALUES ('registry-test', 'features', 'Point', 'external_id');
            INSERT INTO honua.service_layers(service_name, layer_id, layer_order)
            SELECT 'registry-test', layer_id, 0 FROM honua.layers;
            INSERT INTO honua.layer_fields(layer_id, field_name, field_type, field_order)
            SELECT layer_id, 'external_id', 'Integer', 0 FROM honua.layers;
            SELECT 'honua.layers'::regclass::oid::bigint;
            """;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertAdoptedRegistryAsync(Npgsql.NpgsqlConnection connection, long originalOid)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 'custom_registry.layers'::regclass::oid::bigint;";
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(originalOid);
        command.CommandText = """
            SELECT honua.resolve_feature_public_objectid(layer_id, 1, '{"external_id":987}'::jsonb)
            FROM custom_registry.layers;
            """;
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(987);
        command.CommandText = """
            INSERT INTO custom_registry.layers(layer_name, table_name, geometry_type)
            VALUES ('next-layer', 'features', 'Point') RETURNING layer_id;
            """;
        ((int)(await command.ExecuteScalarAsync())!).Should().Be(2);
        command.CommandText = """
            DELETE FROM custom_registry.layers WHERE layer_name = 'registry-test';
            SELECT (SELECT count(*) FROM custom_registry.service_layers) +
                (SELECT count(*) FROM custom_registry.layer_fields);
            """;
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(0);
    }
}
