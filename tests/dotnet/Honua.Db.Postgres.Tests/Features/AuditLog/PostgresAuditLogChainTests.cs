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
/// Integration tests for the tamper-evident hash chain, the integrity verifier,
/// and the SIEM exporter (#350, #509). Exercises real SQL against an isolated
/// schema with the append-only rules installed.
/// </summary>
[Collection("Database")]
public sealed class PostgresAuditLogChainTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task RecordAsync_BuildsLinkedHashChain_AndVerifies()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogChainTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(Provider(schema), NullLogger<PostgresAuditLog>.Instance, schema);

            for (var i = 0; i < 3; i++)
            {
                await sink.RecordAsync(Event($"corr-{i}", $"action.{i}"));
            }

            var rows = await ReadChainAsync(schema);
            rows.Should().HaveCount(3);

            // Genesis row has no predecessor; every later row links to the prior
            // row's entry_hash.
            rows[0].PrevHash.Should().BeNull();
            rows[1].PrevHash.Should().Be(rows[0].EntryHash);
            rows[2].PrevHash.Should().Be(rows[1].EntryHash);
            rows.Select(r => r.EntryHash).Should().OnlyHaveUniqueItems();

            var report = await Verifier(schema).VerifyAsync();
            report.Verified.Should().BeTrue();
            report.RowsChecked.Should().Be(3);
            report.UnhashedRows.Should().Be(0);
            report.FirstBrokenAuditId.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Verify_DetectsTampering_WhenRowMutatedBypassingRules()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogChainTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(Provider(schema), NullLogger<PostgresAuditLog>.Instance, schema);
            await sink.RecordAsync(Event("corr-a", "auth.success"));
            await sink.RecordAsync(Event("corr-b", "auth.success"));

            (await Verifier(schema).VerifyAsync()).Verified.Should().BeTrue();

            // Simulate a privileged tamper that bypassed the append-only rules:
            // drop the rule, mutate the action of the first row, restore the rule.
            await ExecuteAsync(schema, $"""
                DROP RULE audit_log_no_update ON "{schema}".audit_log;
                UPDATE "{schema}".audit_log SET action = 'forged'
                    WHERE audit_id = (SELECT MIN(audit_id) FROM "{schema}".audit_log);
                CREATE RULE audit_log_no_update AS ON UPDATE TO "{schema}".audit_log DO INSTEAD NOTHING;
                """);

            var report = await Verifier(schema).VerifyAsync();
            report.Verified.Should().BeFalse("the entry_hash no longer matches the mutated row");
            report.FirstBrokenAuditId.Should().NotBeNull();
            report.FailureReason.Should().Contain("entry_hash");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Verify_DetectsTampering_WhenRowDeletedBypassingRules()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogChainTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(Provider(schema), NullLogger<PostgresAuditLog>.Instance, schema);
            await sink.RecordAsync(Event("corr-a", "auth.success"));
            await sink.RecordAsync(Event("corr-b", "auth.success"));
            await sink.RecordAsync(Event("corr-c", "auth.success"));

            // Delete the middle row, bypassing the append-only rule.
            await ExecuteAsync(schema, $"""
                DROP RULE audit_log_no_delete ON "{schema}".audit_log;
                DELETE FROM "{schema}".audit_log
                    WHERE audit_id = (SELECT audit_id FROM "{schema}".audit_log ORDER BY audit_id OFFSET 1 LIMIT 1);
                CREATE RULE audit_log_no_delete AS ON DELETE TO "{schema}".audit_log DO INSTEAD NOTHING;
                """);

            var report = await Verifier(schema).VerifyAsync();
            report.Verified.Should().BeFalse("a deleted row breaks the prev_hash chain link");
            report.FailureReason.Should().Contain("prev_hash");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Export_ReturnsTrail_OldestFirst_WithTimeRangeFilter()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogChainTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var sink = new PostgresAuditLog(Provider(schema), NullLogger<PostgresAuditLog>.Instance, schema);

            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await sink.RecordAsync(Event("c0", "a0") with { Timestamp = t0 });
            await sink.RecordAsync(Event("c1", "a1") with { Timestamp = t0.AddHours(1) });
            await sink.RecordAsync(Event("c2", "a2") with { Timestamp = t0.AddHours(2) });

            var exporter = new PostgresAuditLogExporter(Provider(schema), schema);

            var all = await CollectAsync(exporter.ExportAsync(new AuditExportFilter()));
            all.Select(r => r.CorrelationId).Should().ContainInOrder("c0", "c1", "c2");

            // Time-range filter: [t0+1h, t0+2h) returns only the middle row.
            var windowed = await CollectAsync(exporter.ExportAsync(new AuditExportFilter
            {
                From = t0.AddHours(1),
                To = t0.AddHours(2),
            }));
            windowed.Should().ContainSingle().Which.CorrelationId.Should().Be("c1");

            // Action filter narrows to one row.
            var byAction = await CollectAsync(exporter.ExportAsync(new AuditExportFilter { Action = "a2" }));
            byAction.Should().ContainSingle().Which.CorrelationId.Should().Be("c2");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static AuditEvent Event(string correlationId, string action) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        EventType = AuditEventType.Authentication,
        Actor = "user-1",
        ActorType = AuditActorType.UserId,
        ResourceType = "http",
        ResourceId = "/api/v1/admin/x",
        Action = action,
        Outcome = AuditOutcome.Success,
        CorrelationId = correlationId,
        RemoteIp = "10.0.0.1",
        UserAgent = "agent/1.0",
        Details = "{}",
    };

    private TestConnectionProvider Provider(string schema) => new(fixture.DataSource, schema);

    private PostgresAuditLogIntegrityVerifier Verifier(string schema) => new(Provider(schema), schema);

    private static async Task<List<AuditEventRecord>> CollectAsync(IAsyncEnumerable<AuditEventRecord> source)
    {
        var results = new List<AuditEventRecord>();
        await foreach (var record in source)
        {
            results.Add(record);
        }

        return results;
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

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var conn = await fixture.GetConnectionAsync(schema);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<ChainRow>> ReadChainAsync(string schema)
    {
        await using var conn = await fixture.GetConnectionAsync(schema);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT prev_hash, entry_hash FROM "{schema}".audit_log ORDER BY audit_id ASC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<ChainRow>();
        while (await reader.ReadAsync())
        {
            results.Add(new ChainRow(
                PrevHash: reader.IsDBNull(0) ? null : reader.GetString(0).TrimEnd(),
                EntryHash: reader.IsDBNull(1) ? null : reader.GetString(1).TrimEnd()));
        }

        return results;
    }

    private sealed record ChainRow(string? PrevHash, string? EntryHash);

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
