// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.AuditLog;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.AuditLog;

/// <summary>
/// Integration tests for <see cref="PostgresAuditLogReader"/>. Mirrors
/// migration 033 inside an isolated per-test schema and exercises filter +
/// cursor pagination semantics introduced in #1168.
/// </summary>
[Collection("Database")]
public sealed class PostgresAuditLogReaderTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogReaderTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(new TestConnectionProvider(fixture.DataSource, schema),
                NullLogger<PostgresAuditLog>.Instance, schemaName: schema);

            await sink.RecordAsync(BuildEvent(new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero), "first"));
            await sink.RecordAsync(BuildEvent(new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.Zero), "second"));

            var reader = new PostgresAuditLogReader(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var page = await reader.ListAsync(new AuditLogFilter { PageSize = 10 });

            page.Items.Should().HaveCount(2);
            page.Items[0].CorrelationId.Should().Be("second");
            page.Items[1].CorrelationId.Should().Be("first");
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
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogReaderTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(new TestConnectionProvider(fixture.DataSource, schema),
                NullLogger<PostgresAuditLog>.Instance, schemaName: schema);

            for (var i = 0; i < 5; i++)
            {
                await sink.RecordAsync(BuildEvent(new DateTimeOffset(2026, 5, 20, 8, 0, i, TimeSpan.Zero), $"corr-{i}"));
            }

            var reader = new PostgresAuditLogReader(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var first = await reader.ListAsync(new AuditLogFilter { PageSize = 2 });
            first.Items.Should().HaveCount(2);
            first.NextCursor.Should().NotBeNullOrEmpty();

            var second = await reader.ListAsync(new AuditLogFilter { PageSize = 2, Cursor = first.NextCursor });
            second.Items.Should().HaveCount(2);
            var firstIds = first.Items.Select(item => item.AuditId).ToArray();
            second.Items.Select(item => item.AuditId).Should().NotIntersectWith(firstIds);

            var third = await reader.ListAsync(new AuditLogFilter { PageSize = 2, Cursor = second.NextCursor });
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
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogReaderTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(new TestConnectionProvider(fixture.DataSource, schema),
                NullLogger<PostgresAuditLog>.Instance, schemaName: schema);
            await sink.RecordAsync(BuildEvent(new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero), "corr"));

            var reader = new PostgresAuditLogReader(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var page = await reader.ListAsync(new AuditLogFilter
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
    public async Task ListAsync_FiltersByActorAndAction()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogReaderTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(new TestConnectionProvider(fixture.DataSource, schema),
                NullLogger<PostgresAuditLog>.Instance, schemaName: schema);

            await sink.RecordAsync(BuildEvent(DateTimeOffset.UtcNow, "corr-a", actor: "alice", action: "alert.acknowledge"));
            await sink.RecordAsync(BuildEvent(DateTimeOffset.UtcNow, "corr-b", actor: "bob", action: "alert.resolve"));
            await sink.RecordAsync(BuildEvent(DateTimeOffset.UtcNow, "corr-c", actor: "alice", action: "alert.resolve"));

            var reader = new PostgresAuditLogReader(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var byActor = await reader.ListAsync(new AuditLogFilter { Actor = "alice", PageSize = 10 });
            byActor.Items.Should().HaveCount(2);
            byActor.Items.Should().OnlyContain(item => item.Actor == "alice");

            var byAction = await reader.ListAsync(new AuditLogFilter { Action = "alert.resolve", PageSize = 10 });
            byAction.Items.Should().HaveCount(2);
            byAction.Items.Should().OnlyContain(item => item.Action == "alert.resolve");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static string BuildOutOfRangeCursor(string suffix)
        => Base64Url.Encode("9223372036854775807:" + suffix);

    private static AuditEvent BuildEvent(DateTimeOffset timestamp, string correlation,
        string actor = "operator", string action = "test.action")
    {
        return new AuditEvent
        {
            Timestamp = timestamp,
            EventType = AuditEventType.AdminAction,
            Actor = actor,
            ActorType = AuditActorType.UserId,
            ResourceType = "alert_event",
            ResourceId = "1",
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = correlation
        };
    }

    private async Task EnsureAuditLogTableAsync(string schema)
    {
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

            CREATE INDEX IF NOT EXISTS idx_audit_log_timestamp_id_{schema}
                ON "{schema}".audit_log (timestamp DESC, audit_id DESC);
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
