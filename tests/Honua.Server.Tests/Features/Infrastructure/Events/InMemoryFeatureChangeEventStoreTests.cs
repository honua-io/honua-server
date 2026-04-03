// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(Protocols.TestQuality)]
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
    }
}
