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
}
