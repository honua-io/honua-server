// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Alerts;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Alerts;

/// <summary>
/// Integration tests for alert admin health aggregation in <see cref="PostgresAlertAdminStore"/>.
/// </summary>
[Collection("Database")]
public sealed class PostgresAlertAdminStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task GetRuleHealthAsync_UsesCurrentStateForActiveIncidentCount()
    {
        await EnsureAlertSchemaAsync();
        await ClearAlertTablesAsync();

        try
        {
            var startedAt = new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero);
            var resolvedAt = startedAt.AddMinutes(5);

            await InsertRuleAsync(ruleId: 101, triggerType: 1, conditionsJson: "{}");
            await InsertStateAsync(ruleId: 101, objectId: 1, inside: false, thresholdStateJson: "{}");
            await InsertEventAsync(ruleId: 101, triggerType: 1, incidentStatus: 1, occurredAt: startedAt, objectId: 1);

            await InsertRuleAsync(ruleId: 102, triggerType: 1, conditionsJson: "{}");
            await InsertStateAsync(ruleId: 102, objectId: 2, inside: true, thresholdStateJson: "{}");
            await InsertEventAsync(ruleId: 102, triggerType: 1, incidentStatus: 1, occurredAt: startedAt, objectId: 2);

            await InsertRuleAsync(ruleId: 103, triggerType: 4,
                conditionsJson: """{"field":"speedKmh","operator":">","value":30}""");
            await InsertStateAsync(ruleId: 103, objectId: 3, inside: false,
                thresholdStateJson: """{"breached":false}""");
            await InsertEventAsync(ruleId: 103, triggerType: 4, incidentStatus: 1, occurredAt: startedAt, objectId: 3);
            await InsertEventAsync(ruleId: 103, triggerType: 4, incidentStatus: 3, occurredAt: resolvedAt, objectId: 3);

            await InsertRuleAsync(ruleId: 104, triggerType: 4,
                conditionsJson: """{"field":"speedKmh","operator":">","value":30}""");
            await InsertStateAsync(ruleId: 104, objectId: 4, inside: false,
                thresholdStateJson: """{"breached":true}""");
            await InsertEventAsync(ruleId: 104, triggerType: 4, incidentStatus: 1, occurredAt: startedAt, objectId: 4);

            var store = new PostgresAlertAdminStore(new TestConnectionProvider(fixture.DataSource));

            var resolvedEnter = await store.GetRuleHealthAsync(101, recentTriggerLimit: 10);
            var activeEnter = await store.GetRuleHealthAsync(102, recentTriggerLimit: 10);
            var resolvedThreshold = await store.GetRuleHealthAsync(103, recentTriggerLimit: 10);
            var activeThreshold = await store.GetRuleHealthAsync(104, recentTriggerLimit: 10);

            resolvedEnter.Should().NotBeNull();
            resolvedEnter!.ActiveIncidentCount.Should().Be(0);
            activeEnter.Should().NotBeNull();
            activeEnter!.ActiveIncidentCount.Should().Be(1);
            resolvedThreshold.Should().NotBeNull();
            resolvedThreshold!.ActiveIncidentCount.Should().Be(0);
            resolvedThreshold.RecentTriggerCount.Should().Be(2);
            activeThreshold.Should().NotBeNull();
            activeThreshold!.ActiveIncidentCount.Should().Be(1);
        }
        finally
        {
            await ClearAlertTablesAsync();
        }
    }

    private async Task EnsureAlertSchemaAsync()
    {
        await fixture.ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS honua;

            CREATE TABLE IF NOT EXISTS honua.alert_zones (
                zone_id BIGSERIAL PRIMARY KEY,
                service_id TEXT NOT NULL,
                zone_name TEXT NOT NULL,
                geometry GEOMETRY(MULTIPOLYGON),
                metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS honua.alert_rules (
                rule_id BIGSERIAL PRIMARY KEY,
                service_id TEXT NOT NULL,
                layer_id INT NOT NULL,
                zone_id BIGINT NULL REFERENCES honua.alert_zones(zone_id) ON DELETE CASCADE,
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

            CREATE TABLE IF NOT EXISTS honua.alert_state (
                rule_id BIGINT NOT NULL REFERENCES honua.alert_rules(rule_id) ON DELETE CASCADE,
                layer_id INT NOT NULL,
                objectid BIGINT NOT NULL,
                inside BOOLEAN NOT NULL DEFAULT FALSE,
                entered_at TIMESTAMPTZ,
                last_evaluated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                last_alert_at TIMESTAMPTZ,
                last_generation BIGINT NOT NULL DEFAULT 0,
                threshold_state JSONB NOT NULL DEFAULT '{}'::jsonb,
                PRIMARY KEY (rule_id, layer_id, objectid)
            );

            CREATE TABLE IF NOT EXISTS honua.alert_events (
                event_id BIGSERIAL PRIMARY KEY,
                dedupe_key TEXT NOT NULL UNIQUE,
                rule_id BIGINT NOT NULL REFERENCES honua.alert_rules(rule_id) ON DELETE CASCADE,
                zone_id BIGINT NULL REFERENCES honua.alert_zones(zone_id) ON DELETE SET NULL,
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
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS honua.alert_event_lifecycle (
                event_id BIGINT PRIMARY KEY REFERENCES honua.alert_events(event_id) ON DELETE CASCADE,
                lifecycle_status SMALLINT NOT NULL DEFAULT 0,
                acknowledged_at TIMESTAMPTZ NULL,
                acknowledged_by TEXT NULL,
                suppressed_until TIMESTAMPTZ NULL,
                suppressed_by TEXT NULL,
                resolved_at TIMESTAMPTZ NULL,
                resolved_by TEXT NULL,
                note TEXT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
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
        await fixture.ExecuteAsync("""
            TRUNCATE TABLE
                honua.alert_dispatch,
                honua.alert_event_lifecycle,
                honua.alert_events,
                honua.alert_state,
                honua.alert_rules,
                honua.alert_zones
            RESTART IDENTITY CASCADE;
            """);
    }

    private async Task InsertRuleAsync(long ruleId, short triggerType, string conditionsJson)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.alert_rules (
                rule_id, service_id, layer_id, rule_name, trigger_type, conditions,
                cooldown_seconds, severity, edition_required, channels, is_active)
            VALUES (
                @rule_id, 'svc-a', 1, @rule_name, @trigger_type, @conditions::jsonb,
                0, 'warning', 1, '{}'::text[], TRUE);
            """;
        command.Parameters.AddWithValue("rule_id", ruleId);
        command.Parameters.AddWithValue("rule_name", "rule-" + ruleId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("trigger_type", triggerType);
        command.Parameters.AddWithValue("conditions", conditionsJson);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertStateAsync(long ruleId, long objectId, bool inside, string thresholdStateJson)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.alert_state (
                rule_id, layer_id, objectid, inside, entered_at, last_evaluated_at,
                last_alert_at, last_generation, threshold_state)
            VALUES (
                @rule_id, 1, @objectid, @inside, NULL, @last_evaluated_at,
                @last_alert_at, 1, @threshold_state::jsonb);
            """;
        command.Parameters.AddWithValue("rule_id", ruleId);
        command.Parameters.AddWithValue("objectid", objectId);
        command.Parameters.AddWithValue("inside", inside);
        command.Parameters.AddWithValue("last_evaluated_at", new DateTimeOffset(2026, 5, 24, 9, 0, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("last_alert_at", new DateTimeOffset(2026, 5, 24, 9, 1, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("threshold_state", thresholdStateJson);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertEventAsync(
        long ruleId,
        short triggerType,
        short incidentStatus,
        DateTimeOffset occurredAt,
        long objectId)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.alert_events (
                dedupe_key, rule_id, service_id, layer_id, objectid, trigger_type, generation,
                severity, occurred_at, payload, incident_status, incident_duration_ms)
            VALUES (
                @dedupe_key, @rule_id, 'svc-a', 1, @objectid, @trigger_type, 1,
                'warning', @occurred_at, '{}'::jsonb, @incident_status, 0);
            """;
        command.Parameters.AddWithValue("dedupe_key", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("rule_id", ruleId);
        command.Parameters.AddWithValue("objectid", objectId);
        command.Parameters.AddWithValue("trigger_type", triggerType);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue("incident_status", incidentStatus);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
