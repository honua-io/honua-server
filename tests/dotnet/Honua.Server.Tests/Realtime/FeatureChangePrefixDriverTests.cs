// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Events.Outbox;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

[Collection("FeatureChangePrefix")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class FeatureChangePrefixDriverTests(FeatureChangeDatabaseFixture database)
    : IClassFixture<FeatureChangeDatabaseFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await ExecuteAsync(await File.ReadAllTextAsync(RepositoryPaths.Resolve(
            "src", "Honua.Server", "Migrations", "024_CreateFeatureChangeOutbox.sql")));

        await ExecuteAsync("""
            ALTER TABLE honua.feature_change_outbox
                ADD COLUMN IF NOT EXISTS operation_instance_id text,
                ADD COLUMN IF NOT EXISTS correlation_id text,
                ADD COLUMN IF NOT EXISTS audit_id text,
                ADD COLUMN IF NOT EXISTS proposal_id text;
            TRUNCATE honua.feature_changes, honua.feature_change_outbox;
            ALTER SEQUENCE honua.sync_generation RESTART WITH 1;
            UPDATE honua.feature_change_prefix SET published = 0, pending = NULL, fence = NULL, pending_writers = NULL;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [IntegrationTest(Timeout = 120000)]
    public async Task ClaimPending_EmptyOutbox_AdvancesCommittedPrefixAndReleasesSingleSlot()
    {
        await AllocateCommittedAsync();
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider);

        (await TickAsync(repository)).Should().BeEmpty();
        (await PublishedAsync()).Should().Be(1, "no writer holds the generation lock");
        (await TickAsync(repository)).Should().BeEmpty();
        (await PublishedAsync()).Should().Be(1);
        provider.Opens.Should().Be(2);
        provider.Releases.Should().Be(provider.Opens);
    }

    [IntegrationTest(Timeout = 120000)]
    public Task ClaimPending_OpenWriter_HoldsPrefixUntilCommit() =>
        AssertOpenWriterAsync(transaction => transaction.CommitAsync());

    [IntegrationTest(Timeout = 120000)]
    public Task ClaimPending_OpenWriter_HoldsPrefixUntilRollback() =>
        AssertOpenWriterAsync(transaction => transaction.RollbackAsync());

    private async Task AssertOpenWriterAsync(Func<NpgsqlTransaction, Task> completeTransaction)
    {
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await AllocateAsync(writer);
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider);
        for (var tick = 0; tick < 3; tick++)
        {
            await TickAsync(repository);
            (await PublishedAsync()).Should().Be(0);
        }

        await completeTransaction(transaction);

        await TickAsync(repository);
        (await PublishedAsync()).Should().Be(1);
        provider.Releases.Should().Be(provider.Opens);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ClaimPending_PendingEvent_ClaimsAfterPublisherTransactionCommits()
    {
        await AllocateCommittedAsync();
        await SeedEventAsync();
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider);

        var entries = await TickAsync(repository);
        entries.Should().ContainSingle().Which.EventId.Should().Be("prefix-driver-event");
        (await TickAsync(repository)).Should().BeEmpty();
        (await PublishedAsync()).Should().Be(1);
        provider.Releases.Should().Be(2);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ClaimPending_RepeatableReadSession_UsesFreshReadCommittedPublication()
    {
        await AllocateCommittedAsync();
        await using var provider = new GatedProvider(database.ConnectionString)
        {
            RepeatableReadDefault = true
        };
        var repository = new PostgresFeatureChangeOutboxRepository(provider);

        await TickAsync(repository);
        await TickAsync(repository);
        (await PublishedAsync()).Should().Be(1);
        provider.Releases.Should().Be(2);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ClaimPending_PublisherBusy_StillClaimsPendingEvents()
    {
        await AllocateCommittedAsync();
        await SeedEventAsync();
        await using var publisher = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await publisher.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(144047712, 2)", publisher);
        await command.ExecuteNonQueryAsync();
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider);

        (await TickAsync(repository)).Should().ContainSingle();
        (await PublishedAsync()).Should().Be(0);
        provider.Releases.Should().Be(1);
        await transaction.CommitAsync();
        await TickAsync(repository);
        await TickAsync(repository);
        (await PublishedAsync()).Should().Be(1);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ClaimPending_UnsafeSequence_RejectsClaimAndReleasesConnectionForRetry()
    {
        await SeedEventAsync();
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider);
        await ExecuteAsync("ALTER SEQUENCE honua.sync_generation CACHE 2");
        try
        {
            var claim = async () => await TickAsync(repository);
            (await claim.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55000");
            provider.Releases.Should().Be(1);
        }
        finally
        {
            await ExecuteAsync("ALTER SEQUENCE honua.sync_generation CACHE 1");
        }

        (await TickAsync(repository)).Should().ContainSingle();
        provider.Releases.Should().Be(2);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ClaimPending_CancelledPublication_ReleasesTransactionAndManagedSlot()
    {
        await AllocateCommittedAsync();
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT singleton FROM honua.feature_change_prefix FOR UPDATE", blocker);
        await command.ExecuteScalarAsync();
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var pending = repository.ClaimPendingAsync("cancelled", 32, TimeSpan.FromSeconds(30), cancellation.Token);
        await WaitForBlockedPublicationAsync();
        await cancellation.CancelAsync();
        var claim = async () => await pending;
        await claim.Should().ThrowAsync<OperationCanceledException>();
        provider.Releases.Should().Be(1);
        await transaction.RollbackAsync();

        (await TickAsync(repository)).Should().BeEmpty();
        (await TickAsync(repository)).Should().BeEmpty();
        (await PublishedAsync()).Should().Be(1);
        provider.Releases.Should().Be(3);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ClaimPending_CancelledRequest_DoesNotConsumeManagedSlot()
    {
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var claim = async () => await repository.ClaimPendingAsync("node", 32, TimeSpan.FromSeconds(30), cancellation.Token);
        await claim.Should().ThrowAsync<OperationCanceledException>();

        (await TickAsync(repository)).Should().BeEmpty();
        provider.Releases.Should().Be(provider.Opens);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task WriteOutboxRow_WakesTheDispatcherSignal_AndTheRowIsClaimedAfterCommit()
    {
        await ExecuteAsync("""
            ALTER TABLE honua.feature_changes
                ADD COLUMN IF NOT EXISTS event_id text,
                ADD COLUMN IF NOT EXISTS operation_instance_id text,
                ADD COLUMN IF NOT EXISTS correlation_id text,
                ADD COLUMN IF NOT EXISTS audit_id text,
                ADD COLUMN IF NOT EXISTS proposal_id text;
            """);
        var wake = new PollWakeSignal<IFeatureChangeOutboxRepository>();
        await using var provider = new GatedProvider(database.ConnectionString);
        var repository = new PostgresFeatureChangeOutboxRepository(provider, wake);
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await AllocateAsync(writer);

        await repository.WriteOutboxRowAsync(writer, transaction, NewEntry("wake-event"), CancellationToken.None);

        wake.Version.Should().Be(1, "a local outbox write must end the dispatcher's idle wait");
        (await TickAsync(repository)).Should().BeEmpty("the row is not visible before the writer commits");
        await transaction.CommitAsync();
        (await TickAsync(repository)).Should().ContainSingle().Which.EventId.Should().Be("wake-event");
    }

    private static FeatureChangeOutboxEntry NewEntry(string eventId) => new()
    {
        OutboxId = Guid.NewGuid(),
        ServiceId = "service",
        LayerId = 734401,
        ObjectId = 1,
        Operation = "insert",
        Protocol = "test",
        RequestId = "request",
        EventId = eventId,
        EventPayload = "{}",
        Status = OutboxStatuses.Pending,
        RetryCount = 0,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Task<IReadOnlyList<FeatureChangeOutboxEntry>> TickAsync(
        PostgresFeatureChangeOutboxRepository repository)
        => repository.ClaimPendingAsync("prefix-driver", 32, TimeSpan.FromSeconds(30), CancellationToken.None);

    private async Task AllocateCommittedAsync()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await AllocateAsync(connection);
        await transaction.CommitAsync();
    }

    private static async Task AllocateAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock_shared(144047712, 0);
            INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, changed_at)
            VALUES (nextval('honua.sync_generation'), 734401, 1, 1, now());
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task SeedEventAsync() => ExecuteAsync("""
        INSERT INTO honua.feature_change_outbox
            (service_id, layer_id, object_id, operation, protocol, request_id, event_id, event_payload)
        VALUES ('service', 734401, 1, 'insert', 'test', 'request', 'prefix-driver-event', '{}');
        """);

    private async Task WaitForBlockedPublicationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var command = database.DataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query LIKE '%SELECT honua.publish_feature_change_prefix()%'
            );
            """);
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private async Task<long> PublishedAsync()
    {
        await using var command = database.DataSource.CreateCommand("SELECT published FROM honua.feature_change_prefix");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class GatedProvider(string connectionString) : IAdoNetDatabaseConnectionProvider, IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 1, MinPoolSize = 0 }.ConnectionString);
        private readonly SemaphoreSlim _gate = new(1, 1);
        public bool RepeatableReadDefault { get; init; }
        public int Opens { get; private set; }
        public int Releases { get; private set; }

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _gate.WaitAsync(timeout.Token);
            NpgsqlConnection? connection = null;
            try
            {
                connection = await _source.OpenConnectionAsync(timeout.Token);
                if (RepeatableReadDefault)
                {
                    await using var command = new NpgsqlCommand("SET default_transaction_isolation = 'repeatable read'", connection);
                    await command.ExecuteNonQueryAsync(timeout.Token);
                }

                Opens++;
                return new SemaphoreReleasingConnection(connection, () => { Releases++; _gate.Release(); });
            }
            catch
            {
                try
                {
                    if (connection is not null)
                    {
                        await connection.DisposeAsync();
                    }
                }
                finally
                {
                    _gate.Release();
                }

                throw;
            }
        }

        public string GetConnectionString() => throw new InvalidOperationException("The managed source must be used.");
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The driver must open its own managed lease.");
        public async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            _gate.Dispose();
        }
    }
}
