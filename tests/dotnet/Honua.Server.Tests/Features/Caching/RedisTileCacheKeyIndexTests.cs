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
    public async Task RecordWriteAsync_TracksStorageExpirationAndPrunesExpiredState()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.CreateTransaction(Arg.Any<object>()).Returns(transaction);
        transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        await index.RecordWriteAsync("tile-key", 42, expiresAt);

        await database.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script => script.Contains("ZRANGEBYSCORE", StringComparison.Ordinal)),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            CommandFlags.DemandMaster);
        var expirationWrite = transaction.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ITransaction.SortedSetAddAsync))
            .Select(call => call.GetArguments())
            .Single(arguments =>
                arguments[0]?.ToString() == "honua:tile-cache:storage-expiration");
        expirationWrite[1]?.ToString().Should().Be("tile-key");
        expirationWrite[2].Should().Be(Convert.ToDouble(expiresAt.ToUnixTimeMilliseconds()));
        expirationWrite[3].Should().Be(SortedSetWhen.Always);
    }

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

        var act = async () => await index.RecordWriteAsync(
            "tile-key",
            42,
            DateTimeOffset.UtcNow.AddHours(1));

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

        var act = async () => await index.RecordAccessAsync("tile-key", 42, expiresAt: null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordAccessAsync_WhenObjectExpired_DoesNotReAddPrunedKey()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        await index.RecordAccessAsync("tile-key", 42, DateTimeOffset.UtcNow.AddMinutes(-1));

        database.DidNotReceive().CreateTransaction(Arg.Any<object>());
    }

    [Fact]
    public async Task RecordAccessAsync_DoesNotShortenExpirationRecordedByNewerWrite()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.CreateTransaction(Arg.Any<object>()).Returns(transaction);
        transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        await index.RecordAccessAsync("tile-key", 42, DateTimeOffset.UtcNow.AddMinutes(5));

        var expirationWrite = transaction.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ITransaction.SortedSetAddAsync))
            .Select(call => call.GetArguments())
            .Single(arguments =>
                arguments[0]?.ToString() == "honua:tile-cache:storage-expiration");
        expirationWrite[3].Should().Be(SortedSetWhen.GreaterThan);
    }

    [Fact]
    public async Task SnapshotWithStatusAsync_DrainsEveryExpiredStorageBatchBeforeReadingIndex()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZRANGEBYSCORE", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(
                Task.FromResult(RedisResult.Create((RedisValue)1_000)),
                Task.FromResult(RedisResult.Create((RedisValue)1_000)),
                Task.FromResult(RedisResult.Create((RedisValue)7)));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("WITHSCORES", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(Task.FromResult(RedisResult.Create(Array.Empty<RedisResult>())));
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var snapshot = await index.SnapshotWithStatusAsync();

        snapshot.IsAvailable.Should().BeTrue();
        snapshot.Entries.Should().BeEmpty();
        await database.Received(3).ScriptEvaluateAsync(
            Arg.Is<string>(script => script.Contains("ZRANGEBYSCORE", StringComparison.Ordinal)),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task ReadPagesAsync_UsesBoundedRedisRanges()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZRANGEBYSCORE", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)0)));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("WITHSCORES", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(
                Task.FromResult(CreateSnapshotResult(("a", "1000"), ("b", "2000"))),
                Task.FromResult(CreateSnapshotResult(("c", "3000"))));
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var entries = new List<string>();
        await foreach (var page in index.ReadPagesAsync(2))
        {
            entries.AddRange(page.Entries.Select(static entry => entry.Key));
        }

        entries.Should().Equal("a", "b", "c");
        var ranges = database.ReceivedCalls()
            .Where(call =>
                call.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync) &&
                call.GetArguments()[0]?.ToString()?.Contains("WITHSCORES", StringComparison.Ordinal) == true)
            .Select(call => (RedisValue[])call.GetArguments()[2]!)
            .Select(values => values.Select(static value => (long)value).ToArray())
            .ToArray();
        ranges.Should().BeEquivalentTo(new[] { new long[] { 0, 1 }, new long[] { 2, 3 } },
            options => options.WithStrictOrdering());
    }

    private static RedisResult CreateSnapshotResult(params (string Key, string Score)[] entries)
        => RedisResult.Create(entries.SelectMany(static entry => new RedisResult[]
        {
            RedisResult.Create((RedisValue)entry.Key),
            RedisResult.Create((RedisValue)entry.Score),
            RedisResult.Create((RedisValue)"42"),
            RedisResult.Create((RedisValue)"version")
        }).ToArray());

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
