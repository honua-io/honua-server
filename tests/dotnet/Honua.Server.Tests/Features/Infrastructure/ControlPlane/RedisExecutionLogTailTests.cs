// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection("Redis")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisExecutionLogTailTests(RedisFixture fixture)
{
    [IntegrationTest]
    public async Task BoundedLog_TrimsWithAbsoluteCursorsAndDeduplicatesExternalEvents()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var store = new RedisExecutionLogStore(redis, NullLogger<RedisExecutionLogStore>.Instance);
        var id = "log-bounds-" + Guid.NewGuid().ToString("N");
        var key = "controlplane:job:log:" + id;
        try
        {
            for (var index = 0; index < RedisExecutionLogStore.MaxStoredEntries + 3; index++)
            {
                await store.AppendAsync(id, Entry("event " + index));
            }
            await store.AppendOnceAsync(id, "attempt1:terminal", Entry("terminal"));
            await store.AppendOnceAsync(id, "attempt1:terminal", Entry("duplicate must not appear"));
            (await redis.GetDatabase().ListLengthAsync(key)).Should().Be(RedisExecutionLogStore.MaxStoredEntries);
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
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync([key, "{" + key + "}:state"]);
        }
    }

    [IntegrationTest]
    public async Task TailAsync_ConcurrentAppendsAndOversizedPayload_StayBounded()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var store = new RedisExecutionLogStore(redis, NullLogger<RedisExecutionLogStore>.Instance);
        var id = "log-concurrent-" + Guid.NewGuid().ToString("N");
        var key = "controlplane:job:log:" + id;
        try
        {
            await store.AppendAsync(id, Entry(new string('x', 100000)));
            var writers = Task.WhenAll(Enumerable.Range(0, 50).Select(index => store.AppendAsync(id, Entry("event " + index))));
            while (!writers.IsCompleted)
            {
                (await store.TailAsync(id, 2)).Items.Count.Should().BeLessThanOrEqualTo(2);
            }
            await writers;
            var payloads = await redis.GetDatabase().ListRangeAsync(key);
            payloads.Should().OnlyContain(payload => Encoding.UTF8.GetByteCount(payload.ToString()) <= RedisExecutionLogStore.MaxEntryBytes);
            payloads[0].ToString().Should().Contain("truncated");
        }
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync([key, "{" + key + "}:state"]);
        }
    }

    [IntegrationTest]
    public async Task LegacyOversizedEntry_ReadsBoundedPlaceholderWithoutChangingCursor()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var store = new RedisExecutionLogStore(redis, NullLogger<RedisExecutionLogStore>.Instance);
        var id = "legacy-log-" + Guid.NewGuid().ToString("N");
        var key = "controlplane:job:log:" + id;
        try
        {
            await redis.GetDatabase().ListRightPushAsync(key, new string('x', 100000));
            var tail = await store.TailAsync(id, 10);
            tail.TotalCount.Should().Be(1);
            tail.Items.Single().Message.Should().Contain("Legacy execution log payload omitted");
            tail.Items.Single().Level.Should().Be(ExecutionLogLevel.Warning);
        }
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync([key, "{" + key + "}:state"]);
        }
    }

    private static ExecutionLogEntry Entry(string message) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = ExecutionLogLevel.Info,
        Message = message
    };
}
