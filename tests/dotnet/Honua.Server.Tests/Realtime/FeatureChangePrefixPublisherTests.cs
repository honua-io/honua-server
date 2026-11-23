// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

[Collection("FeatureChangePrefix")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class FeatureChangePrefixPublisherTests(FeatureChangeDatabaseFixture database)
    : IClassFixture<FeatureChangeDatabaseFixture>, IAsyncLifetime
{
    private const string PublishSql = "SELECT honua.publish_feature_change_prefix()";
    // The writer-trigger contract from migration 228: hold the shared generation lock before nextval.
    private const string AllocateSql = """
        SELECT pg_advisory_xact_lock_shared(144047712, 0);
        INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, changed_at)
        VALUES (nextval('honua.sync_generation'), 734401, 1, 1, now())
        RETURNING generation;
        """;

    public async Task InitializeAsync()
    {
        await ExecuteAsync("""
            TRUNCATE honua.feature_changes;
            ALTER SEQUENCE honua.sync_generation RESTART WITH 1;
            UPDATE honua.feature_change_prefix SET published = 0, pending = NULL, fence = NULL, pending_writers = NULL;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_EmptySequence_DoesNotAssignTransactionIdOrAdvance()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        (await ScalarAsync(PublishSql, connection)).Should().Be(0);
        await using var command = new NpgsqlCommand("SELECT pg_current_xact_id_if_assigned() IS NULL", connection);
        (await command.ExecuteScalarAsync()).Should().Be(true);
        await transaction.CommitAsync();
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_LaterWriterCommitsFirst_HoldsThenPublishesBoth()
    {
        await using var older = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await older.BeginTransactionAsync();
        var first = await AllocateAsync(older);
        var second = await AllocateCommittedAsync();
        second.Should().BeGreaterThan(first);

        (await TickAsync()).Should().Be(0);
        (await TickAsync()).Should().Be(0);
        (await VisibleThroughAsync(0)).Should().BeEmpty();
        await transaction.CommitAsync();

        (await SettleAsync()).Should().Be(second);
        (await VisibleThroughAsync(second)).Should().Equal(first, second);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_LoneOpenWriter_HoldsAcrossRepeatedCallsThenPublishesCommit()
    {
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        var generation = await AllocateAsync(writer);

        (await TickAsync()).Should().Be(0);
        (await TickAsync()).Should().Be(0);
        (await TickAsync()).Should().Be(0);
        await transaction.CommitAsync();

        (await SettleAsync()).Should().Be(generation);
        (await VisibleThroughAsync(generation)).Should().Equal(generation);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_LoneOpenWriter_HoldsAcrossRepeatedCallsThenReleasesRollbackGap()
    {
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        var generation = await AllocateAsync(writer);

        (await TickAsync()).Should().Be(0);
        (await TickAsync()).Should().Be(0);
        (await TickAsync()).Should().Be(0);
        await transaction.RollbackAsync();

        (await SettleAsync()).Should().Be(generation);
        (await VisibleThroughAsync(generation)).Should().BeEmpty();
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_RolledBackWriter_ReleasesFenceAcrossSequenceGap()
    {
        await using var older = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await older.BeginTransactionAsync();
        var rolledBack = await AllocateAsync(older);
        var committed = await AllocateCommittedAsync();
        (await TickAsync()).Should().Be(0);
        await transaction.RollbackAsync();

        (await SettleAsync()).Should().Be(committed);
        (await VisibleThroughAsync(committed)).Should().Equal(committed);
        committed.Should().BeGreaterThan(rolledBack);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_OlderTransactionAllocatesAfterFence_DoesNotSkipItsChange()
    {
        await using var older = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await older.BeginTransactionAsync();
        await using (var assign = new NpgsqlCommand("SELECT pg_current_xact_id()", older))
        {
            await assign.ExecuteScalarAsync();
        }

        var first = await AllocateCommittedAsync();
        (await TickAsync()).Should().Be(first, "no writer holds the generation lock");
        var late = await AllocateAsync(older);
        (await TickAsync()).Should().Be(first);
        await transaction.CommitAsync();

        (await SettleAsync()).Should().Be(late);
        (await VisibleThroughAsync(late)).Should().Equal(first, late);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_CompetingPublisher_ReturnsWithoutWaitingOrChangingState()
    {
        var committed = await AllocateCommittedAsync();
        await using var lockOwner = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await lockOwner.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(144047712, 2)", lockOwner))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using var contender = await database.DataSource.OpenConnectionAsync();
        await using (var timeout = new NpgsqlCommand("SET statement_timeout = '2s'", contender))
        {
            await timeout.ExecuteNonQueryAsync();
        }

        (await ScalarAsync(PublishSql, contender)).Should().Be(0);
        await transaction.CommitAsync();
        (await SettleAsync()).Should().Be(committed);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_PublisherTransactionRollsBack_DoesNotLosePendingWork()
    {
        var committed = await AllocateCommittedAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            (await ScalarAsync(PublishSql, connection)).Should().Be(committed);
            await transaction.RollbackAsync();
        }

        await using (var command = database.DataSource.CreateCommand(
            "SELECT published FROM honua.feature_change_prefix"))
        {
            ((long)(await command.ExecuteScalarAsync())!).Should().Be(0);
        }

        (await SettleAsync()).Should().Be(committed);
        (await VisibleThroughAsync(committed)).Should().Equal(committed);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_FreshConnection_ResumesPersistedFence()
    {
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        var generation = await AllocateAsync(writer);
        (await TickAsync()).Should().Be(0);
        await transaction.CommitAsync();

        await using var independentSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var publisher = await independentSource.OpenConnectionAsync();
        (await ScalarAsync(PublishSql, publisher)).Should().Be(generation);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_AfterWriterAssignedTransactionId_RejectsUnsafeInvocation()
    {
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await AllocateAsync(writer);
        var publish = async () => await ScalarAsync(PublishSql, writer);
        (await publish.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("25001");
        await transaction.RollbackAsync();
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_RepeatableRead_RejectsStaleSnapshot()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var publish = async () => await ScalarAsync(PublishSql, connection);
        (await publish.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("25001");
        await transaction.RollbackAsync();
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_ReadOnlyTransaction_RejectsInvocation()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var publish = async () => await ScalarAsync(PublishSql, connection);
        (await publish.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("25006");
        await transaction.RollbackAsync();
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_SequenceCacheChanged_RejectsUnsafeAllocationBoundary()
    {
        await ExecuteAsync("ALTER SEQUENCE honua.sync_generation CACHE 2");
        try
        {
            var publish = async () => await TickAsync();
            (await publish.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55000");
        }
        finally
        {
            await ExecuteAsync("ALTER SEQUENCE honua.sync_generation CACHE 1");
        }
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_NoWriterInFlight_PublishesInOneCall()
    {
        var generation = await AllocateCommittedAsync();

        (await TickAsync()).Should().Be(generation);
        await using var command = database.DataSource.CreateCommand("SELECT honua.feature_change_read_horizon()");
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(generation);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_UnrelatedOpenTransaction_DoesNotHoldBackPrefix()
    {
        await using var unrelated = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await unrelated.BeginTransactionAsync();
        await using (var assign = new NpgsqlCommand("SELECT pg_current_xact_id()", unrelated))
        {
            await assign.ExecuteScalarAsync();
        }

        var generation = await AllocateCommittedAsync();
        (await TickAsync()).Should().Be(generation, "only holders of the writer lock fence the prefix");
        await transaction.RollbackAsync();
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Publish_ActivatedMigration_WritersShareTheGenerationLock()
    {
        await using var body = database.DataSource.CreateCommand("""
            SELECT position('pg_advisory_xact_lock_shared(144047712, 0)' IN pg_get_functiondef(oid)) > 0
                   AND position('pg_advisory_xact_lock(144047712, 0)' IN pg_get_functiondef(oid)) = 0
            FROM pg_proc WHERE oid IN ('honua.track_feature_changes()'::regprocedure,
                                      'honua.track_version_edits()'::regprocedure)
            """);
        await using var reader = await body.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            reader.GetBoolean(0).Should().BeTrue();
            count++;
        }

        count.Should().Be(2);
    }

    private async Task<long> AllocateCommittedAsync()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var generation = await AllocateAsync(connection);
        await transaction.CommitAsync();
        return generation;
    }

    private static async Task<long> AllocateAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(AllocateSql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.NextResultAsync()).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeTrue();
        return reader.GetInt64(0);
    }

    private async Task<long> TickAsync()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        return await ScalarAsync(PublishSql, connection);
    }

    private async Task<long> SettleAsync()
    {
        // Two cycles normally suffice: capture a boundary, then observe its transactions retired.
        var head = 0L;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            head = await TickAsync();
        }

        return head;
    }

    private async Task<IReadOnlyList<long>> VisibleThroughAsync(long horizon)
    {
        await using var command = database.DataSource.CreateCommand(
            "SELECT generation FROM honua.feature_changes WHERE generation <= @horizon ORDER BY generation");
        command.Parameters.AddWithValue("horizon", horizon);
        await using var reader = await command.ExecuteReaderAsync();
        var generations = new List<long>();
        while (await reader.ReadAsync())
        {
            generations.Add(reader.GetInt64(0));
        }

        return generations;
    }

    private static async Task<long> ScalarAsync(string sql, NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
