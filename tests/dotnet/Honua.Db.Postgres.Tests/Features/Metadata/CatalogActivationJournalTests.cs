// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

[Collection("Database")]
public sealed class CatalogActivationJournalTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public Task ActivateCatalog_BootstrapAndNoOp_DoNotStartWatch() => InSchemaAsync(async connection =>
    {
        await ActivateAsync(connection, 1);
        await ActivateAsync(connection, 1);
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches")).Should().Be(0);
    });

    [IntegrationTest]
    public Task ActivateCatalog_ChangedRevision_PreservesPriorDocumentAndExpectedEtag() => InSchemaAsync(async connection =>
    {
        await ActivateAsync(connection, 1);
        await ActivateAsync(connection, 2);
        await using var command = new NpgsqlCommand("""
            SELECT revision, expected_etag, previous_revision, previous_document->>'name', state,
                   activated_at <= clock_timestamp()
              FROM catalog_activation_watches
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        reader.GetString(1).Should().Be("etag-2");
        reader.GetInt64(2).Should().Be(1);
        reader.GetString(3).Should().Be("catalog-1");
        reader.GetString(4).Should().Be("watching");
        reader.GetBoolean(5).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeFalse();
    });

    [IntegrationTest]
    public Task ActivateCatalog_TransactionAborts_LeavesPriorWatchCurrent() => InSchemaAsync(async connection =>
    {
        await ActivateAsync(connection, 1);
        await ActivateAsync(connection, 2);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ActivateAsync(connection, 3);
            await transaction.RollbackAsync();
        }
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches")).Should().Be(1);
        (await ScalarAsync(connection, "SELECT revision FROM catalog_activation_watches WHERE state = 'watching'"))
            .Should().Be(2);
    });

    [IntegrationTest]
    public Task ActivateCatalog_ManyChanges_BoundsHistoryAndSupersedesEarlierWatches() => InSchemaAsync(async connection =>
    {
        for (var revision = 1; revision <= 36; revision++)
        {
            await ActivateAsync(connection, revision);
        }
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches")).Should().Be(32);
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches WHERE state = 'watching'"))
            .Should().Be(1);
        (await ScalarAsync(connection, "SELECT revision FROM catalog_activation_watches WHERE state = 'watching'"))
            .Should().Be(36);
        await ExecuteAsync(connection, "DELETE FROM metadata_v2_snapshots WHERE revision < 36");
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches WHERE previous_document IS NOT NULL"))
            .Should().Be(32);
    });

    [IntegrationTest]
    public Task ActivateCatalog_AnotherEnvironment_DoesNotSupersedeExistingWatch() => InSchemaAsync(async connection =>
    {
        await ActivateAsync(connection, 1);
        await ActivateAsync(connection, 2);
        await ActivateAsync(connection, 1, "Other");
        await ActivateAsync(connection, 2, "Other");
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches WHERE state = 'watching'"))
            .Should().Be(2);
    });

    [IntegrationTest]
    public Task ActivateCatalog_UnacknowledgedPurge_PreservesRollbackRecordBeyondRetention() => InSchemaAsync(async connection =>
    {
        await ActivateAsync(connection, 1);
        await ActivateAsync(connection, 2);
        await ExecuteAsync(connection, """
            UPDATE catalog_activation_watches SET state = 'rolled_back', rollback_revision = 3,
                rollback_change_id = 'rollback-2', rollback_reason = 'Error rate increased';
            """);
        for (var revision = 3; revision <= 38; revision++)
        {
            await ActivateAsync(connection, revision);
        }
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches")).Should().Be(33);
        (await ScalarAsync(connection, "SELECT revision FROM catalog_activation_watches WHERE state = 'rolled_back'"))
            .Should().Be(2);
        await ExecuteAsync(connection, """
            UPDATE catalog_activation_watches SET purge_completed_at = clock_timestamp() WHERE state = 'rolled_back';
            """);
        await ActivateAsync(connection, 39);
        (await ScalarAsync(connection, "SELECT count(*) FROM catalog_activation_watches")).Should().Be(32);
    });

    [IntegrationTest]
    public Task RecordRollback_MissingRevision_RejectsIncompleteRecord() => InSchemaAsync(async connection =>
    {
        await ActivateAsync(connection, 1);
        await ActivateAsync(connection, 2);
        var update = () => ExecuteAsync(connection, """
            UPDATE catalog_activation_watches SET state = 'rolled_back',
                rollback_change_id = 'rollback-2', rollback_reason = 'Error rate increased';
            """);
        await update.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);
    });

    private async Task InSchemaAsync(Func<NpgsqlConnection, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(CatalogActivationJournalTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "src", "Honua.Server", "Migrations", "162_CreateCatalogActivationWatches.sql"));
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await ExecuteAsync(connection, sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal));
            await ExecuteAsync(connection, $"SET search_path TO \"{schema}\"");
            await test(connection);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static Task ActivateAsync(NpgsqlConnection connection, long revision, string environment = "Test") => ExecuteAsync(connection, $$"""
        INSERT INTO metadata_v2_snapshots
            (environment, revision, schema_version, api_version, document, etag, generated_at)
        VALUES ('{{environment}}', {{revision}}, '2', '2', '{"name":"catalog-{{revision}}"}', 'etag-{{revision}}', now())
        ON CONFLICT DO NOTHING;
        INSERT INTO metadata_v2_current (environment, revision, etag)
        VALUES ('{{environment}}', {{revision}}, 'etag-{{revision}}')
        ON CONFLICT (environment) DO UPDATE SET revision = EXCLUDED.revision, etag = EXCLUDED.etag;
        """);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
