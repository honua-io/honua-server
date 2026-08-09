// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Alerts;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Alerts;

/// <summary>
/// Integration tests for <see cref="PostgresAlertDispatchStore"/> backlog counting and
/// delivered-row retention (#2481). The dispatch store targets the literal
/// <c>honua.alert_dispatch</c> table (search-path isolation does not scope it), so this
/// suite mirrors <see cref="PostgresAlertAdminStoreTests"/>'s global-schema pattern.
/// </summary>
[Collection("Database")]
public sealed class PostgresAlertDispatchStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task GetBacklogAsync_ExcludesDeliveredRows_AndCountsPendingAndDeadLettered()
    {
        await EnsureAlertSchemaAsync();
        await ClearAlertTablesAsync();

        var eventId = await InsertRuleAndEventAsync();
        var oldest = DateTimeOffset.UtcNow.AddMinutes(-10);
        await InsertDispatchAsync(eventId, status: 0, deliveredAt: null, createdAt: oldest); // pending
        await InsertDispatchAsync(eventId, status: 1, deliveredAt: null);              // processing
        await InsertDispatchAsync(eventId, status: 3, deliveredAt: null);              // failed (retriable)
        await InsertDispatchAsync(eventId, status: 4, deliveredAt: null);              // dead-letter
        await InsertDispatchAsync(eventId, status: 4, deliveredAt: null, channelType: 3); // email dead-letter
        await InsertDispatchAsync(eventId, status: 2, deliveredAt: DateTimeOffset.UtcNow); // delivered
        await fixture.ApplyGlobalSeedSqlAsync("""
            INSERT INTO honua.alert_channel_state (channel_type, is_paused)
            VALUES (1, true)
            ON CONFLICT (channel_type) DO UPDATE SET is_paused = EXCLUDED.is_paused;
            """);

        var store = new PostgresAlertDispatchStore(
            new TestConnectionProvider(fixture.DataSource),
            NullLogger<PostgresAlertDispatchStore>.Instance);

        var backlog = await store.GetBacklogAsync();

        backlog.PendingCount.Should().Be(3, "pending, processing, and retriable-failed rows are backlog");
        backlog.RetryingCount.Should().Be(1);
        backlog.DeadLetteredCount.Should().Be(2);
        backlog.OldestItemAt.Should().BeCloseTo(oldest, TimeSpan.FromSeconds(1));
        backlog.Channels.Should().HaveCount(2);
        backlog.Channels[0].ChannelType.Should().Be(Honua.Core.Features.Alerts.Domain.AlertChannelType.Webhook);
        backlog.Channels[0].IsPaused.Should().BeTrue();
        backlog.Channels[0].PendingCount.Should().Be(2);
        backlog.Channels[0].RetryingCount.Should().Be(1);
        backlog.Channels[0].DeadLetteredCount.Should().Be(1);
        backlog.Channels[0].OldestItemAt.Should().BeCloseTo(oldest, TimeSpan.FromSeconds(1));
        backlog.Channels[1].ChannelType.Should().Be(Honua.Core.Features.Alerts.Domain.AlertChannelType.Email);
        backlog.Channels[1].DeadLetteredCount.Should().Be(1);
        backlog.PendingCount.Should().Be(backlog.Channels.Sum(channel => channel.PendingCount + channel.RetryingCount));
        backlog.RetryingCount.Should().Be(backlog.Channels.Sum(channel => channel.RetryingCount));
        backlog.DeadLetteredCount.Should().Be(backlog.Channels.Sum(channel => channel.DeadLetteredCount));
    }

    [IntegrationTest]
    public async Task PurgeDeliveredAsync_DeletesOnlyOldDeliveredRows()
    {
        await EnsureAlertSchemaAsync();
        await ClearAlertTablesAsync();

        var eventId = await InsertRuleAndEventAsync();
        var now = DateTimeOffset.UtcNow;
        await InsertDispatchAsync(eventId, status: 2, deliveredAt: now.AddHours(-48)); // old delivered → purged
        await InsertDispatchAsync(eventId, status: 2, deliveredAt: now.AddHours(-1));  // recent delivered → kept
        await InsertDispatchAsync(eventId, status: 0, deliveredAt: null);              // pending → kept
        await InsertDispatchAsync(eventId, status: 4, deliveredAt: null);              // dead-letter → kept

        var store = new PostgresAlertDispatchStore(
            new TestConnectionProvider(fixture.DataSource),
            NullLogger<PostgresAlertDispatchStore>.Instance);

        var deleted = await store.PurgeDeliveredAsync(now.AddHours(-24), batchLimit: 100);

        deleted.Should().Be(1, "only the delivered row older than the cutoff is purged");
        (await CountDispatchAsync()).Should().Be(3);
        (await CountDispatchByStatusAsync(2)).Should().Be(1, "the recent delivered row is retained");
    }

    [IntegrationTest]
    public async Task PurgeDeliveredAsync_RespectsBatchLimit()
    {
        await EnsureAlertSchemaAsync();
        await ClearAlertTablesAsync();

        var eventId = await InsertRuleAndEventAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await InsertDispatchAsync(eventId, status: 2, deliveredAt: now.AddHours(-48));
        }

        var store = new PostgresAlertDispatchStore(
            new TestConnectionProvider(fixture.DataSource),
            NullLogger<PostgresAlertDispatchStore>.Instance);

        var deleted = await store.PurgeDeliveredAsync(now.AddHours(-24), batchLimit: 2);

        deleted.Should().Be(2, "the batch limit caps the number of rows deleted per pass");
        (await CountDispatchByStatusAsync(2)).Should().Be(3);
    }

    private async Task<long> InsertRuleAndEventAsync()
    {
        long eventId = 0;
        await fixture.RunUnderSchemaMutationLockAsync(async () =>
        {
            await using var connection = await fixture.GetConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO honua.alert_rules (
                    rule_id, service_id, layer_id, rule_name, trigger_type, conditions,
                    cooldown_seconds, severity, edition_required, channels, is_active)
                VALUES (
                    9001, 'svc-a', 1, 'rule-9001', 1, '{}'::jsonb,
                    0, 'warning', 1, '{}'::text[], TRUE)
                ON CONFLICT (rule_id) DO NOTHING;

                INSERT INTO honua.alert_events (
                    dedupe_key, rule_id, service_id, layer_id, objectid, trigger_type, generation,
                    severity, occurred_at, payload, incident_status, incident_duration_ms)
                VALUES (
                    @dedupe_key, 9001, 'svc-a', 1, 1, 1, 1,
                    'warning', now(), '{}'::jsonb, 1, 0)
                RETURNING event_id;
                """;
            command.Parameters.AddWithValue("dedupe_key", Guid.NewGuid().ToString("N"));
            var result = await command.ExecuteScalarAsync();
            eventId = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        });
        return eventId;
    }

    private async Task InsertDispatchAsync(
        long eventId,
        short status,
        DateTimeOffset? deliveredAt,
        DateTimeOffset? createdAt = null,
        short channelType = 1)
    {
        await fixture.ApplyGlobalSeedSqlAsync(
            """
            INSERT INTO honua.alert_dispatch (
                event_id, channel_type, status, attempts, max_attempts, next_attempt_at, delivered_at, created_at)
            VALUES (
                @event_id, @channel_type, @status, 0, 5, now(), @delivered_at, @created_at);
            """,
            command =>
            {
                command.Parameters.AddWithValue("event_id", eventId);
                command.Parameters.AddWithValue("status", status);
                command.Parameters.AddWithValue("channel_type", channelType);
                command.Parameters.AddWithValue("delivered_at", (object?)deliveredAt ?? DBNull.Value);
                command.Parameters.AddWithValue("created_at", (object?)createdAt ?? DateTimeOffset.UtcNow);
            });
    }

    private async Task<long> CountDispatchAsync()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM honua.alert_dispatch;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> CountDispatchByStatusAsync(short status)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM honua.alert_dispatch WHERE status = @status;";
        command.Parameters.AddWithValue("status", status);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task EnsureAlertSchemaAsync()
    {
        await fixture.ApplyGlobalSeedSqlAsync("""
            CREATE SCHEMA IF NOT EXISTS honua;

            CREATE TABLE IF NOT EXISTS honua.alert_rules (
                rule_id BIGSERIAL PRIMARY KEY,
                service_id TEXT NOT NULL,
                layer_id INT NOT NULL,
                zone_id BIGINT NULL,
                rule_name TEXT NOT NULL,
                trigger_type SMALLINT NOT NULL,
                conditions JSONB NOT NULL DEFAULT '{}'::jsonb,
                cooldown_seconds INT NOT NULL DEFAULT 0,
                severity TEXT NOT NULL DEFAULT 'warning',
                edition_required SMALLINT NOT NULL DEFAULT 1,
                channels TEXT[] NOT NULL DEFAULT '{}'::text[],
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS honua.alert_events (
                event_id BIGSERIAL PRIMARY KEY,
                dedupe_key TEXT NOT NULL UNIQUE,
                rule_id BIGINT NULL REFERENCES honua.alert_rules(rule_id) ON DELETE CASCADE,
                zone_id BIGINT NULL,
                service_id TEXT NOT NULL,
                layer_id INT NOT NULL,
                objectid BIGINT NOT NULL,
                trigger_type SMALLINT NOT NULL,
                generation BIGINT NOT NULL,
                severity TEXT NOT NULL,
                occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                payload JSONB NOT NULL DEFAULT '{}'::jsonb,
                incident_status SMALLINT NOT NULL DEFAULT 1,
                incident_duration_ms BIGINT NOT NULL DEFAULT 0,
                source TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS honua.alert_dispatch (
                dispatch_id BIGSERIAL PRIMARY KEY,
                event_id BIGINT NOT NULL REFERENCES honua.alert_events(event_id) ON DELETE CASCADE,
                channel_type SMALLINT NOT NULL,
                destination TEXT,
                status SMALLINT NOT NULL DEFAULT 0,
                attempts INT NOT NULL DEFAULT 0,
                max_attempts INT NOT NULL DEFAULT 5,
                next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                last_attempt_at TIMESTAMPTZ,
                delivered_at TIMESTAMPTZ,
                last_error TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS honua.alert_channel_state (
                channel_type SMALLINT PRIMARY KEY,
                is_paused BOOLEAN NOT NULL DEFAULT FALSE,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """);
    }

    private async Task ClearAlertTablesAsync()
    {
        await fixture.ApplyGlobalSeedSqlAsync("""
            TRUNCATE TABLE
                honua.alert_dispatch,
                honua.alert_channel_state,
                honua.alert_events,
                honua.alert_rules
            RESTART IDENTITY CASCADE;
            """);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
