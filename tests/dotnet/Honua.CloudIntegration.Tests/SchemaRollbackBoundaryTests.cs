// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Npgsql;
using Xunit;

namespace Honua.CloudIntegration.Tests;

[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
[Trait("Tier", "Integration")]
public sealed class SchemaRollbackBoundaryTests(LocalSubstratePostgresFixture postgres)
    : IClassFixture<LocalSubstratePostgresFixture>
{
    [SkippableTheory]
    [InlineData("DROP VIEW layer_summary;")]
    [InlineData("DROP TYPE layer_kind;")]
    public async Task MigrationRunner_DestructiveObjectDrop_RejectsBeforeChangingDatabase(string contraction)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for the contract DDL proof.");
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var name = $"contract_{Guid.NewGuid():N}";
        const string original = "CREATE VIEW layer_summary AS SELECT 7 AS id; CREATE TYPE layer_kind AS ENUM ('retained');";
        var older = SyntheticMigrationsCompiler.Compile(name, ("001.sql", original));
        var newer = SyntheticMigrationsCompiler.Compile(name, ("001.sql", original), ("002.sql", contraction));
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        (await runner.RunMigrationsAsync(connectionString, older)).Successful.Should().BeTrue();
        var rejected = await runner.RunMigrationsAsync(connectionString, newer);
        rejected.Successful.Should().BeFalse();
        rejected.ErrorMessage.Should().Contain("002.sql");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM layer_summary";
        (await command.ExecuteScalarAsync()).Should().Be(7);
        command.CommandText = "SELECT 'retained'::layer_kind::text";
        (await command.ExecuteScalarAsync()).Should().Be("retained");
        command.CommandText = "SELECT COUNT(*) FROM public.schema_versions";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }

    [SkippableFact]
    public async Task OlderReader_AfterNewerContractMigration_RefusesWithoutChangingData()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for the rollback boundary proof.");
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var name = $"rollback_{Guid.NewGuid():N}";
        const string original = "CREATE TABLE fixture (id integer PRIMARY KEY, legacy_name text); INSERT INTO fixture VALUES (7, 'retained');";
        var older = SyntheticMigrationsCompiler.Compile(name, ("001.sql", original));
        var newer = SyntheticMigrationsCompiler.Compile(name,
            ("001.sql", original),
            ("002.sql", "ALTER TABLE fixture RENAME COLUMN legacy_name TO display_name;"));
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        (await runner.RunMigrationsAsync(connectionString, older)).Successful.Should().BeTrue();

        // Simulate the separately approved contract phase of a newer revision. The old
        // reader must refuse even though its own migration set has nothing left to apply.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE fixture RENAME COLUMN legacy_name TO display_name; INSERT INTO public.schema_versions (scriptname, applied) VALUES (@name, now());";
        command.Parameters.AddWithValue("name", newer.GetManifestResourceNames().Single(value => value.EndsWith("002.sql", StringComparison.Ordinal)));
        await command.ExecuteNonQueryAsync();

        var rejected = await runner.RunMigrationsAsync(connectionString, older);
        rejected.Successful.Should().BeFalse();
        rejected.Error.Should().BeOfType<DatabaseSchemaCompatibilityException>();
        rejected.ErrorMessage.Should().Contain(DatabaseSchemaCompatibilityException.ErrorCode);
        var plan = await runner.PlanMigrationsAsync(connectionString, older);
        plan.Successful.Should().BeFalse();
        plan.Error.Should().BeOfType<DatabaseSchemaCompatibilityException>();
        command.CommandText = "SELECT display_name FROM fixture WHERE id = 7";
        (await command.ExecuteScalarAsync()).Should().Be("retained");
    }

    [SkippableFact]
    public async Task StartupGuard_WithMigrationsDisabled_RejectsUnknownNewerJournalEntry()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for the rollback boundary proof.");
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE public.schema_versions (scriptname text NOT NULL); INSERT INTO public.schema_versions VALUES ('Honua.Server.Migrations.999_Contract.sql');";
        await command.ExecuteNonQueryAsync();

        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var startup = () => guard.VerifyAsync(connectionString);
        var exception = (await startup.Should().ThrowAsync<DatabaseSchemaCompatibilityException>()).Which;
        exception.UnknownMigrations.Should().Equal("Honua.Server.Migrations.999_Contract.sql");
        exception.Message.Should().Contain("restore").And.Contain(DatabaseSchemaCompatibilityException.ErrorCode);
    }
}
