// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Postgres.Features.Observability;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Observability;

/// <summary>
/// Integration tests for <see cref="PostgresInvestigationStore"/> (#1168). Mirrors
/// migration 034 inside an isolated per-test schema.
/// </summary>
[Collection("Database")]
public sealed class PostgresInvestigationStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task CreateAndGet_RoundTripsRecord()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresInvestigationStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresInvestigationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var created = await store.CreateAsync("disk pressure", "alice", "investigating", DateTimeOffset.UtcNow);
            created.InvestigationId.Should().StartWith("inv_");

            var loaded = await store.GetAsync(created.InvestigationId);
            loaded.Should().NotBeNull();
            loaded!.Title.Should().Be("disk pressure");
            loaded.Status.Should().Be(InvestigationStatus.Open);
            loaded.Pins.Should().BeEmpty();
            loaded.Links.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AddPin_AppendsPinAndTouchesUpdatedAt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresInvestigationStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresInvestigationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var created = await store.CreateAsync("network", "alice", null, DateTimeOffset.UtcNow.AddMinutes(-5));
            var pinned = await store.AddPinAsync(
                created.InvestigationId,
                "alert:42",
                OperateEventKind.Alert,
                DateTimeOffset.UtcNow,
                note: "looks related",
                actor: "bob",
                createdAt: DateTimeOffset.UtcNow);

            pinned.Should().NotBeNull();
            pinned!.Pins.Should().HaveCount(1);
            pinned.Pins[0].EventRef.Should().Be("alert:42");
            pinned.Pins[0].EventKind.Should().Be(OperateEventKind.Alert);
            pinned.UpdatedAt.Should().BeAfter(created.UpdatedAt);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AddLink_ThenRemoveLink_BothReflected()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresInvestigationStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresInvestigationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var created = await store.CreateAsync("incident", "alice", null, DateTimeOffset.UtcNow);
            var linked = await store.AddLinkAsync(created.InvestigationId, InvestigationResourceKind.Release,
                "2026.05.21", note: null, actor: "alice", createdAt: DateTimeOffset.UtcNow);
            linked.Should().NotBeNull();
            linked!.Links.Should().HaveCount(1);
            var linkId = linked.Links[0].LinkId;

            var afterRemove = await store.RemoveLinkAsync(created.InvestigationId, linkId, DateTimeOffset.UtcNow);
            afterRemove.Should().NotBeNull();
            afterRemove!.Links.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task UpdateAsync_PersistsStatusTransition()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresInvestigationStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresInvestigationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var created = await store.CreateAsync("temp incident", "alice", null, DateTimeOffset.UtcNow);
            var updated = await store.UpdateAsync(created.InvestigationId, title: null, summary: "all clear",
                status: InvestigationStatus.Closed, updatedAt: DateTimeOffset.UtcNow);

            updated.Should().NotBeNull();
            updated!.Status.Should().Be(InvestigationStatus.Closed);
            updated.Summary.Should().Be("all clear");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListAsync_FiltersAndPaginates()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresInvestigationStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresInvestigationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            for (var i = 0; i < 4; i++)
            {
                await store.CreateAsync($"inv {i}", "alice", null, DateTimeOffset.UtcNow.AddSeconds(i));
            }

            var first = await store.ListAsync(new InvestigationFilter { PageSize = 2 });
            first.Items.Should().HaveCount(2);
            first.NextCursor.Should().NotBeNull();

            var second = await store.ListAsync(new InvestigationFilter { PageSize = 2, Cursor = first.NextCursor });
            second.Items.Should().HaveCount(2);
            second.NextCursor.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task EnsureSchemaAsync(string schema)
    {
        await fixture.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS "{schema}".investigations (
                investigation_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                status SMALLINT NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                created_by TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                summary TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "{schema}".investigation_pins (
                pin_id BIGSERIAL PRIMARY KEY,
                investigation_id TEXT NOT NULL REFERENCES "{schema}".investigations(investigation_id) ON DELETE CASCADE,
                event_ref TEXT NOT NULL,
                event_kind SMALLINT NOT NULL,
                occurred_at TIMESTAMPTZ NOT NULL,
                note TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                created_by TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "{schema}".investigation_links (
                link_id BIGSERIAL PRIMARY KEY,
                investigation_id TEXT NOT NULL REFERENCES "{schema}".investigations(investigation_id) ON DELETE CASCADE,
                resource_kind SMALLINT NOT NULL,
                resource_id TEXT NOT NULL,
                note TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                created_by TEXT NOT NULL
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
