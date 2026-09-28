// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class RedisCacheIndexMaintenanceTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IdleTick_BoundsWorkAndResumesMemberPages()
    {
        var clock = new TickTimeProvider();
        var (redis, database, server) = CreateRedis();
        server.ExecuteAsync(Arg.Any<int?>(), "SCAN", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(Scan("0", "test:scope:default:__cache_key_index__"));
        var members = Enumerable.Range(0, 20000).Select(index => $"test:scope:default:key:{index}").ToArray();
        database.ExecuteAsync("SSCAN", Arg.Any<object[]>()).Returns(Scan("0", members));
        var removed = new List<string>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var batch = call.ArgAt<RedisKey[]>(1)[1..];
                Assert.InRange(batch.Length, 1, 128);
                lock (removed)
                {
                    removed.AddRange(batch.Select(value => value.ToString()));
                    if (removed.Distinct(StringComparer.Ordinal).Count() == members.Length)
                    {
                        complete.TrySetResult();
                    }
                }
                return Task.FromResult(RedisResult.Create(batch.Length));
            });

        await using var cleanup = new RedisCacheIndexMaintenance(redis, "test:", NullLogger.Instance, clock);
        clock.Tick();
        await WaitUntilAsync(() => database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "ScriptEvaluateAsync") == 126);
        Assert.Equal(128, database.ReceivedCalls().Count(call => call.GetMethodInfo().Name is "ScriptEvaluateAsync" or "ExecuteAsync") +
            server.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "ExecuteAsync"));
        Assert.False(complete.Task.IsCompleted);

        clock.Tick();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (removed)
        {
            Assert.Equal(members.OrderBy(value => value, StringComparer.Ordinal),
                removed.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
        }
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task Dispose_WaitsForIssuedScanAndStopsTimer()
    {
        var clock = new TickTimeProvider();
        var (redis, _, server) = CreateRedis();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ExecuteAsync(Arg.Any<int?>(), "SCAN", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(_ => { started.TrySetResult(); return release.Task; });
        var cleanup = new RedisCacheIndexMaintenance(redis, "test:", NullLogger.Instance, clock);
        clock.Tick();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposal = cleanup.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult(Scan("0"));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, clock.ActiveTimers);
        clock.Tick();
        Assert.Single(server.ReceivedCalls(), call => call.GetMethodInfo().Name == "ExecuteAsync");
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task FailedScan_RetriesNextTickWithoutRetainingRequestContext(bool permissionDenied)
    {
        var clock = new TickTimeProvider();
        var (redis, _, server) = CreateRedis();
        var ambient = new AsyncLocal<object?> { Value = new object() };
        var attempts = 0;
        server.ExecuteAsync(Arg.Any<int?>(), "SCAN", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                Assert.Null(ambient.Value);
                return Interlocked.Increment(ref attempts) == 1
                    ? Task.FromException<RedisResult>(permissionDenied
                        ? new RedisServerException("NOPERM this user has no permissions to run SCAN")
                        : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Expected failure"))
                    : Task.FromResult(Scan("0"));
            });
        await using var cleanup = new RedisCacheIndexMaintenance(redis, "test:", NullLogger.Instance, clock);
        // End the resolving request before the fake timer fires. Unlike a real
        // suppressed timer, Tick runs directly in this test's current context.
        ambient.Value = null;
        clock.Tick();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1);
        clock.Tick();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 2);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task DeniedMemberScan_RetriesAfterPermissionsRecover()
    {
        var clock = new TickTimeProvider();
        var (redis, database, server) = CreateRedis();
        server.ExecuteAsync(Arg.Any<int?>(), "SCAN", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(Scan("0", "test:scope:default:__cache_key_index__"));
        var attempts = 0;
        database.ExecuteAsync("SSCAN", Arg.Any<object[]>()).Returns(_ =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<RedisResult>(new RedisServerException("NOPERM this user has no permissions to run SSCAN"))
                : Task.FromResult(Scan("0")));
        await using var cleanup = new RedisCacheIndexMaintenance(redis, "test:", NullLogger.Instance, clock);
        clock.Tick();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1);
        clock.Tick();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 2);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task Constructor_AlreadySuppressed_PreservesCallerFlowState()
    {
        var clock = new TickTimeProvider();
        var (redis, _, _) = CreateRedis();
        RedisCacheIndexMaintenance cleanup;
        using (ExecutionContext.SuppressFlow())
        {
            cleanup = new RedisCacheIndexMaintenance(redis, "test:", NullLogger.Instance, clock);
            Assert.True(ExecutionContext.IsFlowSuppressed());
        }
        await cleanup.DisposeAsync();
        Assert.False(ExecutionContext.IsFlowSuppressed());
        Assert.Equal(0, clock.ActiveTimers);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task ServiceConstructor_OwnedTimersDoNotCaptureCallerContext(bool alreadySuppressed)
    {
        var clock = new TickTimeProvider();
        var (redis, _, _) = CreateRedis();
        RedisCacheService Create() => new(
            Substitute.For<IDistributedCache>(), Options.Create(new CacheOptions { Enabled = true }),
            NullLogger<RedisCacheService>.Instance, Substitute.For<IPerformanceMonitor>(), redis,
            timeProvider: clock);
        RedisCacheService service;
        if (alreadySuppressed)
        {
            using (ExecutionContext.SuppressFlow())
            {
                service = Create();
                Assert.True(ExecutionContext.IsFlowSuppressed());
            }
        }
        else
        {
            service = Create();
        }
        try
        {
            Assert.Equal(2, clock.ActiveTimers);
            Assert.True(clock.AllCreationsSuppressed);
            Assert.False(ExecutionContext.IsFlowSuppressed());
        }
        finally
        {
            await service.DisposeAsync();
        }
        Assert.Equal(0, clock.ActiveTimers);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void ServiceTimer_DoesNotRootInitialRequestState()
    {
        var (service, requestState) = CreateServiceWithRequestState();
        using (service)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.False(requestState.IsAlive);
            GC.KeepAlive(service);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RedisCacheService Service, WeakReference RequestState) CreateServiceWithRequestState()
    {
        var ambient = new AsyncLocal<object?> { Value = new object() };
        var reference = new WeakReference(ambient.Value);
        try
        {
            return (new RedisCacheService(
                null, Options.Create(new CacheOptions { Enabled = true }),
                NullLogger<RedisCacheService>.Instance, Substitute.For<IPerformanceMonitor>()), reference);
        }
        finally
        {
            ambient.Value = null;
        }
    }

    private static (IConnectionMultiplexer Redis, IDatabase Database, IServer Server) CreateRedis()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var server = Substitute.For<IServer>();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 6379);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        redis.GetEndPoints(Arg.Any<bool>()).Returns([endpoint]);
        redis.GetServer(endpoint, Arg.Any<object>()).Returns(server);
        server.IsConnected.Returns(true);
        server.ServerType.Returns(ServerType.Standalone);
        return (redis, database, server);
    }

    private static RedisResult Scan(string cursor, params string[] values)
        => RedisResult.Create([RedisResult.Create((RedisValue)cursor), RedisResult.Create(values.Select(value => RedisResult.Create((RedisValue)value)).ToArray())]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    internal sealed class TickTimeProvider : TimeProvider
    {
        private readonly List<TickTimer> _timers = new();
        public int ActiveTimers => _timers.Count(timer => !timer.IsDisposed);
        public bool AllCreationsSuppressed { get; private set; } = true;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            AllCreationsSuppressed &= ExecutionContext.IsFlowSuppressed();
            var timer = new TickTimer(callback, state, period);
            _timers.Add(timer);
            return timer;
        }
        public void Tick()
        {
            foreach (var timer in _timers.Where(timer => timer.Period == RedisCacheIndexMaintenance.Interval))
            {
                timer.Tick();
            }
        }
        private sealed class TickTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
        {
            public TimeSpan Period { get; } = period;
            public bool IsDisposed { get; private set; }
            public void Tick()
            {
                if (!IsDisposed)
                {
                    callback(state);
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

[Collection("Redis")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class RedisCacheIndexMaintenanceIntegrationTests(RedisFixture fixture)
{
    [IntegrationTest]
    [Operation(Operations.Cache, Operations.TestInfrastructure)]
    public async Task RestartedService_IdleSweepReclaimsExpiredScopedMembersAndPreservesRenewal()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var database = redis.GetDatabase();
        // Glob characters exercise literal prefix isolation during historical discovery.
        var prefix = $"index-proof:[{Guid.NewGuid():N}]:";
        const string storagePrefix = "application:";
        var clock = new RedisCacheIndexMaintenanceTests.TickTimeProvider();
        RedisCacheService Create(RedisCacheIndexMaintenanceTests.TickTimeProvider? provider = null) => new(
            Substitute.For<IDistributedCache>(),
            Options.Create(new CacheOptions { Enabled = true, EnableFallback = false, KeyPrefix = prefix }),
            NullLogger<RedisCacheService>.Instance,
            Substitute.For<IPerformanceMonitor>(), redis, storagePrefix, provider ?? clock);
        string Full(string key) => storagePrefix + prefix + key;
        (string? Schema, string Prefix)[] scopes = [(null, "scope:default:"), ("tenant-a", "scope:schema:tenant-a:")];
        var schemaContext = new SchemaContext();

        try
        {
            await using (var original = Create())
            {
                foreach (var scope in scopes)
                {
                    schemaContext.CurrentSchema = scope.Schema;
                    for (var index = 0; index < 20; index++)
                    {
                        await original.SetAsync(scope.Prefix + "expired:" + index, "payload", TimeSpan.FromMilliseconds(100));
                    }
                    await original.SetAsync(scope.Prefix + "renewed", "old", TimeSpan.FromMilliseconds(100));
                }
            }
            await Task.Delay(250);

            schemaContext.CurrentSchema = null;
            await using var restarted = Create();
            var peerClock = new RedisCacheIndexMaintenanceTests.TickTimeProvider();
            await using var peer = Create(peerClock);
            foreach (var scope in scopes)
            {
                schemaContext.CurrentSchema = scope.Schema;
                await peer.SetAsync(scope.Prefix + "renewed", "new", TimeSpan.FromMinutes(1));
                Assert.Equal(21, await database.SetLengthAsync(Full(scope.Prefix + "__cache_key_index__")));
            }
            schemaContext.CurrentSchema = null;
            // No original key is read/retried, and the restarted service has no local
            // write metadata for expired members. Only the owned idle timer cleans them.
            for (var tick = 0; tick < 20; tick++)
            {
                clock.Tick();
                peerClock.Tick();
                await Task.Delay(25);
                if (await database.SetLengthAsync(Full(scopes[0].Prefix + "__cache_key_index__")) == 1 &&
                    await database.SetLengthAsync(Full(scopes[1].Prefix + "__cache_key_index__")) == 1)
                {
                    break;
                }
            }
            foreach (var scope in scopes)
            {
                schemaContext.CurrentSchema = scope.Schema;
                Assert.Equal(1, await database.SetLengthAsync(Full(scope.Prefix + "__cache_key_index__")));
                Assert.True(await database.SetContainsAsync(Full(scope.Prefix + "__cache_key_index__"), Full(scope.Prefix + "renewed")));
                Assert.Equal("new", await restarted.GetAsync<string>(scope.Prefix + "renewed"));
                Assert.False(await database.KeyExistsAsync(Full(scope.Prefix + "expired:0")));
            }
            Assert.False(restarted.IsUsingFallback);
        }
        finally
        {
            schemaContext.CurrentSchema = null;
            foreach (var scope in scopes)
            {
                await database.KeyDeleteAsync([Full(scope.Prefix + "__cache_key_index__"), Full(scope.Prefix + "renewed")]);
            }
        }
    }
}
