// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Honua.Db.Postgres.Security.Tests;

public sealed partial class SecureConnectionDataSourceCacheTests
{
    [SecurityTest]
    [UnitTest]
    public async Task IdlePruning_WaitsForLastLeaseThenFullIdleLifetime_AndAllowsRecreation()
    {
        var clock = new PoolClock();
        using var cache = new SecureConnectionDataSourceCache(IdleConfiguration(), timeProvider: clock);
        using var first = cache.Acquire("source", SampleConnectionString);
        using var second = cache.Acquire("source", SampleConnectionString);
        clock.Advance(TimeSpan.FromSeconds(20));
        first.Dispose();
        clock.Advance(TimeSpan.FromSeconds(20));
        await AssertUsableAsync(second.DataSource);
        second.Dispose();
        clock.Advance(TimeSpan.FromSeconds(9));
        await AssertUsableAsync(second.DataSource);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertDisposedAsync(second.DataSource);
        using var replacement = cache.Acquire("source", SampleConnectionString);
        Assert.NotSame(second.DataSource, replacement.DataSource);
        await AssertUsableAsync(replacement.DataSource);
    }

    [SecurityTest]
    [UnitTest]
    public async Task RetireConnection_DetachesMatchingPools_PreservesPinsAndUnrelatedSources()
    {
        var id = Guid.NewGuid();
        using var cache = new SecureConnectionDataSourceCache(IdleConfiguration());
        using var named = cache.Acquire("registered", SampleConnectionString);
        using var bound = cache.Acquire("bound-id:" + id.ToString("D"), SampleConnectionString, preservePrimarySchema: false);
        using var other = cache.Acquire("other", SampleConnectionString);
        named.Dispose();
        cache.RetireConnection(id, "registered");
        await AssertDisposedAsync(named.DataSource);
        await AssertUsableAsync(bound.DataSource);
        await AssertUsableAsync(other.DataSource);
        bound.Dispose();
        await AssertDisposedAsync(bound.DataSource);
        using var replacement = cache.Acquire("registered", SampleConnectionString);
        Assert.NotSame(named.DataSource, replacement.DataSource);
    }

    [SecurityTest]
    [UnitTest]
    public async Task IdlePruning_RetiresRemoteLegacyAndLateOpenPoolsWithoutTombstones()
    {
        var clock = new PoolClock();
        using var local = new SecureConnectionDataSourceCache(IdleConfiguration(), timeProvider: clock);
        using var remote = new SecureConnectionDataSourceCache(IdleConfiguration(), timeProvider: clock);
        var id = Guid.NewGuid();
        using var remoteLease = remote.Acquire("registered", SampleConnectionString);
        using var legacy = remote.Acquire("bound-string:legacy-digest", SampleConnectionString, preservePrimarySchema: false);
        local.RetireConnection(id, "registered");
        // A request can finish resolving before deletion but acquire its pool afterward.
        using var late = local.Acquire("bound-id:" + id.ToString("D"), SampleConnectionString, preservePrimarySchema: false);
        remoteLease.Dispose();
        legacy.Dispose();
        late.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));
        await AssertDisposedAsync(remoteLease.DataSource);
        await AssertDisposedAsync(legacy.DataSource);
        await AssertDisposedAsync(late.DataSource);
        using var recreated = local.Acquire("registered", SampleConnectionString);
        await AssertUsableAsync(recreated.DataSource);
    }

    [SecurityTest]
    [UnitTest]
    public async Task Dispose_StopsMaintenance_AndDefersPinnedSourceDisposal()
    {
        var clock = new PoolClock();
        using var cache = new SecureConnectionDataSourceCache(IdleConfiguration(), timeProvider: clock);
        using var lease = cache.Acquire("source", SampleConnectionString);
        cache.Dispose();
        Assert.All(clock.Timers, timer => Assert.True(timer.IsDisposed));
        clock.Advance(TimeSpan.FromSeconds(100));
        await AssertUsableAsync(lease.DataSource);
        lease.Dispose();
        await AssertDisposedAsync(lease.DataSource);
    }

    [SecurityTest]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceCallback_DoesNotCaptureCreatingRequestContext(bool alreadySuppressed)
    {
        var requestContext = new AsyncLocal<string?> { Value = "creating-request" };
        var clock = new ContextObservingClock(requestContext);
        SecureConnectionDataSourceCache cache;
        if (alreadySuppressed)
        {
            using (ExecutionContext.SuppressFlow())
            {
                cache = new SecureConnectionDataSourceCache(IdleConfiguration(), timeProvider: clock);
            }
        }
        else
        {
            cache = new SecureConnectionDataSourceCache(IdleConfiguration(), timeProvider: clock);
        }

        using (cache)
        {
            requestContext.Value = null;
            Assert.Null(await clock.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    private sealed class ContextObservingClock(AsyncLocal<string?> requestContext) : TimeProvider
    {
        public TaskCompletionSource<string?> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => base.CreateTimer(value =>
            {
                Observed.TrySetResult(requestContext.Value);
                callback(value);
            }, state, dueTime, period);
    }

    [SecurityTest]
    [UnitTest]
    public async Task DisposeAsync_WaitsForOutstandingMaintenance_WithoutHoldingLookupLock()
    {
        using var releaseCallback = new ManualResetEventSlim();
        var clock = new PausingMaintenanceClock(releaseCallback);
        await using var cache = new SecureConnectionDataSourceCache(IdleConfiguration(), timeProvider: clock);
        using var lease = cache.Acquire("source", SampleConnectionString);
        Task? disposal = null;
        try
        {
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposal = cache.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            // Returning a pin takes the lookup lock. It must remain independent of
            // the callback that asynchronous disposal is waiting to finish.
            await Task.Run(lease.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            await AssertDisposedAsync(lease.DataSource);
        }
        finally
        {
            releaseCallback.Set();
            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class PausingMaintenanceClock(ManualResetEventSlim releaseCallback) : TimeProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => base.CreateTimer(value =>
            {
                Entered.TrySetResult();
                if (releaseCallback.Wait(TimeSpan.FromSeconds(15)))
                {
                    callback(value);
                }
            }, state, dueTime, period);
    }

    private static IConfiguration IdleConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Limits:Connections:ConnectionIdleLifetimeSeconds"] = "10",
            ["Limits:Connections:ConnectionPruningIntervalSeconds"] = "1"
        }).Build();

    private sealed class PoolClock : TimeProvider
    {
        private long _timestamp;
        public List<PoolTimer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), dueTime);
            Assert.Equal(dueTime, period);
            var timer = new PoolTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
            foreach (var timer in Timers)
            {
                timer.Fire();
            }
        }
    }

    private sealed class PoolTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Fire()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }
        public void Dispose() => IsDisposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
