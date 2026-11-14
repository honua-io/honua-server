// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTest]
    public Task ActivationWatch_TwoHosts_RollBackOnceWithForwardRevision() => WithWatchSchemaAsync(async schema =>
    {
        var first = WatchStore(schema);
        var second = WatchStore(schema);
        var original = await first.SaveAsync(WatchGraph("original"), null);
        var candidate = await first.SaveAsync(WatchGraph("candidate"), original.Etag);
        var watch = (await first.ReadPendingAsync()).Should().ContainSingle().Which;
        var results = await Task.WhenAll(
            first.TryRollbackAsync(new(watch.Id, "Serving errors increased")),
            second.TryRollbackAsync(new(watch.Id, "Serving errors increased")));
        results.Count(value => value).Should().Be(1);
        var restored = await second.GetCurrentAsync();
        restored.Revision.Should().BeGreaterThan(candidate.Revision);
        restored.Graph.Resources.Single().Metadata.Name.Should().Be("original");
        var pending = (await second.ReadPendingAsync()).Should().ContainSingle().Which;
        pending.RollbackRevision.Should().Be(restored.Revision);
        pending.ChangeId.Should().Be($"catalog-watch-{watch.Id}");
    });

    [IntegrationTest]
    public Task ActivationWatch_InterveningAdminWrite_IsNeverOverwritten() => WithWatchSchemaAsync(async schema =>
    {
        var first = WatchStore(schema);
        var second = WatchStore(schema);
        var original = await first.SaveAsync(WatchGraph("original"), null);
        var candidate = await first.SaveAsync(WatchGraph("candidate"), original.Etag);
        var oldWatch = (await first.ReadPendingAsync()).Single();
        var newer = await second.SaveAsync(WatchGraph("newer"), candidate.Etag);
        (await first.TryRollbackAsync(new(oldWatch.Id, "Serving errors increased"))).Should().BeFalse();
        (await first.GetCurrentAsync()).Etag.Should().Be(newer.Etag);
    });

    [IntegrationTest]
    public Task ActivationWatch_Restart_RetainsPurgeUntilAcknowledged() => WithWatchSchemaAsync(async schema =>
    {
        var store = WatchStore(schema);
        var original = await store.SaveAsync(WatchGraph("original"), null);
        await store.SaveAsync(WatchGraph("candidate"), original.Etag);
        var watch = (await store.ReadPendingAsync()).Single();
        (await store.TryRollbackAsync(new(watch.Id, "Serving errors increased"))).Should().BeTrue();
        var restarted = WatchStore(schema);
        (await restarted.ReadPendingAsync()).Should().ContainSingle().Which.RollbackRevision.Should().NotBeNull();
        await restarted.CompletePurgeAsync(watch.Id);
        (await WatchStore(schema).ReadPendingAsync()).Should().BeEmpty();
        (await WatchStore(schema).ReadRecentRollbacksAsync()).Should().ContainSingle().Which.ChangeId.Should().Be(watch.ChangeId ?? $"catalog-watch-{watch.Id}");
    });

    [IntegrationTest]
    public Task ActivationWatch_OldRollback_LeavesRecentFindingsOnlyWhileItsPurgeIsPending() => WithWatchSchemaAsync(async schema =>
    {
        var store = WatchStore(schema);
        var original = await store.SaveAsync(WatchGraph("original"), null);
        await store.SaveAsync(WatchGraph("candidate"), original.Etag);
        var watch = (await store.ReadPendingAsync()).Single();
        (await store.TryRollbackAsync(new(watch.Id, "Serving errors increased"))).Should().BeTrue();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            UPDATE "{schema}".catalog_activation_watches
               SET activated_at = clock_timestamp() - interval '25 hours'
             WHERE id = @id
            """, connection))
        {
            command.Parameters.AddWithValue("id", watch.Id);
            await command.ExecuteNonQueryAsync();
        }

        (await store.ReadRecentRollbacksAsync()).Should().ContainSingle("an unacknowledged purge stays visible however old");
        await store.CompletePurgeAsync(watch.Id);
        (await store.ReadRecentRollbacksAsync()).Should().BeEmpty();
    });

    [IntegrationTest]
    public Task ActivationWatch_ExpiredOrOtherEnvironment_CannotRollBack() => WithWatchSchemaAsync(async schema =>
    {
        var store = WatchStore(schema);
        var original = await store.SaveAsync(WatchGraph("original"), null);
        var candidate = await store.SaveAsync(WatchGraph("candidate"), original.Etag);
        var watch = (await store.ReadPendingAsync()).Single();
        var other = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
            "Other", FixtureBypassDatabaseSchemaGuard.Instance, schema);
        (await other.ReadPendingAsync()).Should().BeEmpty();
        (await other.TryRollbackAsync(new(watch.Id, "Serving errors increased"))).Should().BeFalse();
        await other.ExpireAsync(watch.Id);
        (await store.ReadPendingAsync()).Should().ContainSingle();
        await store.ExpireAsync(watch.Id);
        (await store.TryRollbackAsync(new(watch.Id, "Serving errors increased"))).Should().BeFalse();
        (await store.GetCurrentAsync()).Etag.Should().Be(candidate.Etag);
    });

    [IntegrationTest]
    public Task ActivationWatch_ElapsedDeadlineWithoutExpiryPass_CannotRollBack() => WithWatchSchemaAsync(async schema =>
    {
        var store = WatchStore(schema);
        var original = await store.SaveAsync(WatchGraph("original"), null);
        var candidate = await store.SaveAsync(WatchGraph("candidate"), original.Etag);
        var watch = (await store.ReadPendingAsync()).Single();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            UPDATE "{schema}".catalog_activation_watches
               SET activated_at = clock_timestamp() - interval '31 minutes'
             WHERE id = @id
            """, connection))
        {
            command.Parameters.AddWithValue("id", watch.Id);
            await command.ExecuteNonQueryAsync();
        }

        (await store.TryRollbackAsync(new(watch.Id, "Serving errors increased"))).Should().BeFalse();
        (await store.GetCurrentAsync()).Etag.Should().Be(candidate.Etag);
        (await store.ReadPendingAsync()).Should().ContainSingle().Which.RollbackRevision.Should().BeNull();
    });

    private PostgresMetadataV2GraphStore WatchStore(string schema) => new(
        new TestConnectionProvider(fixture.DataSource, schema), "Test",
        FixtureBypassDatabaseSchemaGuard.Instance, schema);

    private static MetadataV2Graph WatchGraph(string name) => new()
    {
        Environment = "Test",
        GeneratedAt = DateTimeOffset.UtcNow,
        Resources = [new() { Metadata = new() { Id = "resource", Name = name }, Type = MetadataV2ResourceType.FeatureDataset }],
    };

    private async Task WithWatchSchemaAsync(Func<string, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("activation_watch");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "src", "Honua.Server", "Migrations", "162_CreateCatalogActivationWatches.sql"));
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal), connection);
            await command.ExecuteNonQueryAsync();
            await test(schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
