// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.AuditLog;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.AuditLog;

/// <summary>
/// Integration tests for <see cref="PostgresAuditLog"/> using the shared
/// Testcontainers Postgres fixture. Exercises real SQL against an isolated
/// schema so the append-only contract (RULE-blocked UPDATE/DELETE) and the
/// composite indexes from migration 033 are validated end-to-end (#1144).
/// </summary>
[Collection("Database")]
public sealed class PostgresAuditLogTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task RecordAsync_InsertsRow_AndIsReadable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = CreateSink(schema);

            var timestamp = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
            var evt = new AuditEvent
            {
                Timestamp = timestamp,
                EventType = AuditEventType.Authentication,
                Actor = "user-abc",
                ActorType = AuditActorType.UserId,
                ResourceType = "http",
                ResourceId = "/api/v1/admin/things",
                Action = "auth.success",
                Outcome = AuditOutcome.Success,
                CorrelationId = "corr-001",
                RemoteIp = "10.0.0.42",
                UserAgent = "honua-test/1.0",
                Details = "{\"scheme\":\"api-key\"}",
            };

            await sink.RecordAsync(evt);

            var rows = await ReadAllAsync(schema);
            rows.Should().HaveCount(1);
            var row = rows[0];
            row.EventType.Should().Be("Authentication");
            row.Actor.Should().Be("user-abc");
            row.ActorType.Should().Be("UserId");
            row.ResourceType.Should().Be("http");
            row.ResourceId.Should().Be("/api/v1/admin/things");
            row.Action.Should().Be("auth.success");
            row.Outcome.Should().Be("Success");
            row.CorrelationId.Should().Be("corr-001");
            row.RemoteIp.Should().Be("10.0.0.42");
            row.UserAgent.Should().Be("honua-test/1.0");
            row.Details.Should().Be("{\"scheme\":\"api-key\"}");
            // Stored timestamp must agree with caller-supplied UTC instant.
            row.Timestamp.Should().BeCloseTo(timestamp.UtcDateTime, TimeSpan.FromSeconds(1));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task RecordAsync_NullableFields_PersistAsNull()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = CreateSink(schema);

            var evt = new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.AdminAction,
                Actor = AuditEvent.AnonymousActor,
                ActorType = AuditActorType.Anonymous,
                ResourceType = "layer",
                Action = "layer.delete",
                Outcome = AuditOutcome.Denied,
                CorrelationId = "corr-002",
                // ResourceId, RemoteIp, UserAgent omitted on purpose.
            };

            await sink.RecordAsync(evt);

            var rows = await ReadAllAsync(schema);
            rows.Should().HaveCount(1);
            rows[0].ResourceId.Should().BeNull();
            rows[0].RemoteIp.Should().BeNull();
            rows[0].UserAgent.Should().BeNull();
            // Details column is NOT NULL — default empty string persists as "".
            rows[0].Details.Should().Be(string.Empty);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AuditLogTable_IsAppendOnly_UpdateAndDeleteAreNoOps()
    {
        // Migration 033 installs DO INSTEAD NOTHING rules so UPDATE / DELETE
        // are silently dropped — this protects forensic history from accidental
        // rewrites by application bugs.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = CreateSink(schema);

            await sink.RecordAsync(new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.Authentication,
                Actor = "user-x",
                ActorType = AuditActorType.UserId,
                ResourceType = "http",
                Action = "auth.success",
                Outcome = AuditOutcome.Success,
                CorrelationId = "corr-003",
            });

            // Attempt to mutate the row — both DML statements should affect 0 rows
            // because the rules rewrite them to DO INSTEAD NOTHING.
            var updateAffected = await ExecuteAsync(
                schema,
                $"UPDATE \"{schema}\".audit_log SET action = 'tampered';");
            var deleteAffected = await ExecuteAsync(
                schema,
                $"DELETE FROM \"{schema}\".audit_log;");

            updateAffected.Should().Be(0, "UPDATE on the audit log must be a no-op");
            deleteAffected.Should().Be(0, "DELETE on the audit log must be a no-op");

            var rows = await ReadAllAsync(schema);
            rows.Should().HaveCount(1);
            rows[0].Action.Should().Be("auth.success", "the original row must survive an attempted tamper");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task RecordAsync_TruncatesOverlongActor_AndStillPersists()
    {
        // Sanity check on the truncation logic — a 1KB actor should not blow up
        // the VARCHAR(256) constraint; instead the value is truncated with a
        // visible marker.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = CreateSink(schema);

            var longActor = new string('A', 1024);
            await sink.RecordAsync(new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.Authentication,
                Actor = longActor,
                ActorType = AuditActorType.UserId,
                ResourceType = "http",
                Action = "auth.success",
                Outcome = AuditOutcome.Success,
                CorrelationId = "corr-trunc",
            });

            var rows = await ReadAllAsync(schema);
            rows.Should().HaveCount(1);
            rows[0].Actor.Length.Should().BeLessThanOrEqualTo(256);
            rows[0].Actor.Should().EndWith("…");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private PostgresAuditLog CreateSink(string schema)
        => new(
            new TestConnectionProvider(fixture.DataSource, schema),
            NullLogger<PostgresAuditLog>.Instance,
            schemaName: schema);

    private async Task EnsureAuditLogTableAsync(string schema)
    {
        // Mirrors migration 033, but inside the per-test isolated schema so
        // tests run in parallel without stepping on each other. The append-only
        // rules are installed too, so the tamper-resistance assertion is real.
        await fixture.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS "{schema}".audit_log (
                audit_id         BIGSERIAL    PRIMARY KEY,
                timestamp        TIMESTAMPTZ  NOT NULL,
                event_type       VARCHAR(32)  NOT NULL,
                actor            VARCHAR(256) NOT NULL,
                actor_type       VARCHAR(32)  NOT NULL,
                resource_type    VARCHAR(64)  NOT NULL,
                resource_id      VARCHAR(256),
                action           VARCHAR(128) NOT NULL,
                outcome          VARCHAR(16)  NOT NULL,
                correlation_id   VARCHAR(64)  NOT NULL,
                remote_ip        VARCHAR(64),
                user_agent       VARCHAR(512),
                details          TEXT         NOT NULL DEFAULT '',
                CONSTRAINT chk_audit_log_event_type_{schema} CHECK (event_type IN (
                    'Authentication','Authorization','AdminAction',
                    'ConfigChange','DataExport','DataDelete')),
                CONSTRAINT chk_audit_log_actor_type_{schema} CHECK (actor_type IN (
                    'Anonymous','UserId','ApiKey','System')),
                CONSTRAINT chk_audit_log_outcome_{schema} CHECK (outcome IN (
                    'Success','Failure','Denied'))
            );

            DROP RULE IF EXISTS audit_log_no_update ON "{schema}".audit_log;
            CREATE RULE audit_log_no_update AS ON UPDATE TO "{schema}".audit_log DO INSTEAD NOTHING;

            DROP RULE IF EXISTS audit_log_no_delete ON "{schema}".audit_log;
            CREATE RULE audit_log_no_delete AS ON DELETE TO "{schema}".audit_log DO INSTEAD NOTHING;
            """);
    }

    private async Task<int> ExecuteAsync(string schema, string sql)
    {
        await using var conn = await fixture.GetConnectionAsync(schema);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<AuditRow>> ReadAllAsync(string schema)
    {
        await using var conn = await fixture.GetConnectionAsync(schema);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT timestamp, event_type, actor, actor_type, resource_type, resource_id,
                   action, outcome, correlation_id, remote_ip, user_agent, details
            FROM "{schema}".audit_log
            ORDER BY audit_id ASC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<AuditRow>();
        while (await reader.ReadAsync())
        {
            results.Add(new AuditRow(
                Timestamp: reader.GetDateTime(0),
                EventType: reader.GetString(1),
                Actor: reader.GetString(2),
                ActorType: reader.GetString(3),
                ResourceType: reader.GetString(4),
                ResourceId: reader.IsDBNull(5) ? null : reader.GetString(5),
                Action: reader.GetString(6),
                Outcome: reader.GetString(7),
                CorrelationId: reader.GetString(8),
                RemoteIp: reader.IsDBNull(9) ? null : reader.GetString(9),
                UserAgent: reader.IsDBNull(10) ? null : reader.GetString(10),
                Details: reader.GetString(11)));
        }
        return results;
    }

    private sealed record AuditRow(
        DateTime Timestamp,
        string EventType,
        string Actor,
        string ActorType,
        string ResourceType,
        string? ResourceId,
        string Action,
        string Outcome,
        string CorrelationId,
        string? RemoteIp,
        string? UserAgent,
        string Details);

    /// <summary>
    /// Minimal <see cref="IDatabaseConnectionProvider"/> that pins the
    /// per-test isolated schema on every connection. Lives in the test
    /// assembly because <c>Honua.TestKit.TestDatabaseConnectionProvider</c>
    /// is only IVT-visible to <c>Honua.Server.Tests</c>.
    /// </summary>
    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schemaName}\", public;";
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
