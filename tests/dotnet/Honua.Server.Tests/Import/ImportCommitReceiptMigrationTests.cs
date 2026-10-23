// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DbUp;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Import;

[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Import)]
public sealed class ImportCommitReceiptMigrationTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalReceiptConstraint_FreshAndApplied136Upgrade(bool previouslyApplied)
    {
        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        string? database = null;
        try
        {
            var connectionString = await postgres.CreateIsolatedDatabaseAsync(nameof(ImportCommitReceiptMigrationTests));
            database = new NpgsqlConnectionStringBuilder(connectionString).Database;
            const string schema = "receipt_migration";
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection))
                await create.ExecuteNonQueryAsync();

            if (previouslyApplied)
            {
                await ApplyAsync(includeCorrection: false);
                // Reproduce the original durable rejection before applying the correction.
                await RejectAsync("v1:sha256:" + new string('a', 64));
            }

            await ApplyAsync(includeCorrection: true);
            await using (var valid = Insert("v1:sha256:" + new string('a', 64)))
                (await valid.ExecuteNonQueryAsync()).Should().Be(1);
            await RejectAsync("v1:sha256:" + new string('a', 63));
            await RejectAsync("v1:sha256:" + new string('a', 65));
            await RejectAsync("v2:sha256:" + new string('a', 64));
            await RejectAsync("v1:sha256:" + new string('A', 64));
            await RejectAsync("v1:sha256:" + new string('a', 63) + "\n");

            await using var retained = new NpgsqlCommand($"SELECT count(*) FROM {schema}.import_commit_receipt", connection);
            (await retained.ExecuteScalarAsync()).Should().Be(1L);

            async Task ApplyAsync(bool includeCorrection)
            {
                await postgres.RunUnderSchemaMutationLockAsync(() =>
                {
                    var upgrade = DeployChanges.To.PostgresqlDatabase(connectionString, schema)
                        .JournalToPostgresqlTable(schema, "schema_versions")
                        .WithScriptsEmbeddedInAssembly(typeof(Program).Assembly, name =>
                            name.EndsWith("136_CreateImportCommitReceipts.sql", StringComparison.Ordinal) ||
                            includeCorrection && name.EndsWith("139_FixImportCommitReceiptFingerprintConstraint.sql", StringComparison.Ordinal))
                        .WithVariable("HonuaSchema", "\"" + schema + "\"")
                        .WithTransaction()
                        .Build().PerformUpgrade();
                    upgrade.Successful.Should().BeTrue(upgrade.Error?.ToString());
                    upgrade.Scripts.Select(script => script.Name).Should().HaveCount(includeCorrection && !previouslyApplied ? 2 : 1);
                    return Task.CompletedTask;
                });
            }

            NpgsqlCommand Insert(string fingerprint)
            {
                var command = new NpgsqlCommand($"INSERT INTO {schema}.import_commit_receipt (operation_id, intent_fingerprint, receipt) VALUES ($1, $2, $3)", connection);
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = Guid.NewGuid().ToString("N") });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = fingerprint });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = new byte[] { 1 } });
                return command;
            }

            async Task RejectAsync(string fingerprint)
            {
                await using var invalid = Insert(fingerprint);
                var failure = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
                failure.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
                failure.ConstraintName.Should().Be("import_commit_receipt_intent");
            }
        }
        finally
        {
            if (database is not null) await postgres.DropDatabaseAsync(database);
            await postgres.DisposeAsync();
        }
    }
}
