// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Redis-backed durability coverage for the feature-stream replay cursor (#2428 GA
/// hardening). The streaming replay path resumes a reconnecting client from a durable
/// cursor served by the Redis-backed <see cref="InMemoryFeatureChangeEventStore"/>; this
/// suite proves that store persists events and their monotonic cursors <b>across a process
/// restart</b> by discarding one store instance (and its multiplexer) and reading the same
/// events back through a brand-new instance over the same Redis. A bounded integration
/// test — no load — that requires the shared Redis container like the other Redis suites.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class FeatureStreamCursorDurabilityTests
{
    private readonly RedisFixture _redis;

    public FeatureStreamCursorDurabilityTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private static InMemoryFeatureChangeEventStore CreateDurableStore(IConnectionMultiplexer multiplexer)
        => new(
            Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 1000 }),
            multiplexer,
            // Strict durability: no in-memory fallback, so a successful append/query proves
            // the event actually round-tripped through Redis.
            allowInMemoryFallback: false);

    private static FeatureChangeEventRequest Edit(long objectId, string eventId) => new()
    {
        EventId = eventId,
        ServiceId = "svc-durability",
        LayerId = 0,
        ObjectId = objectId,
        Operation = "update",
        Protocol = "rest",
        RequestId = $"req-{objectId}",
    };

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/sse")]
    public async Task ReplayCursor_SurvivesStoreRestart_EventsAndOrderPreserved()
    {
        await FlushAsync();

        var appendedCursors = new List<long>();
        var eventIds = Enumerable.Range(1, 5).Select(i => $"evt-{Guid.NewGuid():N}-{i}").ToArray();

        // First "process": append five edits through a durable store, then discard it.
        using (var multiplexerA = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString))
        {
            using var storeA = CreateDurableStore(multiplexerA);
            for (var i = 0; i < eventIds.Length; i++)
            {
                var persisted = await storeA.AppendAsync(Edit(i + 1, eventIds[i]));
                appendedCursors.Add(persisted.Cursor);
            }
        }

        appendedCursors.Should().BeInAscendingOrder("cursors are monotonic");

        // Second "process": a brand-new store over the same Redis must serve the same events
        // and cursors — proving replay durability across a restart.
        using var multiplexerB = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        using var storeB = CreateDurableStore(multiplexerB);

        var replayFrom = appendedCursors[0] - 1;
        var replayed = await storeB.QueryAsync(cursor: replayFrom, from: null, to: null, limit: 100);

        replayed.Select(e => e.EventId).Should().Contain(eventIds,
            "every durably-appended event survives a store restart and is replayable from its cursor");
        replayed.Select(e => e.Cursor).Should().BeInAscendingOrder();

        var currentCursor = await storeB.GetCurrentCursorAsync();
        currentCursor.Should().Be(appendedCursors[^1],
            "the durable cursor high-watermark is preserved across the restart");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/sse")]
    public async Task ReplayCursor_AfterRestart_ResumesFromMidStreamCursor_WithoutRedelivery()
    {
        await FlushAsync();

        var eventIds = Enumerable.Range(1, 5).Select(i => $"evt-{Guid.NewGuid():N}-{i}").ToArray();
        var cursors = new List<long>();

        using (var multiplexerA = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString))
        {
            using var storeA = CreateDurableStore(multiplexerA);
            for (var i = 0; i < eventIds.Length; i++)
            {
                var persisted = await storeA.AppendAsync(Edit(i + 1, eventIds[i]));
                cursors.Add(persisted.Cursor);
            }
        }

        // A client that had already consumed through the 2nd event reconnects after the
        // restart with that cursor; replay must resume with events 3, 4, 5 only.
        using var multiplexerB = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        using var storeB = CreateDurableStore(multiplexerB);

        var resumeCursor = cursors[1];
        var resumed = await storeB.QueryAsync(cursor: resumeCursor, from: null, to: null, limit: 100);

        resumed.Select(e => e.EventId).Should().StartWith(eventIds.Skip(2),
            "reconnect resumes strictly after the client's last-seen cursor, with no redelivery");
        resumed.Should().OnlyContain(e => e.Cursor > resumeCursor);
    }

    private async Task FlushAsync()
    {
        var config = ConfigurationOptions.Parse(_redis.ConnectionString);
        config.AllowAdmin = true;
        using var admin = await ConnectionMultiplexer.ConnectAsync(config);
        foreach (var endpoint in admin.GetEndPoints())
        {
            await admin.GetServer(endpoint).FlushDatabaseAsync();
        }
    }
}
