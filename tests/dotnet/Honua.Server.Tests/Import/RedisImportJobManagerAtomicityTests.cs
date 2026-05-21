// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class RedisImportJobManagerAtomicityTests
{
    [UnitTest]
    public async Task RecoverInFlightAsync_WhenRedisHasInFlightJobs_UsesAtomicMoveBackToReadyQueue()
    {
        const string queueKey = "test:queue:recover";

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ListRightPopLeftPushAsync($"{queueKey}:processing", queueKey, Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult((RedisValue)"job-1"),
                Task.FromResult((RedisValue)"job-2"),
                Task.FromResult(RedisValue.Null));

        var queue = new RedisJobQueue(redis, NullLogger.Instance, queueKey);

        await queue.RecoverInFlightAsync();

        await database.Received(3)
            .ListRightPopLeftPushAsync($"{queueKey}:processing", queueKey, Arg.Any<CommandFlags>());
        await database.DidNotReceive()
            .ListRemoveAsync($"{queueKey}:processing", Arg.Any<RedisValue>(), 1, Arg.Any<CommandFlags>());
        await database.DidNotReceive()
            .ListLeftPushAsync(queueKey, Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [UnitTest]
    public async Task TryAcquireLeadershipAsync_WhenConfiguredRedisIsUnavailable_DoesNotPromoteLocalFallbackLeadership()
    {
        const string leaderKey = "test:leader:unavailable";
        const string instanceId = "instance-a";

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.LockTakeAsync(leaderKey, instanceId, Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated outage")));

        using var election = new RedisLeaderElection(redis, NullLogger.Instance, leaderKey, instanceId);

        (await election.TryAcquireLeadershipAsync()).Should().BeFalse();
        election.IsLeader.Should().BeFalse();
        election.IsUsingFallback.Should().BeFalse();
    }

    [UnitTest]
    public async Task HeartbeatAsync_WhenRedisBackedLeaderLosesRedisConnection_DropsLeadershipWithoutFallback()
    {
        const string leaderKey = "test:leader:heartbeat-outage";
        const string instanceId = "instance-a";

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.LockTakeAsync(leaderKey, instanceId, Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        database.LockExtendAsync(leaderKey, instanceId, Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated outage")));

        using var election = new RedisLeaderElection(redis, NullLogger.Instance, leaderKey, instanceId);

        (await election.TryAcquireLeadershipAsync()).Should().BeTrue();
        election.IsLeader.Should().BeTrue();
        election.IsUsingFallback.Should().BeFalse();

        (await election.HeartbeatAsync()).Should().BeFalse();
        election.IsLeader.Should().BeFalse();
        election.IsUsingFallback.Should().BeFalse();
    }

    [UnitTest]
    public async Task TryAcquireLeadershipAsync_WhenRedisRecoversAfterOutage_ReacquiresLeadershipThroughRedisWithoutFallback()
    {
        const string leaderKey = "test:leader:restore";
        const string instanceId = "instance-a";

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.LockTakeAsync(leaderKey, instanceId, Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(
                Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated outage")),
                Task.FromResult(true));
        database.PingAsync(Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(TimeSpan.Zero));

        using var election = new RedisLeaderElection(redis, NullLogger.Instance, leaderKey, instanceId);

        (await election.TryAcquireLeadershipAsync()).Should().BeFalse();
        election.IsLeader.Should().BeFalse();
        election.IsUsingFallback.Should().BeFalse();
        ExpireRedisRetryWindow(election);

        (await election.TryAcquireLeadershipAsync()).Should().BeTrue();
        election.IsLeader.Should().BeTrue();
        election.IsUsingFallback.Should().BeFalse();
    }

    [UnitTest]
    public async Task Dispose_WhenReleaseHangs_ReturnsPromptlyWithoutBlocking()
    {
        // Audit fix: synchronous Dispose() must not block on Redis/network; it schedules
        // a best-effort release on a background task with a bounded timeout.
        const string leaderKey = "test:leader:dispose-hang";
        const string instanceId = "instance-a";

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.LockTakeAsync(leaderKey, instanceId, Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        var hangingRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.LockReleaseAsync(leaderKey, instanceId, Arg.Any<CommandFlags>())
            .Returns(_ => hangingRelease.Task);

        var election = new RedisLeaderElection(redis, NullLogger.Instance, leaderKey, instanceId);

        (await election.TryAcquireLeadershipAsync()).Should().BeTrue();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        election.Dispose();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "Dispose must not block synchronously on a hanging Redis release");

        // Let background timeout fire so the hanging task is observed.
        hangingRelease.TrySetResult(true);
        await Task.Delay(50);
    }

    [UnitTest]
    public async Task DisposeAsync_AwaitsBoundedRelease()
    {
        const string leaderKey = "test:leader:dispose-async";
        const string instanceId = "instance-a";

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.LockTakeAsync(leaderKey, instanceId, Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        database.LockReleaseAsync(leaderKey, instanceId, Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        var election = new RedisLeaderElection(redis, NullLogger.Instance, leaderKey, instanceId);
        (await election.TryAcquireLeadershipAsync()).Should().BeTrue();

        await election.DisposeAsync();

        election.IsLeader.Should().BeFalse();
        await database.Received().LockReleaseAsync(leaderKey, instanceId, Arg.Any<CommandFlags>());
    }

    [UnitTest]
    public async Task SetProgressAsync_WhenActiveIndexWriteFails_DoesNotLeaveCachedProgressBehind()
    {
        var harness = new RedisProgressHarness(throwOnDirectSetAdd: true);
        var store = CreateStore(harness);
        var request = CreateRequest();

        await FluentActions
            .Invoking(() => store.SetProgressAsync("job-1", request, TimeSpan.FromMinutes(5)))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Distributed import state is unavailable*");

        (await harness.Cache.GetStringAsync("test:request:job-1")).Should().BeNull();
        (await harness.GetRedisValueAsync("test:request:job-1")).Should().BeNull();
    }

    [UnitTest]
    public async Task DeleteProgressAsync_WhenActiveIndexWriteFails_DoesNotDropStoredProgress()
    {
        var harness = new RedisProgressHarness(throwOnDirectSetRemove: true);
        var store = CreateStore(harness);
        var request = CreateRequest();

        harness.SeedProgress("job-1", request);

        await FluentActions
            .Invoking(() => store.DeleteProgressAsync("job-1"))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Distributed import state is unavailable*");

        (await harness.Cache.GetStringAsync("test:request:job-1")).Should().NotBeNull();
        (await harness.GetRedisValueAsync("test:request:job-1")).Should().NotBeNull();
    }

    private static RedisProgressStore<GeoservicesImportRequest> CreateStore(RedisProgressHarness harness)
        => new(
            harness.Cache,
            NullLogger.Instance,
            "test:request:",
            GeoservicesImportJsonContext.Default.GeoservicesImportRequest,
            harness.Redis);

    private static GeoservicesImportRequest CreateRequest()
        => new()
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "geoservices_atomicity_test",
            AutoPublish = false
        };

    private static void ExpireRedisRetryWindow(RedisLeaderElection election)
    {
        var retryField = typeof(RedisLeaderElection).GetField("_lastRedisFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        retryField.Should().NotBeNull();
        retryField!.SetValue(election, DateTime.UtcNow - TimeSpan.FromMinutes(1));
    }

    private sealed class RedisProgressHarness
    {
        private readonly ConcurrentDictionary<string, string> _redisStrings = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _redisSets = new(StringComparer.Ordinal);
        private readonly bool _throwOnDirectSetAdd;
        private readonly bool _throwOnDirectSetRemove;

        public RedisProgressHarness(bool throwOnDirectSetAdd = false, bool throwOnDirectSetRemove = false)
        {
            _throwOnDirectSetAdd = throwOnDirectSetAdd;
            _throwOnDirectSetRemove = throwOnDirectSetRemove;

            Cache = new DelegatingDistributedCache(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

            var database = Substitute.For<IDatabase>();
            var transaction = Substitute.For<ITransaction>();
            var redis = Substitute.For<IConnectionMultiplexer>();

            redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

            database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    var key = call.ArgAt<RedisKey>(0).ToString();
                    _redisStrings[key] = call.ArgAt<RedisValue>(1).ToString();
                    return Task.FromResult(true);
                });

            database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    var key = call.ArgAt<RedisKey>(0).ToString();
                    return Task.FromResult(_redisStrings.TryGetValue(key, out var value) ? (RedisValue)value : RedisValue.Null);
                });

            database.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    if (_throwOnDirectSetAdd)
                    {
                        throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated active-index failure");
                    }

                    AddSetMember(call.ArgAt<RedisKey>(0).ToString(), call.ArgAt<RedisValue>(1).ToString());
                    return Task.FromResult(true);
                });

            database.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    if (_throwOnDirectSetRemove)
                    {
                        throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated active-index failure");
                    }

                    RemoveSetMember(call.ArgAt<RedisKey>(0).ToString(), call.ArgAt<RedisValue>(1).ToString());
                    return Task.FromResult(true);
                });

            database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    var key = call.ArgAt<RedisKey>(0).ToString();
                    return Task.FromResult(GetSetMembers(key));
                });

            database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));

            database.CreateTransaction().Returns(transaction);

            transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.ExecuteAsync(Arg.Any<CommandFlags>())
                .Returns(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated transaction failure")));

            Redis = redis;
        }

        public IDistributedCache Cache { get; }

        public IConnectionMultiplexer Redis { get; }

        public void SeedProgress(string jobId, GeoservicesImportRequest request)
        {
            var key = $"test:request:{jobId}";
            var json = JsonSerializer.Serialize(request, GeoservicesImportJsonContext.Default.GeoservicesImportRequest);
            _redisStrings[key] = json;
            Cache.SetString(key, json, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
            AddSetMember("test:request:active", jobId);
        }

        public Task<string?> GetRedisValueAsync(string key)
            => Task.FromResult(_redisStrings.TryGetValue(key, out var value) ? value : null);

        private void AddSetMember(string key, string member)
        {
            var set = _redisSets.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            set[member] = 1;
        }

        private void RemoveSetMember(string key, string member)
        {
            if (_redisSets.TryGetValue(key, out var set))
            {
                set.TryRemove(member, out _);
            }
        }

        private RedisValue[] GetSetMembers(string key)
        {
            if (!_redisSets.TryGetValue(key, out var set))
            {
                return [];
            }

            return set.Keys.Select(member => (RedisValue)member).ToArray();
        }
    }

    private sealed class DelegatingDistributedCache(IDistributedCache inner) : IDistributedCache
    {
        private readonly IDistributedCache _inner = inner;

        public byte[]? Get(string key) => _inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

        public void Remove(string key) => _inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => _inner.SetAsync(key, value, options, token);
    }
}
