// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
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
        rejected.Error.Should().BeOfType<DatabaseSchemaCompatibilityException>()
            .Which.DeclarationRefusals.Should().BeEmpty("a journal with no declaration table keeps the original refusal (#6477)");
        rejected.ErrorMessage.Should().Contain(DatabaseSchemaCompatibilityException.ErrorCode)
            .And.EndWith("Application rollback does not reverse database migrations.");
        var plan = await runner.PlanMigrationsAsync(connectionString, older);
        plan.Successful.Should().BeFalse();
        plan.Error.Should().BeOfType<DatabaseSchemaCompatibilityException>();
        command.CommandText = "SELECT display_name FROM fixture WHERE id = 7";
        (await command.ExecuteScalarAsync()).Should().Be("retained");
    }

    [SkippableTheory]
    [InlineData("contract")]
    [InlineData("forged-expand")]
    [InlineData("tampered-text")]
    [InlineData("floor-above-reader")]
    [InlineData("opted-out")]
    public async Task OlderReader_AfterNewerDeclaredEntryItCannotVerify_RefusesWithoutChangingData(string kind)
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for the rollback boundary proof.");
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var name = $"rollback_declared_{Guid.NewGuid():N}";
        const string original = "CREATE TABLE fixture (id integer PRIMARY KEY, legacy_name text); INSERT INTO fixture VALUES (7, 'retained');";
        var older = SyntheticMigrationsCompiler.Compile(name, ("001_fixture.sql", original), ("002_reader_compatibility.sql", ReaderCompatibilityJournalScript()));
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest), ServerCoreSchemaMigrations.Manifest);
        (await runner.RunMigrationsAsync(connectionString, older)).Successful.Should().BeTrue();

        // Simulate the newer revision: apply its script, journal it, and record its declaration
        // exactly as the newer runner does. Each variant is one the older reader must not accept.
        var newerScript = $"{name}.003_newer.sql";
        const string rename = "ALTER TABLE fixture RENAME COLUMN legacy_name TO display_name;";
        const string additive = "ALTER TABLE fixture ADD COLUMN note text;";
        var (applied, declaration, reason) = kind switch
        {
            "contract" => (rename, MigrationReaderCompatibility.Declare(newerScript, rename), "declared contract, not expand"),
            "forged-expand" => (rename, MigrationReaderCompatibility.Declare(newerScript, rename) with
            {
                Classification = MigrationReaderCompatibilityClassification.Expand,
            }, "this reader classifies the recorded script as contract"),
            "tampered-text" => (rename, MigrationReaderCompatibility.Declare(newerScript, additive) with
            {
                ScriptText = rename,
            }, "the recorded script text does not match its recorded hash"),
            "floor-above-reader" => (additive, MigrationReaderCompatibility.Declare(
                newerScript, "-- honua:reader-compatible-from schema=3\n" + additive),
                "declared compatible from reader schema 3; this reader is schema 2"),
            _ => (additive, MigrationReaderCompatibility.Declare(
                newerScript, "-- honua:reader-incompatible reason=older readers violate the new trigger\n" + additive),
                "declared reader-incompatible, not expand"),
        };
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = applied + " INSERT INTO public.schema_versions (scriptname, applied) VALUES (@name, now());";
        command.Parameters.AddWithValue("name", newerScript);
        await command.ExecuteNonQueryAsync();
        await PostgresDatabaseMigrationRunner.RecordReaderCompatibilityDeclarationsAsync(connection, [declaration], CancellationToken.None);

        var rejected = await runner.RunMigrationsAsync(connectionString, older);

        rejected.Successful.Should().BeFalse();
        var refusal = rejected.Error.Should().BeOfType<DatabaseSchemaCompatibilityException>().Which;
        refusal.UnknownMigrations.Should().Equal(newerScript);
        refusal.DeclarationRefusals.Should().ContainKey(newerScript).WhoseValue.Should().Be(reason);
        (await runner.PlanMigrationsAsync(connectionString, older)).Error.Should().BeOfType<DatabaseSchemaCompatibilityException>();
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM public.schema_versions";
        (await command.ExecuteScalarAsync()).Should().Be(3L, "a refused rollback applies nothing");
        command.CommandText = "SELECT COUNT(*) FROM fixture WHERE id = 7";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }

    [SkippableFact]
    public async Task StartupGuard_WithMigrationsDisabled_RejectsDeclaredExpandEntryItDoesNotShip()
    {
        Skip.IfNot(postgres.Available, "Docker/PostgreSQL is required for the rollback boundary proof.");
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        const string future = "Honua.Server.Migrations.999_FutureExpand.sql";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE public.schema_versions (scriptname text NOT NULL); " + ReaderCompatibilityJournalScript() +
            " INSERT INTO public.schema_versions VALUES ('" + future + "');";
        await command.ExecuteNonQueryAsync();
        var declaration = MigrationReaderCompatibility.Declare(future, "CREATE TABLE IF NOT EXISTS honua.future_note (id integer);");
        MigrationReaderCompatibility.Verify(declaration, future, 135).Accepted.Should().BeTrue();
        await PostgresDatabaseMigrationRunner.RecordReaderCompatibilityDeclarationsAsync(connection, [declaration], CancellationToken.None);

        // HONUA_SKIP_MIGRATIONS=true runs only the guard; its reader check is unchanged by #6477.
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var startup = () => guard.VerifyAsync(connectionString);
        (await startup.Should().ThrowAsync<DatabaseSchemaCompatibilityException>()).Which
            .UnknownMigrations.Should().Equal(future);
    }

    private static string ReaderCompatibilityJournalScript()
    {
        using var stream = typeof(ServerCoreSchemaMigrations).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.135_RecordMigrationReaderCompatibility.sql")
            ?? throw new InvalidOperationException("Migration 135 is not embedded in the server assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
