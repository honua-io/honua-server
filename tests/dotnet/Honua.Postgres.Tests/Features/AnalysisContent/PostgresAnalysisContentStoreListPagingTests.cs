// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.AnalysisContent;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.AnalysisContent;

/// <summary>
/// Postgres integration tests for <see cref="PostgresAnalysisContentStore"/> list paging
/// (honua-server#1237 post-merge Codex P2): the SQL used <c>COUNT(*) OVER ()</c>, which only
/// materialises on returned rows. When the requested offset is past the last matching row the
/// page is empty and the window count never appears, leaving <c>TotalCount = 0</c> and diverging
/// from the in-memory store (<c>InMemoryAnalysisContentStore</c>), which always reports the full
/// filtered total. These tests assert TotalCount reflects the true filtered total on a populated
/// page AND on an empty (past-the-end) page.
/// </summary>
[Collection("Database")]
public sealed class PostgresAnalysisContentStoreListPagingTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task ListItemsAsync_OffsetPastLastRow_ReturnsEmptyPageWithTrueTotalCount()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAnalysisContentStoreListPagingTests));
        try
        {
            await EnsureTableAsync(schema);
            var store = new PostgresAnalysisContentStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            // Seed three Active SavedQuery items.
            const int seeded = 3;
            for (var i = 0; i < seeded; i++)
            {
                await store.CreateItemAsync(BuildItem($"item-{i}"), BuildVersion($"item-{i}"));
            }

            // Sanity: a first page returns the rows and the full total.
            var firstPage = await store.ListItemsAsync(new AnalysisContentItemQuery { Limit = 10, Offset = 0 });
            firstPage.Items.Should().HaveCount(seeded);
            firstPage.TotalCount.Should().Be(seeded);

            // Offset past the last matching row: empty page, but TotalCount must still be the
            // true filtered total (this is the regression — it previously returned 0).
            var emptyPage = await store.ListItemsAsync(new AnalysisContentItemQuery { Limit = 10, Offset = seeded + 5 });
            emptyPage.Items.Should().BeEmpty();
            emptyPage.TotalCount.Should().Be(seeded,
                "an empty past-the-end page must still report the full filtered total (matching InMemoryAnalysisContentStore)");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListItemsAsync_LifecycleFilter_EmptyPageTotalCountReflectsFilteredTotal()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAnalysisContentStoreListPagingTests) + "Lifecycle");
        try
        {
            await EnsureTableAsync(schema);
            var store = new PostgresAnalysisContentStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            // Two Active + one Archived; the default (Active) filter must count only the two
            // active items, including on an empty past-the-end page.
            await store.CreateItemAsync(BuildItem("active-1"), BuildVersion("active-1"));
            await store.CreateItemAsync(BuildItem("active-2"), BuildVersion("active-2"));
            await store.CreateItemAsync(
                BuildItem("archived-1", AnalysisContentLifecycle.Archived),
                BuildVersion("archived-1"));

            var emptyPage = await store.ListItemsAsync(new AnalysisContentItemQuery
            {
                Lifecycle = AnalysisContentLifecycle.Active,
                Limit = 10,
                Offset = 50
            });

            emptyPage.Items.Should().BeEmpty();
            emptyPage.TotalCount.Should().Be(2,
                "the filtered total must count only matching (Active) items even on an empty page");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static AnalysisContentItem BuildItem(
        string itemId,
        AnalysisContentLifecycle lifecycle = AnalysisContentLifecycle.Active)
        => new()
        {
            ItemId = itemId,
            Kind = AnalysisContentKind.SavedQuery,
            Name = itemId,
            Title = itemId,
            CurrentVersion = 1,
            CurrentVersionId = $"{itemId}-v1",
            Lifecycle = lifecycle,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static AnalysisContentVersion BuildVersion(string itemId)
        => new()
        {
            VersionId = $"{itemId}-v1",
            ItemId = itemId,
            Version = 1,
            Kind = AnalysisContentKind.SavedQuery,
            SavedQuery = new SavedQueryContent { LayerId = 1 },
            ContentHash = $"{itemId}-hash",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private async Task EnsureTableAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS "{schema}".analysis_content_items (
                item_id            TEXT        PRIMARY KEY,
                kind               TEXT        NOT NULL,
                name               TEXT        NOT NULL,
                title              TEXT        NULL,
                owner_id           TEXT        NULL,
                visibility         TEXT        NOT NULL DEFAULT 'organization',
                current_version    INT         NOT NULL,
                current_version_id TEXT        NOT NULL,
                lifecycle          TEXT        NOT NULL DEFAULT 'Active',
                created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                created_by         TEXT        NULL
            );

            CREATE TABLE IF NOT EXISTS "{schema}".analysis_content_versions (
                version_id    TEXT        PRIMARY KEY,
                item_id       TEXT        NOT NULL REFERENCES "{schema}".analysis_content_items(item_id) ON DELETE CASCADE,
                version       INT         NOT NULL,
                kind          TEXT        NOT NULL,
                content_hash  TEXT        NOT NULL,
                created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                created_by    TEXT        NULL,
                version_body  JSONB       NOT NULL,
                CONSTRAINT analysis_content_versions_item_version UNIQUE (item_id, version)
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

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

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}
