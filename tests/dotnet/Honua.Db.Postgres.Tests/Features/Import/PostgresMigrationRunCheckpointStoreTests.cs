// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class PostgresMigrationRunCheckpointStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsCheckpoint()
    {
        await EnsureTableAsync();
        var store = CreateStore();
        var runId = $"ckpt-roundtrip-{Guid.NewGuid():N}";
        var checkpoint = NewCheckpoint(runId, phase: "apply", marker: "item-42", completed: 42, attempt: 2);

        await store.SaveAsync(checkpoint);

        var loaded = await store.LoadAsync(runId);
        loaded.Should().NotBeNull();
        loaded!.RunId.Should().Be(runId);
        loaded.Phase.Should().Be("apply");
        loaded.ResumeMarker.Should().Be("item-42");
        loaded.CompletedItemCount.Should().Be(42);
        loaded.Attempt.Should().Be(2);
    }

    [IntegrationTest]
    public async Task LoadAsync_WhenNoCheckpoint_ReturnsNull()
    {
        await EnsureTableAsync();
        var store = CreateStore();

        var loaded = await store.LoadAsync($"ckpt-missing-{Guid.NewGuid():N}");

        loaded.Should().BeNull();
    }

    [IntegrationTest]
    public async Task SaveAsync_IsIdempotentUpsert_KeepingLatestSnapshot()
    {
        await EnsureTableAsync();
        var store = CreateStore();
        var runId = $"ckpt-upsert-{Guid.NewGuid():N}";

        await store.SaveAsync(NewCheckpoint(runId, phase: "scan", marker: "item-1", completed: 1, attempt: 1));
        await store.SaveAsync(NewCheckpoint(runId, phase: "apply", marker: "item-9", completed: 9, attempt: 2));

        var loaded = await store.LoadAsync(runId);
        loaded.Should().NotBeNull();
        loaded!.Phase.Should().Be("apply");
        loaded.ResumeMarker.Should().Be("item-9");
        loaded.CompletedItemCount.Should().Be(9);
        loaded.Attempt.Should().Be(2);
    }

    [IntegrationTest]
    public async Task SaveAsync_RedactsUrlLikeMarker_ViaSanitizer()
    {
        await EnsureTableAsync();
        var store = CreateStore();
        var runId = $"ckpt-redact-{Guid.NewGuid():N}";

        await store.SaveAsync(NewCheckpoint(
            runId,
            phase: "scan",
            marker: "https://user:secret@source.example/api",
            completed: 0,
            attempt: 1));

        var loaded = await store.LoadAsync(runId);
        loaded.Should().NotBeNull();
        loaded!.ResumeMarker.Should().Be(MigrationRunCheckpointSanitizer.RedactedMarker);
    }

    [IntegrationTest]
    public async Task DeleteAsync_RemovesCheckpoint_AndReportsPresence()
    {
        await EnsureTableAsync();
        var store = CreateStore();
        var runId = $"ckpt-delete-{Guid.NewGuid():N}";
        await store.SaveAsync(NewCheckpoint(runId, phase: "apply", marker: "item-3", completed: 3, attempt: 1));

        var firstDelete = await store.DeleteAsync(runId);
        var secondDelete = await store.DeleteAsync(runId);

        firstDelete.Should().BeTrue("the checkpoint existed");
        secondDelete.Should().BeFalse("the checkpoint was already removed");
        (await store.LoadAsync(runId)).Should().BeNull();
    }

    private PostgresMigrationRunCheckpointStore CreateStore()
        => new(new TestConnectionProvider(fixture.DataSource));

    private static MigrationRunCheckpoint NewCheckpoint(
        string runId,
        string phase,
        string marker,
        int completed,
        int attempt) => new()
        {
            RunId = runId,
            Phase = phase,
            ResumeMarker = marker,
            CompletedItemCount = completed,
            CapturedAt = DateTimeOffset.UtcNow,
            Attempt = attempt
        };

    private async Task EnsureTableAsync()
    {
        await fixture.ApplyGlobalSeedSqlAsync("""
            CREATE SCHEMA IF NOT EXISTS honua;
            CREATE TABLE IF NOT EXISTS honua.migration_run_checkpoints (
                run_id      TEXT        PRIMARY KEY,
                checkpoint  JSONB       NOT NULL,
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );
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

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}
