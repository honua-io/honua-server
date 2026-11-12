// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class CacheRefreshCoordinatorTests : IDisposable
{
    private readonly ManualTimeProvider _timeProvider = new();
    private readonly CacheRefreshCoordinator _coordinator;
    private readonly CancellationTokenSource _cts;

    public CacheRefreshCoordinatorTests()
    {
        var options = Options.Create(new CacheOptions
        {
            BackgroundRefreshEnabled = true,
            MaxConcurrentRefreshes = 2,
            RefreshTimeoutSeconds = 5
        });

        _coordinator = new CacheRefreshCoordinator(
            options,
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<CacheRefreshCoordinator>.Instance,
            _timeProvider);

        _cts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryEnqueueRefresh_NewKey_ReturnsTrue()
    {
        var result = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        result.Should().BeTrue();
        _coordinator.QueueDepth.Should().BeGreaterThan(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryEnqueueRefresh_DuplicateKey_ReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        var duplicate = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        duplicate.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryEnqueueRefresh_DifferentKeys_BothSucceed()
    {
        var first = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        var second = _coordinator.TryEnqueueRefresh("layer:2", _ => Task.CompletedTask);

        first.Should().BeTrue();
        second.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void NotifyInvalidation_MarksKeyInvalidated()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        _coordinator.NotifyInvalidation("layer:1");

        _coordinator.WasInvalidated("layer:1").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_AfterInvalidation_ReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        _coordinator.NotifyInvalidation("layer:1");

        var claimed = _coordinator.TryClaimWriteBack("layer:1");

        claimed.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_BeforeInvalidation_ReturnsTrue()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        var claimed = _coordinator.TryClaimWriteBack("layer:1");

        claimed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void NotifyInvalidation_WithoutPendingRefresh_IsIgnored()
    {
        // No pending refresh → invalidation flag must not be tracked (avoids stale markers
        // that would permanently block a later TryEnqueueRefresh for the same key).
        _coordinator.NotifyInvalidation("layer:1");

        _coordinator.WasInvalidated("layer:1").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void NotifyInvalidation_WithoutPendingRefresh_DoesNotBlockLaterEnqueue()
    {
        // Invalidation for a non-pending key must not leave state that blocks a future enqueue.
        _coordinator.NotifyInvalidation("layer:1");

        var enqueued = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        enqueued.Should().BeTrue("invalidation without a pending refresh must not create stale markers");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_TracksSuccessMetrics()
    {
        var refreshed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);

        var coordinator = new CacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 5
            }),
            performanceMonitor,
            NullLogger<CacheRefreshCoordinator>.Instance);

        coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            refreshed.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Start the background service
        _ = coordinator.StartAsync(_cts.Token);

        // Wait for refresh to complete
        var completed = await Task.WhenAny(refreshed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(refreshed.Task, "the refresh callback should have been invoked");

        // Poll for the success bookkeeping to settle instead of a fixed wait.
        // The refresh callback signals before the background task records metrics,
        // and that gap can exceed 100ms under CI load.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && coordinator.SuccessCount == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        coordinator.SuccessCount.Should().Be(1);
        coordinator.FailureCount.Should().Be(0);
        coordinator.SkippedCount.Should().Be(0);

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_WithInvalidation_TracksSkippedMetrics()
    {
        var refreshStarted = new TaskCompletionSource<bool>();
        var refreshCanProceed = new TaskCompletionSource<bool>();
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);

        var coordinator = new CacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 5
            }),
            performanceMonitor,
            NullLogger<CacheRefreshCoordinator>.Instance);

        coordinator.TryEnqueueRefresh("layer:1", async _ =>
        {
            refreshStarted.SetResult(true);
            await refreshCanProceed.Task; // Wait for test to invalidate
        });

        // Start the background service
        _ = coordinator.StartAsync(_cts.Token);

        // Wait for refresh to start
        await refreshStarted.Task;

        // Invalidate while refresh is in progress
        coordinator.NotifyInvalidation("layer:1");

        // Allow refresh to complete
        refreshCanProceed.SetResult(true);

        // Poll for the skipped bookkeeping to settle instead of a fixed 500ms wait.
        // Under CI load, the async invalidation → skipped-counter path can take more
        // than 500ms to propagate, which caused intermittent failures showing
        // SkippedCount=0 when the refresh hadn't yet finalized its skip accounting.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && coordinator.SkippedCount == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        coordinator.SkippedCount.Should().Be(1);
        coordinator.SuccessCount.Should().Be(0);

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_WithException_TracksFailureMetrics()
    {
        var refreshed = new TaskCompletionSource<bool>();
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);

        var coordinator = new CacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 5
            }),
            performanceMonitor,
            NullLogger<CacheRefreshCoordinator>.Instance);

        coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            refreshed.SetResult(true);
            throw new InvalidOperationException("Test failure");
        });

        // Start the background service
        _ = coordinator.StartAsync(_cts.Token);

        // Wait for refresh to complete
        var completed = await Task.WhenAny(refreshed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(refreshed.Task);

        // Poll for the failure bookkeeping to settle instead of a fixed wait.
        // The refresh callback signals before the background task reaches its
        // exception handler, and that gap can exceed 100ms under CI load.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && coordinator.FailureCount == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        coordinator.FailureCount.Should().Be(1);
        coordinator.SuccessCount.Should().Be(0);

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    [FlakyTest("CI-load timing dependence on async failure-bookkeeping settle (commit 398aab8a stabilised; same flake family as TracksSkippedMetrics — watch for regressions). Tracked in #812.")]
    public async Task ProcessRefresh_WithTimeout_TracksFailureMetrics()
    {
        var refreshStarted = new TaskCompletionSource<bool>();
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);

        var coordinator = new CacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 1 // Short timeout for test
            }),
            performanceMonitor,
            NullLogger<CacheRefreshCoordinator>.Instance);

        coordinator.TryEnqueueRefresh("layer:1", async ct =>
        {
            refreshStarted.SetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // Will timeout
        });

        // Start the background service
        _ = coordinator.StartAsync(_cts.Token);

        // Wait for refresh to start
        await refreshStarted.Task;

        // Poll for the failure bookkeeping to settle instead of a fixed wait.
        // The configured RefreshTimeoutSeconds is 1, but propagating the
        // timeout cancellation into the FailureCount counter takes additional
        // async hops that occasionally exceed the prior 2-second fixed wait
        // under CI load. Bound the wait at 10s with a 50ms tick.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && coordinator.FailureCount == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        coordinator.FailureCount.Should().Be(1);

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_FailedRefresh_TemporarilyBacksOffRetry()
    {
        var refreshAttempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            refreshAttempted.SetResult(true);
            throw new InvalidOperationException("simulated failure");
        });

        _ = _coordinator.StartAsync(_cts.Token);

        var attempted = await Task.WhenAny(refreshAttempted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        attempted.Should().Be(refreshAttempted.Task, "the initial refresh should have run");

        for (int i = 0; i < 100 && (_coordinator.FailureCount < 1 || _coordinator.QueueDepth > 0); i++)
        {
            await Task.Delay(10);
        }

        _coordinator.FailureCount.Should().Be(1);
        _coordinator.QueueDepth.Should().Be(0);

        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask)
            .Should().BeFalse("recent failures should back off immediate retries");

        // Runner scheduling must not consume the backoff before we can assert it.
        // Check both sides of the exact one-second boundary using controlled time.
        _timeProvider.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));

        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask)
            .Should().BeFalse("the retry backoff should remain active until its deadline");

        _timeProvider.Advance(TimeSpan.FromTicks(1));

        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask)
            .Should().BeTrue("the retry backoff should expire at its deadline");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_AfterRefreshCompletes_KeyCanBeReenqueued()
    {
        var callCount = 0;
        var firstRefreshDone = new TaskCompletionSource<bool>();
        var secondRefreshDone = new TaskCompletionSource<bool>();

        _ = _coordinator.StartAsync(_cts.Token);

        _coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref callCount);
            firstRefreshDone.SetResult(true);
            return Task.CompletedTask;
        });

        var completed = await Task.WhenAny(firstRefreshDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(firstRefreshDone.Task, "the first refresh should have completed");

        for (int i = 0; i < 100 && _coordinator.QueueDepth > 0; i++)
        {
            await Task.Delay(10);
        }

        var result = _coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref callCount);
            secondRefreshDone.SetResult(true);
            return Task.CompletedTask;
        });

        result.Should().BeTrue();

        var completed2 = await Task.WhenAny(secondRefreshDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed2.Should().Be(secondRefreshDone.Task, "the second refresh should have completed");
        callCount.Should().Be(2);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void QueueDepth_InitiallyZero()
    {
        _coordinator.QueueDepth.Should().Be(0);
        _coordinator.SuccessCount.Should().Be(0);
        _coordinator.FailureCount.Should().Be(0);
        _coordinator.SkippedCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task WasInvalidatedAsync_ReturnsSameAsSync()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        _coordinator.NotifyInvalidation("layer:1");

        var syncResult = _coordinator.WasInvalidated("layer:1");
        var asyncResult = await ((ICacheRefreshCoordinator)_coordinator).WasInvalidatedAsync("layer:1");

        asyncResult.Should().Be(syncResult).And.BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task TryClaimWriteBackAsync_BeforeInvalidation_ReturnsTrue()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        var result = await ((ICacheRefreshCoordinator)_coordinator).TryClaimWriteBackAsync("layer:1");

        result.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task TryClaimWriteBackAsync_AfterInvalidation_ReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        _coordinator.NotifyInvalidation("layer:1");

        var result = await ((ICacheRefreshCoordinator)_coordinator).TryClaimWriteBackAsync("layer:1");

        result.Should().BeFalse();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UnixEpoch.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }
}
