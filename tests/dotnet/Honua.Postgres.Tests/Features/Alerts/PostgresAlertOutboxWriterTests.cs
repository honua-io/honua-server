// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Alerts;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Alerts;

/// <summary>
/// Integration tests for <see cref="PostgresAlertOutboxWriter"/> — the atomic append-event +
/// enqueue-dispatch write that closes the alert-loss window (#A11b-1). The event append and its
/// per-channel dispatch enqueue must commit together on one connection/transaction so a crash can
/// never persist an event with no delivery enqueued (or vice versa).
/// </summary>
[Collection("Database")]
public sealed class PostgresAlertOutboxWriterTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task AppendAndEnqueueAsync_PersistsEventAndDispatchTogether()
    {
        await EnsureAlertSchemaAsync();
        await ClearAlertTablesAsync();
        await InsertRuleAsync();

        var writer = new PostgresAlertOutboxWriter(new TestConnectionProvider(fixture.DataSource));
        var envelope = Envelope("dedupe-atomic-1");

        var eventId = await writer.AppendAndEnqueueAsync(
            envelope,
            ImmutableArray.Create(AlertChannelType.Webhook, AlertChannelType.Slack));

        eventId.Should().NotBeNull("the event was newly appended");
        var appendedEventId = eventId is { } id ? id : throw new InvalidOperationException("Expected a non-null event id.");
        (await CountEventsAsync(appendedEventId)).Should().Be(1, "the event row is committed");
        (await CountDispatchForEventAsync(appendedEventId)).Should().Be(2, "one dispatch row per deliverable channel is committed in the same transaction");
    }

    [IntegrationTest]
    public async Task AppendAndEnqueueAsync_WhenDeduplicated_ReturnsNullAndEnqueuesNothing()
    {
        await EnsureAlertSchemaAsync();
        await ClearAlertTablesAsync();
        await InsertRuleAsync();

        var writer = new PostgresAlertOutboxWriter(new TestConnectionProvider(fixture.DataSource));
        var first = await writer.AppendAndEnqueueAsync(Envelope("dedupe-dup"), ImmutableArray.Create(AlertChannelType.Webhook));
        first.Should().NotBeNull();

        // Same dedupe key: the second write must be a no-op that enqueues no additional dispatch.
        var second = await writer.AppendAndEnqueueAsync(Envelope("dedupe-dup"), ImmutableArray.Create(AlertChannelType.Webhook));

        second.Should().BeNull("the event was already appended");
        (await CountDispatchForEventAsync(first!.Value)).Should().Be(1, "the duplicate write enqueues no new dispatch row");
    }

    private static AlertEventEnvelope Envelope(string dedupeKey)
        => new()
        {
            DedupeKey = dedupeKey,
            RuleId = 9101,
            ServiceId = "svc-a",
            LayerId = 1,
            ObjectId = 1,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 1,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{}",
            IncidentStatus = AlertIncidentStatus.Started,
            IncidentDurationMs = 0,
        };

    private async Task InsertRuleAsync()
        => await fixture.ApplyGlobalSeedSqlAsync(
            """
            INSERT INTO honua.alert_rules (
                rule_id, service_id, layer_id, rule_name, trigger_type, conditions,
                cooldown_seconds, severity, edition_required, channels, is_active)
            VALUES (
                9101, 'svc-a', 1, 'rule-9101', 1, '{}'::jsonb,
                0, 'warning', 1, '{}'::text[], TRUE)
            ON CONFLICT (rule_id) DO NOTHING;
            """);

    private async Task<long> CountEventsAsync(long eventId)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM honua.alert_events WHERE event_id = @event_id;";
        command.Parameters.AddWithValue("event_id", eventId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> CountDispatchForEventAsync(long eventId)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM honua.alert_dispatch WHERE event_id = @event_id;";
        command.Parameters.AddWithValue("event_id", eventId);
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
            """);
    }

    private async Task ClearAlertTablesAsync()
    {
        await fixture.ApplyGlobalSeedSqlAsync("""
            TRUNCATE TABLE
                honua.alert_dispatch,
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
