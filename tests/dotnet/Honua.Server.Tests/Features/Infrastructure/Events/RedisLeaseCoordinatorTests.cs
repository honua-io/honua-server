// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Collection("Unit")]
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisLeaseCoordinatorTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TryAcquireOrExtendAsync_WhenRenewalFails_CancelsLeaseLossToken()
    {
        var database = Substitute.For<IDatabase>();
        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true), Task.FromResult(false));
        database.LockExtendAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(false));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        using var coordinator = new RedisLeaseCoordinator(redis, $"test:lease:{Guid.NewGuid():N}", TimeSpan.FromSeconds(5));

        (await coordinator.TryAcquireOrExtendAsync()).Should().BeTrue();
        coordinator.LeaseLostToken.IsCancellationRequested.Should().BeFalse();

        (await coordinator.TryAcquireOrExtendAsync()).Should().BeFalse();
        coordinator.LeaseLostToken.IsCancellationRequested.Should().BeTrue();
        coordinator.HasLease.Should().BeFalse();
    }
}
