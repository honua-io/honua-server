// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Alerts;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Alerts;

/// <summary>
/// Integration tests for <see cref="PostgresAlertEventQuery"/> and
/// <see cref="PostgresAlertLifecycleStore"/> using the shared Testcontainers
/// Postgres fixture. Mirrors migration 013 (alert events) and migration 034
/// (alert event lifecycle) inside an isolated per-test schema (#1168).
/// </summary>
[Collection("Database")]
public sealed class PostgresAlertEventQueryTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task ListAsync_ReturnsNewestFirst_AndAttachesLifecycleState()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var (eventId1, _) = await InsertEventAsync(schema, ruleId: 1, severity: "warning",
                occurredAt: new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.Zero));
            var (eventId2, _) = await InsertEventAsync(schema, ruleId: 1, severity: "critical",
                occurredAt: new DateTimeOffset(2026, 5, 20, 9, 5, 0, TimeSpan.Zero));

            var query = new PostgresAlertEventQuery(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var lifecycle = new PostgresAlertLifecycleStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            await lifecycle.AcknowledgeAsync(eventId2, "user-1", "ack", DateTimeOffset.UtcNow);

            var page = await query.ListAsync(new AlertEventFilter { PageSize = 10 });

            page.Items.Should().HaveCount(2);
            page.Items[0].EventId.Should().Be(eventId2);
            page.Items[0].LifecycleStatus.Should().Be(AlertLifecycleStatus.Acknowledged);
            page.Items[0].AcknowledgedBy.Should().Be("user-1");
            page.Items[1].EventId.Should().Be(eventId1);
            page.Items[1].LifecycleStatus.Should().Be(AlertLifecycleStatus.Open);
            page.NextCursor.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListAsync_PaginatesViaCursor()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var baseTime = new DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 5; i++)
            {
                await InsertEventAsync(schema, ruleId: 1, severity: "info", occurredAt: baseTime.AddSeconds(i));
            }

            var query = new PostgresAlertEventQuery(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var first = await query.ListAsync(new AlertEventFilter { PageSize = 2 });
            first.Items.Should().HaveCount(2);
            first.NextCursor.Should().NotBeNullOrEmpty();

            var second = await query.ListAsync(new AlertEventFilter { PageSize = 2, Cursor = first.NextCursor });
            second.Items.Should().HaveCount(2);
            second.Items[0].EventId.Should().BeLessThan(first.Items[1].EventId);
            second.NextCursor.Should().NotBeNullOrEmpty();

            var third = await query.ListAsync(new AlertEventFilter { PageSize = 2, Cursor = second.NextCursor });
            third.Items.Should().HaveCount(1);
            third.NextCursor.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListAsync_WithOutOfRangeCursor_DoesNotThrowAndIgnoresCursor()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            await InsertEventAsync(schema, ruleId: 1, severity: "info",
                occurredAt: new DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.Zero));

            var query = new PostgresAlertEventQuery(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var page = await query.ListAsync(new AlertEventFilter
            {
                PageSize = 10,
                Cursor = BuildOutOfRangeCursor("1")
            });

            page.Items.Should().HaveCount(1);
            page.NextCursor.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListAsync_FiltersByServiceAndSeverity()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var baseTime = new DateTimeOffset(2026, 5, 20, 11, 0, 0, TimeSpan.Zero);
            await InsertEventAsync(schema, ruleId: 1, severity: "info", occurredAt: baseTime, serviceId: "svc-a");
            await InsertEventAsync(schema, ruleId: 1, severity: "critical", occurredAt: baseTime.AddSeconds(1), serviceId: "svc-a");
            await InsertEventAsync(schema, ruleId: 1, severity: "warning", occurredAt: baseTime.AddSeconds(2), serviceId: "svc-b");

            var query = new PostgresAlertEventQuery(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var filtered = await query.ListAsync(new AlertEventFilter
            {
                ServiceId = "svc-a",
                Severities = [AlertSeverity.Critical],
                PageSize = 10
            });

            filtered.Items.Should().HaveCount(1);
            filtered.Items[0].ServiceId.Should().Be("svc-a");
            filtered.Items[0].Severity.Should().Be(AlertSeverity.Critical);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SuppressAsync_PersistsExpiryAndAuditMetadata()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var (eventId, _) = await InsertEventAsync(schema, ruleId: 1, severity: "warning",
                occurredAt: DateTimeOffset.UtcNow);

            var lifecycle = new PostgresAlertLifecycleStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var until = DateTimeOffset.UtcNow.AddHours(1);
            var row = await lifecycle.SuppressAsync(eventId, "user-2", until, "noisy at night", DateTimeOffset.UtcNow);

            row.Should().NotBeNull();
            row!.Status.Should().Be(AlertLifecycleStatus.Suppressed);
            row.SuppressedBy.Should().Be("user-2");
            row.SuppressedUntil.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
            row.Note.Should().Be("noisy at night");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ResolveThenAcknowledge_ClearsResolvedState()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var (eventId, _) = await InsertEventAsync(schema, ruleId: 1, severity: "warning",
                occurredAt: DateTimeOffset.UtcNow);

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var lifecycle = new PostgresAlertLifecycleStore(provider, schema);
            var query = new PostgresAlertEventQuery(provider, schema);

            await lifecycle.ResolveAsync(eventId, "resolver", "done", DateTimeOffset.UtcNow);
            var row = await lifecycle.AcknowledgeAsync(eventId, "ack-user", "checking", DateTimeOffset.UtcNow);
            var summary = await query.GetAsync(eventId);

            row.Should().NotBeNull();
            row!.Status.Should().Be(AlertLifecycleStatus.Acknowledged);
            row.AcknowledgedBy.Should().Be("ack-user");
            row.ResolvedAt.Should().BeNull();
            row.ResolvedBy.Should().BeNull();
            summary.Should().NotBeNull();
            summary!.LifecycleStatus.Should().Be(AlertLifecycleStatus.Acknowledged);
            summary.ResolvedAt.Should().BeNull();
            summary.ResolvedBy.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SuppressThenResolve_ClearsSuppressionState()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var (eventId, _) = await InsertEventAsync(schema, ruleId: 1, severity: "warning",
                occurredAt: DateTimeOffset.UtcNow);

            var lifecycle = new PostgresAlertLifecycleStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            await lifecycle.SuppressAsync(eventId, "suppressor", DateTimeOffset.UtcNow.AddHours(1), "quiet",
                DateTimeOffset.UtcNow);
            var row = await lifecycle.ResolveAsync(eventId, "resolver", "fixed", DateTimeOffset.UtcNow);

            row.Should().NotBeNull();
            row!.Status.Should().Be(AlertLifecycleStatus.Resolved);
            row.ResolvedBy.Should().Be("resolver");
            row.SuppressedUntil.Should().BeNull();
            row.SuppressedBy.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AcknowledgeThenSuppress_ClearsAcknowledgementState()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var (eventId, _) = await InsertEventAsync(schema, ruleId: 1, severity: "warning",
                occurredAt: DateTimeOffset.UtcNow);

            var lifecycle = new PostgresAlertLifecycleStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            await lifecycle.AcknowledgeAsync(eventId, "ack-user", "checking", DateTimeOffset.UtcNow);
            var row = await lifecycle.SuppressAsync(eventId, "suppressor", DateTimeOffset.UtcNow.AddHours(1), "quiet",
                DateTimeOffset.UtcNow);

            row.Should().NotBeNull();
            row!.Status.Should().Be(AlertLifecycleStatus.Suppressed);
            row.SuppressedBy.Should().Be("suppressor");
            row.AcknowledgedAt.Should().BeNull();
            row.AcknowledgedBy.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AcknowledgeAsync_ReturnsNull_WhenEventDoesNotExist()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAlertEventQueryTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var lifecycle = new PostgresAlertLifecycleStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var row = await lifecycle.AcknowledgeAsync(eventId: 9999, actor: "user-3", note: null,
                acknowledgedAt: DateTimeOffset.UtcNow);

            row.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static string BuildOutOfRangeCursor(string suffix)
        => Base64Url.Encode("9223372036854775807:" + suffix);

    private async Task<(long EventId, long RuleId)> InsertEventAsync(
        string schema,
        long ruleId,
        string severity,
        DateTimeOffset occurredAt,
        string serviceId = "svc-a")
    {
        await using var conn = await fixture.GetConnectionAsync(schema);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO "{{schema}}".alert_events (
                dedupe_key, rule_id, service_id, layer_id, objectid, trigger_type, generation,
                severity, occurred_at, payload, incident_status, incident_duration_ms)
            VALUES (@dedupe, @rule, @svc, 1, @oid, 1, 1, @sev, @occurred, '{}'::jsonb, 1, 0)
            RETURNING event_id;
            """;
        cmd.Parameters.AddWithValue("dedupe", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("rule", ruleId);
        cmd.Parameters.AddWithValue("svc", serviceId);
        cmd.Parameters.AddWithValue("oid", 1L);
        cmd.Parameters.AddWithValue("sev", severity);
        cmd.Parameters.AddWithValue("occurred", occurredAt);

        var eventId = (long)(await cmd.ExecuteScalarAsync())!;
        return (eventId, ruleId);
    }

    private async Task EnsureSchemaAsync(string schema)
    {
        await fixture.ExecuteAsync($$"""
            CREATE TABLE IF NOT EXISTS "{{schema}}".alert_events (
                event_id BIGSERIAL PRIMARY KEY,
                dedupe_key TEXT NOT NULL UNIQUE,
                rule_id BIGINT NOT NULL,
                zone_id BIGINT NULL,
                service_id TEXT NOT NULL,
                layer_id INT NOT NULL,
                objectid BIGINT NOT NULL,
                trigger_type SMALLINT NOT NULL,
                generation BIGINT NOT NULL,
                severity TEXT NOT NULL,
                occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                payload JSONB NOT NULL DEFAULT '{}'::jsonb,
                incident_status SMALLINT NOT NULL DEFAULT 1,
                incident_duration_ms BIGINT NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_alert_events_occurred_event_{{schema}}
                ON "{{schema}}".alert_events (occurred_at DESC, event_id DESC);

            CREATE TABLE IF NOT EXISTS "{{schema}}".alert_rules (
                rule_id BIGINT PRIMARY KEY,
                rule_name TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS "{{schema}}".alert_event_lifecycle (
                event_id BIGINT PRIMARY KEY REFERENCES "{{schema}}".alert_events(event_id) ON DELETE CASCADE,
                lifecycle_status SMALLINT NOT NULL DEFAULT 0,
                acknowledged_at TIMESTAMPTZ NULL,
                acknowledged_by TEXT NULL,
                suppressed_until TIMESTAMPTZ NULL,
                suppressed_by TEXT NULL,
                resolved_at TIMESTAMPTZ NULL,
                resolved_by TEXT NULL,
                note TEXT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schema) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schema}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
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
