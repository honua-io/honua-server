// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresExecutionLogStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task BoundedLog_TrimsWithAbsoluteCursorsAndDeduplicatesExternalEvents()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.LogStore;
        var id = "log-bounds-" + Guid.NewGuid().ToString("N");

        for (var index = 0; index < ExecutionLogEntryBounds.MaxStoredEntries + 3; index++)
        {
            await store.AppendAsync(id, Entry("event " + index));
        }

        await store.AppendOnceAsync(id, "attempt1:terminal", Entry("terminal"));
        await store.AppendOnceAsync(id, "attempt1:terminal", Entry("duplicate must not appear"));
        (await StoredEntryCountAsync(harness, id)).Should().Be(ExecutionLogEntryBounds.MaxStoredEntries);
        var tail = await store.TailAsync(id, 2);
        tail.Items.Select(item => item.Message).Should().Equal("event 2002", "terminal");
        tail.TotalCount.Should().Be(2004);
        tail.DroppedCount.Should().Be(4);
        var page = await store.QueryAsync(id, new ExecutionLogQuery { Limit = 2 });
        page.Items.Select(item => item.Message).Should().Equal("event 4", "event 5");
        page.TotalCount.Should().Be(2004);
        Encoding.UTF8.GetString(Convert.FromBase64String(page.NextCursor!)).Should().Be("6");
        page.DroppedCount.Should().Be(4);
        await store.AppendAsync(id, Entry("new event"));
        var next = await store.QueryAsync(id, new ExecutionLogQuery { Limit = 2, Cursor = page.NextCursor });
        next.Items.Select(item => item.Message).Should().Equal("event 6", "event 7");
        next.TotalCount.Should().Be(2005);
    }

    [IntegrationTest]
    public async Task AppendOnceAsync_EventWhoseEntryWasTrimmed_IsStillNotAppendedAgain()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.LogStore;
        var id = "log-dedupe-after-trim-" + Guid.NewGuid().ToString("N");

        await store.AppendOnceAsync(id, "attempt1:started", Entry("started"));
        for (var index = 0; index < ExecutionLogEntryBounds.MaxStoredEntries; index++)
        {
            await store.AppendAsync(id, Entry("event " + index));
        }

        await store.AppendOnceAsync(id, "attempt1:started", Entry("started again"));

        var tail = await store.TailAsync(id, 1);
        tail.Items.Single().Message.Should().Be("event 1999");
        tail.TotalCount.Should().Be(ExecutionLogEntryBounds.MaxStoredEntries + 1);
    }

    [IntegrationTest]
    public async Task AppendOnceAsync_ConcurrentDeliveriesOfOneEvent_AppendExactlyOneEntry()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.LogStore;
        var id = "log-dedupe-race-" + Guid.NewGuid().ToString("N");

        await Task.WhenAll(Enumerable.Range(0, 16).Select(
            delivery => store.AppendOnceAsync(id, "attempt1:terminal", Entry("terminal from delivery " + delivery))));

        var tail = await store.TailAsync(id, 10);
        tail.TotalCount.Should().Be(1);
        tail.Items.Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task TailAsync_ConcurrentAppendsAndOversizedPayload_StayBounded()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.LogStore;
        var id = "log-concurrent-" + Guid.NewGuid().ToString("N");

        await store.AppendAsync(id, Entry(new string('x', 100000)));
        var writers = Task.WhenAll(Enumerable.Range(0, 50).Select(index => store.AppendAsync(id, Entry("event " + index))));
        while (!writers.IsCompleted)
        {
            (await store.TailAsync(id, 2)).Items.Count.Should().BeLessThanOrEqualTo(2);
        }

        await writers;
        var payloads = await StoredEntriesAsync(harness, id);
        payloads.Should().HaveCount(51);
        payloads.Should().OnlyContain(payload => Encoding.UTF8.GetByteCount(payload) <= ExecutionLogEntryBounds.MaxEntryBytes);
        payloads[0].Should().Contain("truncated");
    }

    [IntegrationTest]
    public async Task EntryLargerThanTheBound_WrittenAroundTheStore_IsRejectedByTheTable()
    {
        await using var harness = await CreateHarnessAsync();
        var id = "log-oversized-" + Guid.NewGuid().ToString("N");
        await harness.LogStore.AppendAsync(id, Entry("first"));

        var oversized = () => harness.ExecuteAsync(
            $"INSERT INTO {harness.Database.Table("execution_job_log_entries")} (operation_id, sequence, entry) VALUES (@operation_id, 1, to_json(repeat('x', 100000)))",
            new NpgsqlParameter("operation_id", id));

        (await oversized.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [IntegrationTest]
    public async Task AppendAsync_AfterTheLogExpired_StartsAnEmptyLog()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.LogStore;
        var id = "log-expired-" + Guid.NewGuid().ToString("N");
        await store.AppendOnceAsync(id, "attempt1:terminal", Entry("before expiry"));
        await harness.ExecuteAsync(
            $"UPDATE {harness.Database.Table("execution_job_log_streams")} SET expires_at = now() - interval '1 second' WHERE operation_id = @operation_id",
            new NpgsqlParameter("operation_id", id));

        (await store.TailAsync(id, 10)).TotalCount.Should().Be(0);

        await store.AppendOnceAsync(id, "attempt1:terminal", Entry("after expiry"));

        var tail = await store.TailAsync(id, 10);
        tail.Items.Select(item => item.Message).Should().Equal("after expiry");
        tail.TotalCount.Should().Be(1);
        tail.DroppedCount.Should().Be(0);
    }

    private Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresExecutionLogStoreTests));

    private static async Task<long> StoredEntryCountAsync(PostgresJobSubstrateHarness harness, string operationId)
        => await harness.ScalarAsync<long>(
            $"SELECT count(*) FROM {harness.Database.Table("execution_job_log_entries")} WHERE operation_id = @operation_id",
            new NpgsqlParameter("operation_id", operationId));

    private static async Task<List<string>> StoredEntriesAsync(PostgresJobSubstrateHarness harness, string operationId)
    {
        await using var command = harness.DataSource.CreateCommand(
            $"SELECT entry::text FROM {harness.Database.Table("execution_job_log_entries")} WHERE operation_id = @operation_id ORDER BY sequence");
        command.Parameters.Add(new NpgsqlParameter("operation_id", operationId));
        var entries = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(reader.GetString(0));
        }

        return entries;
    }

    private static ExecutionLogEntry Entry(string message) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = ExecutionLogLevel.Info,
        Message = message
    };
}
