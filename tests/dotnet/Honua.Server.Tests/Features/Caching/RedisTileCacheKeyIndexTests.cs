// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

public sealed class RedisTileCacheKeyIndexTests
{
    [Fact]
    public async Task ExecuteSerializedAsync_WhenLeaseRenewalIsLost_CancelsMutationAndFailsClosed()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.LockTakeAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CommandFlags>())
            .Returns(true);
        database.LockExtendAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CommandFlags>())
            .Returns(false);
        database.LockReleaseAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<CommandFlags>())
            .Returns(true);
        var index = new RedisTileCacheKeyIndex(
            redis,
            NullLogger<RedisTileCacheKeyIndex>.Instance,
            mutationLeaseDuration: TimeSpan.FromMilliseconds(100),
            mutationLeaseRenewalInterval: TimeSpan.FromMilliseconds(5));
        var mutationObservedCancellation = false;

        var act = async () => await index.ExecuteSerializedAsync(
            "tile-key",
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    mutationObservedCancellation = true;
                    throw;
                }
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Lost the distributed tile-cache mutation lease*");
        mutationObservedCancellation.Should().BeTrue();
        await database.Received().LockExtendAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CommandFlags>());
        await database.Received().LockReleaseAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<CommandFlags>());
    }
}
