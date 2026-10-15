// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class RedisExecutionLogTailTests
{
    [UnitTest]
    public async Task TailAsync_ConcurrentAppends_DoNotExpandTheObservedWindow()
    {
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        const string key = "controlplane:job:log:gp-tail";
        database.ListLengthAsync(key, Arg.Any<CommandFlags>()).Returns(100);
        // The list grows after LLEN. Reading to -1 would return all of the new
        // entries; only the finite window observed at LLEN is part of this tail.
        database.ListRangeAsync(key, 98, 99, Arg.Any<CommandFlags>()).Returns(new RedisValue[]
        {
            """{"timestamp":"2026-10-15T00:00:00Z","level":1,"message":"event 98"}""",
            """{"timestamp":"2026-10-15T00:00:01Z","level":1,"message":"event 99"}""",
        });
        var store = new RedisExecutionLogStore(redis, NullLogger<RedisExecutionLogStore>.Instance);

        var result = await store.TailAsync("gp-tail", 2);

        result.Items.Select(x => x.Message).Should().Equal("event 98", "event 99");
        result.TotalCount.Should().Be(100);
        await database.Received(1).ListRangeAsync(key, 98, 99, Arg.Any<CommandFlags>());
        await database.DidNotReceive().ListRangeAsync(key, Arg.Any<long>(), -1, Arg.Any<CommandFlags>());
    }
}
