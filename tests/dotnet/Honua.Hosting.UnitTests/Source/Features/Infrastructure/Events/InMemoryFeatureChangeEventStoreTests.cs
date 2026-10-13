// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text.Json;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(TestProtocols.TestQuality)]
public sealed class InMemoryFeatureChangeEventStoreTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AppendAsync_WhenDurableRedisWriteFails_MarksStoreUnavailable()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns<Task<RedisResult>>(_ =>
                Task.FromException<RedisResult>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated outage")));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 100 }),
            redis,
            allowInMemoryFallback: false);

        var act = () => store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-1"
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        ((IFeatureChangeEventStoreHealth)store).CanPersistEvents.Should().BeFalse();
        ((IFeatureChangeEventStoreHealth)store).IsUsingInMemoryFallback.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AppendAsync_WithoutRedisInSingleNodeMode_PersistsInMemoryAndReportsHealthyFallback()
    {
        // Single-node/serverless topology (#1618): no Redis configured, in-memory mode allowed.
        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 100 }),
            redis: null,
            allowInMemoryFallback: true);

        store.CanPersistEvents.Should().BeTrue();
        store.IsUsingInMemoryFallback.Should().BeTrue();

        var created = await store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-1"
        });

        created.Cursor.Should().Be(1);
        var events = await store.QueryAsync(cursor: null, from: null, to: null, limit: 10);
        events.Should().ContainSingle(e => e.EventId == created.EventId);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AppendAsync_WithoutRedisAndDurabilityRequired_ReportsUnavailableAndThrows()
    {
        // Strict store contract retained: durability required + no Redis = cannot persist.
        // (The host registration never produces this combination anymore — when Redis is
        // unconfigured it explicitly allows the in-memory single-node mode, see
        // FeatureEventsAndStreamingRegistration.)
        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 100 }),
            redis: null,
            allowInMemoryFallback: false);

        ((IFeatureChangeEventStoreHealth)store).CanPersistEvents.Should().BeFalse();

        var act = () => store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-1"
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AppendAsync_WithChangedAttributesAndGeometryEnvelope_PersistsSerializedPayloads()
    {
        var database = Substitute.For<IDatabase>();
        RedisValue[]? capturedValues = null;
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                capturedValues = callInfo.ArgAt<RedisValue[]>(2);
                return Task.FromResult(RedisResult.Create(new[] { RedisResult.Create((RedisValue)"1") }));
            });
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 100 }),
            redis,
            allowInMemoryFallback: false);
        var changedAttributes = new Dictionary<string, object?>
        {
            ["name"] = "updated",
            ["count"] = 5,
            ["active"] = true
        };
        var geometryEnvelope = new[] { -1d, -2d, 3d, 4d };

        await store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-1",
            ChangedAttributes = changedAttributes,
            GeometryChanged = true,
            GeometryEnvelope = geometryEnvelope
        });

        capturedValues.Should().NotBeNull();
        capturedValues![10].ToString().Should().Be(
            JsonSerializer.Serialize(changedAttributes, FeatureChangeEventsJsonContext.Default.DictionaryStringObject));
        capturedValues[11].ToString().Should().Be(
            JsonSerializer.Serialize(geometryEnvelope, FeatureChangeEventsJsonContext.Default.DoubleArray));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetRetentionWindowAsync_AfterAllPayloadsExpire_ReturnsKnownEmptyIssuedCursor()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync((RedisKey)"featurechange:cursor", Arg.Any<CommandFlags>())
            .Returns((RedisValue)37);
        database.SortedSetRangeByRankAsync(
                (RedisKey)"featurechange:index",
                0,
                127,
                Order.Ascending,
                Arg.Any<CommandFlags>())
            .Returns(Array.Empty<RedisValue>());

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 100 }),
            redis,
            allowInMemoryFallback: false);

        var current = await store.GetCurrentCursorAsync();
        var window = await store.GetRetentionWindowAsync();

        current.Should().Be(37, "the durable counter survives payload expiry");
        window.CurrentCursor.Should().Be(37);
        window.IsEmpty.Should().BeTrue();
        window.IsDeterminate.Should().BeTrue();
        window.HasGapAfter(37).Should().BeFalse(
            "a baseline captured at the issued cursor needs no expired successor events");
        window.HasGapAfter(0).Should().BeTrue(
            "an older client cursor still needs the expired history and must re-snapshot");
        (await store.GetOldestRetainedCursorAsync()).Should().Be(long.MaxValue,
            "the legacy oldest-cursor projection remains fail-closed for older callers");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task QueryAsync_WhenInteriorRedisPayloadIsMissing_ReturnsNonContiguousBatch()
    {
        var beforeGap = CreateEvent(cursor: 42);
        var afterGap = CreateEvent(cursor: 44);
        var database = Substitute.For<IDatabase>();
        database.SortedSetRangeByScoreAsync(
                (RedisKey)"featurechange:index",
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<Exclude>(),
                Arg.Any<Order>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<CommandFlags>())
            .Returns([(RedisValue)42, (RedisValue)43, (RedisValue)44]);
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo => callInfo.ArgAt<RedisKey>(0).ToString() switch
            {
                "featurechange:event:42" => (RedisValue)JsonSerializer.Serialize(
                    beforeGap,
                    FeatureChangeEventsJsonContext.Default.FeatureChangeEvent),
                "featurechange:event:44" => (RedisValue)JsonSerializer.Serialize(
                    afterGap,
                    FeatureChangeEventsJsonContext.Default.FeatureChangeEvent),
                _ => RedisValue.Null
            });

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 100 }),
            redis,
            allowInMemoryFallback: false);

        var events = await store.QueryAsync(cursor: 41, from: null, to: null, limit: 10);

        // The store prunes the missing payload but leaves replay validation to the transport.
        events.Select(evt => evt.Cursor).Should().Equal([42L, 44L]);
        await database.Received(1).SortedSetRemoveAsync(
            (RedisKey)"featurechange:index",
            (RedisValue)43,
            Arg.Any<CommandFlags>());
    }

    private static FeatureChangeEvent CreateEvent(long cursor)
        => new()
        {
            EventId = $"event-{cursor}",
            Cursor = cursor,
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "test",
            ServiceId = "test",
            LayerId = 0,
            ObjectId = cursor,
            Operation = "update",
            Protocol = "test",
            RequestId = $"request-{cursor}"
        };
}
