// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Observability;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Observability;

[Collection("Database")]
public sealed class PostgresOpsExternalEventStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task AppendAsync_ConcurrentRedeliveryAndRestart_PreservesOneOriginalEvent()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsExternalEventStoreTests));
        try
        {
            await ApplyMigrationAsync(schema);
            var provider = new TestDatabaseConnectionProvider(fixture.DataSource, () => schema);
            var now = DateTimeOffset.UtcNow;
            var original = new OpsExternalEvent("event-1", now, "{\"original\":true}");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                new PostgresOpsExternalEventStore(provider, schema).AppendAsync(original)));
            var restarted = new PostgresOpsExternalEventStore(provider, schema);
            var replayed = await restarted.AppendAsync(original with { Body = "{\"replacement\":true}", OccurredAt = now.AddHours(1) });
            replayed.Id.Should().Be(original.Id);
            replayed.OccurredAt.Should().BeCloseTo(original.OccurredAt, TimeSpan.FromMilliseconds(1));
            replayed.Body.Should().Contain("original").And.NotContain("replacement");
            var events = await restarted.ReadRecentAsync(now.AddMinutes(-1));
            events.Should().ContainSingle();
            events[0].Id.Should().Be(original.Id);
            events[0].Body.Should().Contain("original").And.NotContain("replacement");
            (await restarted.ReadRecentAsync(now.AddMinutes(1))).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AppendAsync_ExpiredReceipt_PrunesAfterDurableWrite()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsExternalEventStoreTests));
        try
        {
            await ApplyMigrationAsync(schema);
            var store = new PostgresOpsExternalEventStore(new TestDatabaseConnectionProvider(fixture.DataSource, () => schema), schema);
            var now = DateTimeOffset.UtcNow;
            await store.AppendAsync(new OpsExternalEvent("expired", now, "{}"));
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"UPDATE \"{schema}\".ops_external_events SET received_at = now() - interval '31 days'";
                await command.ExecuteNonQueryAsync();
            }

            await store.AppendAsync(new OpsExternalEvent("current", now, "{}"));
            (await store.ReadRecentAsync(now.AddMinutes(-1))).Should().ContainSingle().Which.Id.Should().Be("current");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "161_CreateOpsExternalEvents.sql"));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync();
    }
}
