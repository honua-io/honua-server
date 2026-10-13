// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class CacheRefreshBackoffTests
{
    [UnitTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Operation(Operations.Cache)]
    public async Task Worker_IdleAfterDistinctFailures_ReclaimsExpiredKeys(bool distributed, bool timeout)
    {
        var clock = new CleanupTimeProvider();
        using var service = CreateService(distributed, clock);
        var coordinator = (ICacheRefreshCoordinator)service;
        var backoff = GetBackoff(service);
        for (var index = 0; index < 100; index++)
        {
            Assert.True(coordinator.TryEnqueueRefresh($"failed-{index}", _ => timeout
                ? Task.FromException(new OperationCanceledException())
                : Task.FromException(new InvalidOperationException("Expected refresh failure"))));
        }

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => coordinator.FailureCount == 100 && coordinator.QueueDepth == 0);
            Assert.Equal(100, backoff.Count);
            Assert.False(coordinator.TryEnqueueRefresh("failed-0", _ => Task.CompletedTask));

            clock.Advance(TimeSpan.FromSeconds(1));

            Assert.Equal(0, backoff.Count);
            Assert.True(coordinator.TryEnqueueRefresh("failed-0", _ => Task.CompletedTask));
            await WaitUntilAsync(() => coordinator.SuccessCount == 1 && coordinator.QueueDepth == 0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        Assert.Equal(0, clock.ActiveTimers);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task Worker_UnrelatedTraffic_ReclaimsOldKeysAndPreservesRecentBackoff(bool distributed)
    {
        var clock = new CleanupTimeProvider();
        using var service = CreateService(distributed, clock);
        var coordinator = (ICacheRefreshCoordinator)service;
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(coordinator.TryEnqueueRefresh("old", _ => Task.FromException(new InvalidOperationException())));
            await WaitUntilAsync(() => coordinator.FailureCount == 1 && coordinator.QueueDepth == 0);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            Assert.True(coordinator.TryEnqueueRefresh("recent", _ => Task.FromException(new InvalidOperationException())));
            await WaitUntilAsync(() => coordinator.FailureCount == 2 && coordinator.QueueDepth == 0);
            Assert.True(coordinator.TryEnqueueRefresh("unrelated", _ => Task.CompletedTask));
            await WaitUntilAsync(() => coordinator.SuccessCount == 1 && coordinator.QueueDepth == 0);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            Assert.Equal(1, GetBackoff(service).Count);
            Assert.False(coordinator.TryEnqueueRefresh("recent", _ => Task.CompletedTask));
            clock.Advance(TimeSpan.FromMilliseconds(500) - TimeSpan.FromTicks(1));
            Assert.False(coordinator.TryEnqueueRefresh("recent", _ => Task.CompletedTask));
            clock.Advance(TimeSpan.FromTicks(1));
            Assert.True(coordinator.TryEnqueueRefresh("recent", _ => Task.CompletedTask));
            await WaitUntilAsync(() => coordinator.SuccessCount == 2 && coordinator.QueueDepth == 0);
            Assert.Equal(0, GetBackoff(service).Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task NotifyInvalidation_RecentFailure_AllowsImmediateRetry(bool distributed)
    {
        var clock = new CleanupTimeProvider();
        using var service = CreateService(distributed, clock);
        var coordinator = (ICacheRefreshCoordinator)service;
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(coordinator.TryEnqueueRefresh("invalidated", _ => Task.FromException(new InvalidOperationException())));
            await WaitUntilAsync(() => coordinator.FailureCount == 1 && coordinator.QueueDepth == 0);
            coordinator.NotifyInvalidation("invalidated");
            Assert.Equal(0, GetBackoff(service).Count);
            Assert.True(coordinator.TryEnqueueRefresh("invalidated", _ => Task.CompletedTask));
            await WaitUntilAsync(() => coordinator.SuccessCount == 1 && coordinator.QueueDepth == 0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Operation(Operations.Cache)]
    public async Task Worker_StopOrDispose_DisposesCleanupTimer(bool distributed, bool dispose)
    {
        var clock = new CleanupTimeProvider();
        using var service = CreateService(distributed, clock);
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => clock.ActiveTimers == 1);
        var backoff = GetBackoff(service);
        backoff.Set("retained-at-stop");
        Assert.Equal(1, backoff.Count);
        if (dispose)
        {
            service.Dispose();
            // Dispose cancels the worker; its asynchronous unwind owns timer disposal.
            await WaitUntilAsync(() => service.ExecuteTask!.IsCompleted);
        }
        else
        {
            await service.StopAsync(CancellationToken.None);
        }
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(0, backoff.Count);
        backoff.Set("late-failure");
        Assert.Equal(0, backoff.Count);
        var callbacks = clock.CallbackCount;
        clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(callbacks, clock.CallbackCount);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task Worker_CallbackFailsAfterStop_DoesNotRetainBackoff(bool distributed)
    {
        var clock = new CleanupTimeProvider();
        using var service = CreateService(distributed, clock);
        var coordinator = (ICacheRefreshCoordinator)service;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(coordinator.TryEnqueueRefresh("late-failure", async _ =>
        {
            entered.SetResult();
            await release.Task;
            throw new InvalidOperationException("Callback finished after worker shutdown");
        }));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);
            Assert.Equal(0, clock.ActiveTimers);
            release.SetResult();
            await WaitUntilAsync(() => coordinator.FailureCount == 1 && coordinator.QueueDepth == 0);
            Assert.Equal(0, GetBackoff(service).Count);
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task Worker_Disabled_DoesNotCreateCleanupTimer(bool distributed)
    {
        var clock = new CleanupTimeProvider();
        using var service = CreateService(distributed, clock, enabled: false);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        Assert.Equal(0, clock.CreatedTimers);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task Expiry_ConcurrentRenewal_PreservesNewDeadline(bool sweep)
    {
        var clock = new CleanupTimeProvider();
        using var backoff = new CacheRefreshBackoff(clock);
        backoff.Set("renewed");
        clock.Advance(TimeSpan.FromSeconds(1));
        using var resumeExpiry = new ManualResetEventSlim();
        var expiryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnNextRead(() =>
        {
            expiryRead.SetResult();
            Assert.True(resumeExpiry.Wait(TimeSpan.FromSeconds(10)));
        });
        var expiry = Task.Run(() =>
        {
            if (sweep)
            {
                backoff.RemoveExpiredEntries();
            }
            else
            {
                Assert.False(backoff.IsActive("renewed"));
            }
        });
        Task? renewal = null;
        try
        {
            await expiryRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            renewal = Task.Run(() =>
            {
                renewalStarted.SetResult();
                backoff.Set("renewed");
            });
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            resumeExpiry.Set();
            await expiry;
            if (renewal != null)
            {
                await renewal;
            }
        }
        Assert.True(backoff.IsActive("renewed"));
        Assert.Equal(1, backoff.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(backoff.IsActive("renewed"));
        Assert.Equal(0, backoff.Count);
    }

    private static BackgroundService CreateService(bool distributed, TimeProvider clock, bool enabled = true)
    {
        var options = Options.Create(new CacheOptions
        {
            BackgroundRefreshEnabled = enabled,
            MaxConcurrentRefreshes = 2,
            RefreshTimeoutSeconds = 5
        });
        var monitor = Substitute.For<IPerformanceMonitor>();
#pragma warning disable CS0618 // Both retained coordinator implementations own retry-backoff state.
        return distributed
            ? new DistributedCacheRefreshCoordinator(options, monitor, NullLogger<DistributedCacheRefreshCoordinator>.Instance, timeProvider: clock)
            : new CacheRefreshCoordinator(options, monitor, NullLogger<CacheRefreshCoordinator>.Instance, clock);
#pragma warning restore CS0618
    }

    private static CacheRefreshBackoff GetBackoff(BackgroundService service) =>
        (CacheRefreshBackoff)service.GetType().GetField("_retryBackoff", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class CleanupTimeProvider : TimeProvider
    {
        private long _utcTicks = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        private readonly System.Collections.Concurrent.ConcurrentBag<CleanupTimer> _timers = [];
        private Action? _beforeNextRead;
        private int _callbackCount;

        public int ActiveTimers => _timers.Count(timer => !timer.IsDisposed);
        public int CreatedTimers => _timers.Count;
        public int CallbackCount => Volatile.Read(ref _callbackCount);
        public void OnNextRead(Action action) => Interlocked.Exchange(ref _beforeNextRead, action);

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref _beforeNextRead, null)?.Invoke();
            return new DateTimeOffset(Volatile.Read(ref _utcTicks), TimeSpan.Zero);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new CleanupTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            var now = Interlocked.Add(ref _utcTicks, duration.Ticks);
            foreach (var timer in _timers)
            {
                timer.FireIfDue(now);
            }
        }

        private sealed class CleanupTimer(CleanupTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private long _nextTicks = Volatile.Read(ref owner._utcTicks) + dueTime.Ticks;
            private int _disposed;
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue(long now)
            {
                if (!IsDisposed && now >= _nextTicks)
                {
                    _nextTicks = now + period.Ticks;
                    Interlocked.Increment(ref owner._callbackCount);
                    callback(state);
                }
            }
        }
    }
}
