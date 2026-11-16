// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

[Collection("Database")]
public sealed class ExternalLayerChangeJournalTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public Task RecordChange_DisabledLayer_DoesNotAdvanceRevision() => InSchemaAsync(async connection =>
    {
        await ExecuteAsync(connection, "INSERT INTO external_layer_revisions (layer_id) VALUES (7)");
        (await ScalarAsync(connection, "SELECT record_external_layer_change(7)")).Should().Be(DBNull.Value);
        (await ScalarAsync(connection, "SELECT revision FROM external_layer_revisions WHERE layer_id = 7"))
            .Should().Be(0L);
        (await ScalarAsync(connection, "SELECT count(*) FROM external_layer_changes")).Should().Be(0L);
    });

    [IntegrationTest]
    public Task RecordChange_TransactionAborts_RollsBackRevisionAndPendingPurge() => InSchemaAsync(async connection =>
    {
        await EnableAsync(connection);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            (await ScalarAsync(connection, "SELECT record_external_layer_change(7)")).Should().Be(1L);
            await transaction.RollbackAsync();
        }

        (await ScalarAsync(connection, "SELECT record_external_layer_change(7)")).Should().Be(1L);
        (await ScalarAsync(connection, "SELECT count(*) FROM external_layer_changes")).Should().Be(1L);
    });

    [IntegrationTest]
    public Task RecordChange_InvalidExtent_RejectsWithoutAdvancingRevision() => InSchemaAsync(async connection =>
    {
        await EnableAsync(connection);
        foreach (var extent in new[] { "ARRAY[181.,0.,182.,1.]", "ARRAY[1.,2.,0.,3.]", "ARRAY[0.,0.]", "ARRAY[0.,NULL,1.,1.]" })
        {
            var record = () => ScalarAsync(connection, $"SELECT record_external_layer_change(7, {extent}::double precision[])");
            await record.Should().ThrowAsync<PostgresException>()
                .Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);
        }

        (await ScalarAsync(connection, "SELECT revision FROM external_layer_revisions WHERE layer_id = 7"))
            .Should().Be(0L);
    });

    [IntegrationTest]
    public Task ClaimChange_AnotherHostOwnsLock_SkipsUntilAbortedClaimCanRetry() => InSchemaAsync(async connection =>
    {
        await EnableAsync(connection);
        await ScalarAsync(connection, "SELECT record_external_layer_change(7, ARRAY[-10.,-5.,10.,5.])");
        await using var otherHost = await fixture.DataSource.OpenConnectionAsync();
        var schema = await ScalarAsync(connection, "SELECT current_schema()");
        await ExecuteAsync(otherHost, $"SET search_path TO \"{schema}\"");
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            (await ScalarAsync(connection, ClaimSql)).Should().Be(1L);
            (await ScalarAsync(otherHost, ClaimSql)).Should().BeNull();
            await transaction.RollbackAsync();
        }

        (await ScalarAsync(otherHost, ClaimSql)).Should().Be(1L);
        (await ScalarAsync(otherHost, "SELECT count(*) FROM external_layer_changes")).Should().Be(1L);
    });

    [IntegrationTest]
    public Task AcknowledgeChange_NewerChange_PreservesNewPendingPurge() => InSchemaAsync(async connection =>
    {
        await EnableAsync(connection);
        await ScalarAsync(connection, "SELECT record_external_layer_change(7)");
        await ScalarAsync(connection, "SELECT record_external_layer_change(7)");
        await ExecuteAsync(connection, "DELETE FROM external_layer_changes WHERE layer_id = 7 AND revision = 1");
        (await ScalarAsync(connection, ClaimSql)).Should().Be(2L);
        (await ScalarAsync(connection, "SELECT revision FROM external_layer_revisions WHERE layer_id = 7"))
            .Should().Be(2L);
    });

    [IntegrationTest]
    public Task RecordChange_TwoWriters_AssignsRevisionInCommitOrder() => InSchemaAsync(async connection =>
    {
        await EnableAsync(connection);
        await using var otherHost = await fixture.DataSource.OpenConnectionAsync();
        var schema = await ScalarAsync(connection, "SELECT current_schema()");
        await ExecuteAsync(otherHost, $"SET search_path TO \"{schema}\"");
        await using var transaction = await connection.BeginTransactionAsync();
        (await ScalarAsync(connection, "SELECT record_external_layer_change(7)")).Should().Be(1L);
        var secondWriter = ScalarAsync(otherHost, "SELECT record_external_layer_change(7)");
        await transaction.CommitAsync();
        (await secondWriter).Should().Be(2L);
        (await ScalarAsync(otherHost, "SELECT count(*) FROM external_layer_changes")).Should().Be(2L);
    });

    private const string ClaimSql = "SELECT revision FROM external_layer_changes ORDER BY layer_id, revision LIMIT 1 FOR UPDATE SKIP LOCKED";

    private static Task EnableAsync(NpgsqlConnection connection) => ExecuteAsync(connection,
        "INSERT INTO external_layer_revisions (layer_id, enabled) VALUES (7, true)");

    private async Task InSchemaAsync(Func<NpgsqlConnection, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ExternalLayerChangeJournalTests));
        try
        {
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "src", "Honua.Server", "Migrations", "165_CreateExternalLayerChanges.sql"));
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
