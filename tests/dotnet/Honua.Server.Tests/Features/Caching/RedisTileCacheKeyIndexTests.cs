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
    public async Task RecordWriteAsync_WhenTransactionIsNotCommitted_Throws()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.CreateTransaction(Arg.Any<object>()).Returns(transaction);
        transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var act = async () => await index.RecordWriteAsync("tile-key", 42);

        await act.Should().ThrowAsync<RedisException>()
            .WithMessage("*write state transaction was not committed*");
    }

    [Fact]
    public async Task RecordAccessAsync_WhenRedisFails_RemainsBestEffort()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.CreateTransaction(Arg.Any<object>()).Returns(transaction);
        transaction.ExecuteAsync(Arg.Any<CommandFlags>())
            .Returns(Task.FromException<bool>(new RedisException("unavailable")));
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var act = async () => await index.RecordAccessAsync("tile-key", 42);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MarkExpiredAsync_ReturnsWhetherRedisAddedTheMarker(bool added)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.SetAddAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<CommandFlags>())
            .Returns(added);
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var result = await index.MarkExpiredAsync("tile-key");

        result.Should().Be(added);
    }

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
