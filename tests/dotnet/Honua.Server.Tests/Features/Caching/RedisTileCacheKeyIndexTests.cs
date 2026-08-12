// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Tiles;
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
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromException<RedisResult>(new RedisException("unavailable")));
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

        await database.DidNotReceive().ScriptEvaluateAsync(
            Arg.Is<string>(script => script.Contains("ZSCORE", StringComparison.Ordinal)),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task RecordAccessAsync_DoesNotShortenExpirationRecordedByNewerWrite()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZRANGEBYSCORE", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)0));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZSCORE", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)1));
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await index.RecordAccessAsync("tile-key", 42, expiresAt, "tenant_a");

        await database.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script =>
                script.Contains("if not redis.call('ZSCORE'", StringComparison.Ordinal) &&
                script.Contains("'ZADD', KEYS[5], 'GT'", StringComparison.Ordinal)),
            Arg.Any<RedisKey[]>(),
            Arg.Is<RedisValue[]>(values =>
                values[0].ToString() == "tile-key" &&
                values[3].ToString() == expiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) &&
                values[4].ToString() == "tenant_a"),
            CommandFlags.DemandMaster);
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
                Arg.Is<string>(script => script.Contains("ZSCAN", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)"0"));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZRANGEBYLEX", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(CreateSnapshotResult(cursor: string.Empty, rawCount: 0));
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
    public async Task ReadPagesAsync_UsesStableBoundedMembershipCursor()
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
                Arg.Is<string>(script => script.Contains("ZSCAN", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)"0"));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZRANGEBYLEX", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(
                CreateSnapshotResult("b", 2, ("a", "1000", "tenant_a"), ("b", "2000", "tenant_a")),
                CreateSnapshotResult("c", 1, ("c", "3000", "tenant_a")));
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var entries = new List<string>();
        await foreach (var page in index.ReadPagesAsync(2))
        {
            entries.AddRange(page.Entries.Select(static entry => entry.Key));
        }

        entries.Should().Equal("a", "b", "c");
        var cursors = database.ReceivedCalls()
            .Where(call =>
                call.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync) &&
                call.GetArguments()[0]?.ToString()?.Contains("ZRANGEBYLEX", StringComparison.Ordinal) == true)
            .Select(call => (RedisValue[])call.GetArguments()[2]!)
            .Select(values => values.Select(static value => value.ToString()).ToArray())
            .ToArray();
        cursors.Should().BeEquivalentTo(new[] { new[] { string.Empty, "2" }, new[] { "b", "2" } },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task ReadPagesAsync_DiscardsLegacyEntriesMissingLifecycleMetadata()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZRANGEBYSCORE", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)0));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZSCAN", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)"0"));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("SPOP", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)0));
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script => script.Contains("ZRANGEBYLEX", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(CreateSnapshotResult(cursor: string.Empty, rawCount: 0));
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);

        var snapshot = await index.SnapshotWithStatusAsync();

        snapshot.IsAvailable.Should().BeTrue();
        await database.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script =>
                script.Contains("HEXISTS', KEYS[4]", StringComparison.Ordinal) &&
                script.Contains("SADD', KEYS[7]", StringComparison.Ordinal)),
            Arg.Is<RedisKey[]>(keys =>
                keys.Length == 7 &&
                keys.Any(key => key.ToString() == "honua:tile-cache:members-migrated:v2") &&
                keys.Any(key => key.ToString() == "honua:tile-cache:legacy-discard:v2")),
            Arg.Any<RedisValue[]>(),
            CommandFlags.DemandMaster);
        await database.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script =>
                script.Contains("SPOP", StringComparison.Ordinal) &&
                script.Contains("HDEL', KEYS[4]", StringComparison.Ordinal) &&
                script.Contains("SET', KEYS[9]", StringComparison.Ordinal)),
            Arg.Is<RedisKey[]>(keys => keys.Length == 9),
            Arg.Any<RedisValue[]>(),
            CommandFlags.DemandMaster);
    }

    private static RedisResult CreateSnapshotResult(
        string cursor,
        int rawCount,
        params (string Key, string Score, string TenantScope)[] entries)
        => RedisResult.Create(
            new RedisResult[]
            {
                RedisResult.Create((RedisValue)cursor),
                RedisResult.Create((RedisValue)rawCount)
            }.Concat(entries.SelectMany(static entry => new RedisResult[]
            {
                RedisResult.Create((RedisValue)entry.Key),
                RedisResult.Create((RedisValue)entry.Score),
                RedisResult.Create((RedisValue)"42"),
                RedisResult.Create((RedisValue)"version"),
                RedisResult.Create((RedisValue)entry.TenantScope)
            })).ToArray());

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

    [Theory]
    [InlineData(0, TileCacheExpirationMarkResult.NotCurrent)]
    [InlineData(1, TileCacheExpirationMarkResult.AlreadyMarked)]
    [InlineData(2, TileCacheExpirationMarkResult.Added)]
    public async Task TryMarkExpiredIfCurrentAsync_UsesOneAtomicGenerationCheckedScript(
        long redisResult,
        TileCacheExpirationMarkResult expected)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Is<string>(script =>
                    script.Contains("current_version", StringComparison.Ordinal) &&
                    script.Contains("SADD", StringComparison.Ordinal)),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                CommandFlags.DemandMaster)
            .Returns(RedisResult.Create((RedisValue)redisResult));
        var index = new RedisTileCacheKeyIndex(redis, NullLogger<RedisTileCacheKeyIndex>.Instance);
        var entry = new TileCacheEntry(
            "tile-key",
            42,
            DateTimeOffset.UtcNow,
            WriteVersion: "version-1");

        var result = await index.TryMarkExpiredIfCurrentAsync(entry);

        result.Should().Be(expected);
        await database.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script => script.Contains("current_version", StringComparison.Ordinal)),
            Arg.Is<RedisKey[]>(keys => keys.Length == 3),
            Arg.Is<RedisValue[]>(values =>
                values[0].ToString() == "tile-key" && values[1].ToString() == "version-1"),
            CommandFlags.DemandMaster);
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
