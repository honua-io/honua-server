// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for DistributedCacheRefreshCoordinator — validates distributed coordination with Redis.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class DistributedCacheRefreshCoordinatorTests : IDisposable
{
    private readonly DistributedCacheRefreshCoordinator _coordinator;
    private readonly IConnectionMultiplexer _redis;
    private readonly CancellationTokenSource _cts;

    public DistributedCacheRefreshCoordinatorTests()
    {
        var options = Options.Create(new CacheOptions
        {
            BackgroundRefreshEnabled = true,
            MaxConcurrentRefreshes = 2,
            RefreshTimeoutSeconds = 5
        });

        _redis = Substitute.For<IConnectionMultiplexer>();
        _redis.IsConnected.Returns(false); // Test fallback mode by default

        _coordinator = new DistributedCacheRefreshCoordinator(
            options,
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance,
            _redis);

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
    public void Constructor_WithoutRedis_EnablesFallbackMode()
    {
        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions { BackgroundRefreshEnabled = true }),
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        coordinator.IsDistributed.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Constructor_WithConnectedRedis_EnablesDistributedMode()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase(Arg.Any<int>()).Returns(Substitute.For<IDatabase>());
        redis.GetSubscriber().Returns(Substitute.For<ISubscriber>());

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions { BackgroundRefreshEnabled = true }),
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance,
            redis);

        coordinator.IsDistributed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Constructor_WithoutAllowFallback_ThrowsWhenRedisUnavailable()
    {
        var action = () => new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions { BackgroundRefreshEnabled = true }),
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance,
            null,
            allowFallback: false);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Redis is required*");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryEnqueueRefresh_FallbackMode_NewKey_ReturnsTrue()
    {
        var result = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        result.Should().BeTrue();
        _coordinator.QueueDepth.Should().BeGreaterThan(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryEnqueueRefresh_FallbackMode_DuplicateKey_ReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        var duplicate = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        duplicate.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryEnqueueRefresh_FallbackMode_DifferentKeys_BothSucceed()
    {
        var first = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        var second = _coordinator.TryEnqueueRefresh("layer:2", _ => Task.CompletedTask);

        first.Should().BeTrue();
        second.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void NotifyInvalidation_FallbackMode_MarksKeyInvalidated()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        _coordinator.NotifyInvalidation("layer:1");

        _coordinator.WasInvalidated("layer:1").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_FallbackMode_AfterInvalidation_ReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        _coordinator.NotifyInvalidation("layer:1");

        var claimed = _coordinator.TryClaimWriteBack("layer:1");

        claimed.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_FallbackMode_BeforeInvalidation_ReturnsTrue()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        var claimed = _coordinator.TryClaimWriteBack("layer:1");

        claimed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void NotifyInvalidation_FallbackMode_WithoutPendingRefresh_IsIgnored()
    {
        // No pending refresh → invalidation flag must not be tracked (avoids stale markers
        // that would permanently block a later TryEnqueueRefresh for the same key).
        _coordinator.NotifyInvalidation("layer:1");

        _coordinator.WasInvalidated("layer:1").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void NotifyInvalidation_FallbackMode_WithoutPendingRefresh_DoesNotBlockLaterEnqueue()
    {
        // Invalidation for a non-pending key must not leave state that blocks a future enqueue.
        _coordinator.NotifyInvalidation("layer:1");

        var enqueued = _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        enqueued.Should().BeTrue("invalidation without a pending refresh must not create stale markers");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task NotifyInvalidationClusterWideAsync_WithoutRedis_FallsBackToLocal()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        await _coordinator.NotifyInvalidationClusterWideAsync("layer:1");

        _coordinator.WasInvalidated("layer:1").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_TracksSuccessMetrics()
    {
        var refreshed = new TaskCompletionSource<bool>();
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 5
            }),
            performanceMonitor,
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            refreshed.SetResult(true);
            return Task.CompletedTask;
        });

        // Start the background service
        _ = coordinator.StartAsync(_cts.Token);

        // Wait for refresh to complete
        var completed = await Task.WhenAny(refreshed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(refreshed.Task, "the refresh callback should have been invoked");

        // Wait for metrics to be recorded
        await Task.Delay(100);

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

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 5
            }),
            performanceMonitor,
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

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

        // Wait for processing to complete
        await Task.Delay(500);

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

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 5
            }),
            performanceMonitor,
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

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

        // Wait for error processing
        await Task.Delay(100);

        coordinator.FailureCount.Should().Be(1);
        coordinator.SuccessCount.Should().Be(0);

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_WithTimeout_TracksFailureMetrics()
    {
        var refreshStarted = new TaskCompletionSource<bool>();
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 1 // Short timeout for test
            }),
            performanceMonitor,
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        coordinator.TryEnqueueRefresh("layer:1", async ct =>
        {
            refreshStarted.SetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // Will timeout
        });

        // Start the background service
        _ = coordinator.StartAsync(_cts.Token);

        // Wait for refresh to start
        await refreshStarted.Task;

        // Wait for timeout + processing
        await Task.Delay(TimeSpan.FromSeconds(2));

        coordinator.FailureCount.Should().Be(1);

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_FallbackMode_FailedRefresh_TemporarilyBacksOffRetry()
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

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask)
            .Should().BeTrue("the retry backoff should eventually expire");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefresh_FallbackMode_AfterRefreshCompletes_KeyCanBeReenqueued()
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
}
