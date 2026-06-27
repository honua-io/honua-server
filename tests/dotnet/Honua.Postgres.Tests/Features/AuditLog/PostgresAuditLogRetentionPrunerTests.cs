// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog.Export;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.AuditLog;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.AuditLog;

/// <summary>
/// Integration tests for the configurable audit retention pruner (#509).
/// Exercises real SQL against an isolated schema with the append-only rules
/// installed, asserting that pruning removes expired head rows, retains recent
/// rows, and leaves the tamper-evident hash chain verifiable.
/// </summary>
[Collection("Database")]
public sealed class PostgresAuditLogRetentionPrunerTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task PruneAsync_RemovesExpiredHeadRows_RetainsRecent_AndChainStillVerifies()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogRetentionPrunerTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(Provider(schema), NullLogger<PostgresAuditLog>.Instance, schema);

            var now = DateTimeOffset.UtcNow;
            // Insert oldest-first so audit_id order matches timestamp order.
            await sink.RecordAsync(Event("old-0") with { Timestamp = now.AddDays(-120) });
            await sink.RecordAsync(Event("old-1") with { Timestamp = now.AddDays(-100) });
            await sink.RecordAsync(Event("recent-0") with { Timestamp = now.AddDays(-10) });
            await sink.RecordAsync(Event("recent-1") with { Timestamp = now.AddDays(-1) });

            var pruner = new PostgresAuditLogRetentionPruner(
                Provider(schema), NullLogger<PostgresAuditLogRetentionPruner>.Instance, schema);

            var removed = await pruner.PruneAsync(
                new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromDays(90) },
                CancellationToken.None);

            removed.Should().Be(2, "the two records older than 90 days are expired");

            var survivors = await ReadCorrelationIdsAsync(schema);
            survivors.Should().ContainInOrder("recent-0", "recent-1");
            survivors.Should().NotContain(new[] { "old-0", "old-1" });

            // Pruning a contiguous head prefix must not break the hash chain: the
            // first surviving row becomes the new genesis (its prev_hash is not
            // checked), so verification still passes.
            var report = await new PostgresAuditLogIntegrityVerifier(Provider(schema), schema).VerifyAsync();
            report.Verified.Should().BeTrue("a head-prefix prune preserves chain integrity");
            report.RowsChecked.Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PruneAsync_UnboundedPolicy_RemovesNothing()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogRetentionPrunerTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(Provider(schema), NullLogger<PostgresAuditLog>.Instance, schema);
            await sink.RecordAsync(Event("a") with { Timestamp = DateTimeOffset.UtcNow.AddYears(-5) });
            await sink.RecordAsync(Event("b") with { Timestamp = DateTimeOffset.UtcNow.AddYears(-5) });

            var pruner = new PostgresAuditLogRetentionPruner(
                Provider(schema), NullLogger<PostgresAuditLogRetentionPruner>.Instance, schema);

            var removed = await pruner.PruneAsync(
                new AuditRetentionPolicy { RetentionWindow = TimeSpan.Zero },
                CancellationToken.None);

            removed.Should().Be(0, "an unbounded policy retains forever");
            (await ReadCorrelationIdsAsync(schema)).Should().HaveCount(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PruneAsync_AllRowsExpired_ClearsTable_AndRestoresAppendOnlyGuard()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogRetentionPrunerTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(Provider(schema), NullLogger<PostgresAuditLog>.Instance, schema);
            await sink.RecordAsync(Event("a") with { Timestamp = DateTimeOffset.UtcNow.AddDays(-200) });
            await sink.RecordAsync(Event("b") with { Timestamp = DateTimeOffset.UtcNow.AddDays(-150) });

            var pruner = new PostgresAuditLogRetentionPruner(
                Provider(schema), NullLogger<PostgresAuditLogRetentionPruner>.Instance, schema);

            var removed = await pruner.PruneAsync(
                new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromDays(90) },
                CancellationToken.None);

            removed.Should().Be(2);
            (await ReadCorrelationIdsAsync(schema)).Should().BeEmpty();

            // The append-only DELETE guard must be reinstated after the prune so a
            // subsequent ordinary delete is once again a no-op.
            await sink.RecordAsync(Event("c") with { Timestamp = DateTimeOffset.UtcNow });
            await ExecuteAsync(schema, $"""DELETE FROM "{schema}".audit_log;""");
            (await ReadCorrelationIdsAsync(schema)).Should().ContainSingle().Which.Should().Be("c");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static AuditEvent Event(string correlationId) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        EventType = AuditEventType.Authentication,
        Actor = "user-1",
        ActorType = AuditActorType.UserId,
        ResourceType = "http",
        ResourceId = "/api/v1/admin/x",
        Action = "auth.success",
        Outcome = AuditOutcome.Success,
        CorrelationId = correlationId,
        RemoteIp = "10.0.0.1",
        UserAgent = "agent/1.0",
        Details = "{}",
    };

    private TestConnectionProvider Provider(string schema) => new(fixture.DataSource, schema);

    private async Task<List<string>> ReadCorrelationIdsAsync(string schema)
    {
        await using var conn = await fixture.GetConnectionAsync(schema);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""SELECT correlation_id FROM "{schema}".audit_log ORDER BY audit_id ASC;""";
        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<string>();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var conn = await fixture.GetConnectionAsync(schema);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
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
                prev_hash        CHAR(64),
                entry_hash       CHAR(64)
            );

            DROP RULE IF EXISTS audit_log_no_update ON "{schema}".audit_log;
            CREATE RULE audit_log_no_update AS ON UPDATE TO "{schema}".audit_log DO INSTEAD NOTHING;

            DROP RULE IF EXISTS audit_log_no_delete ON "{schema}".audit_log;
            CREATE RULE audit_log_no_delete AS ON DELETE TO "{schema}".audit_log DO INSTEAD NOTHING;
            """);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IAdoNetDatabaseConnectionProvider
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
