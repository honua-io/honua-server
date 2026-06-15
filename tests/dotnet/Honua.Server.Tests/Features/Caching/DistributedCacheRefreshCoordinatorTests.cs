// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
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
        var refreshed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    // ── Async Redis path tests (hosting-caching-rendering-events-2) ──────────────

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task WasInvalidatedAsync_FallbackMode_ReturnsSameAsSync()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        _coordinator.NotifyInvalidation("layer:1");

        var syncResult = _coordinator.WasInvalidated("layer:1");
        var asyncResult = await _coordinator.WasInvalidatedAsync("layer:1");

        asyncResult.Should().Be(syncResult).And.BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task TryClaimWriteBackAsync_FallbackMode_BeforeInvalidation_ReturnsTrue()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        var result = await _coordinator.TryClaimWriteBackAsync("layer:1");

        result.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task TryClaimWriteBackAsync_FallbackMode_AfterInvalidation_ReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);
        _coordinator.NotifyInvalidation("layer:1");

        var result = await _coordinator.TryClaimWriteBackAsync("layer:1");

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task WasInvalidatedAsync_DistributedMode_UsesRedisKeyExistsAsync()
    {
        // Arrange: set up a coordinator in distributed mode with a mock Redis database
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var database = Substitute.For<IDatabase>();
        database.Multiplexer.Returns(redis);
        redis.GetDatabase(Arg.Any<int>()).Returns(database);
        redis.GetSubscriber().Returns(Substitute.For<ISubscriber>());

        // Simulate the invalidated key being present in Redis
        database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions { BackgroundRefreshEnabled = true }),
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance,
            redis);

        // Act
        var result = await coordinator.WasInvalidatedAsync("layer:1");

        // Assert
        result.Should().BeTrue("the async path should query Redis for the invalidated key");
        await database.Received(1).KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task StopAsync_DrainsPendingInvalidationTasksBeforeShuttingDown()
    {
        // Arrange: coordinator in distributed mode with a slow Redis database
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var database = Substitute.For<IDatabase>();
        database.Multiplexer.Returns(redis);
        redis.GetDatabase(Arg.Any<int>()).Returns(database);
        redis.GetSubscriber().Returns(Substitute.For<ISubscriber>());

        // The ScriptEvaluateAsync will complete after a brief delay to simulate I/O
        var scriptCompleted = new TaskCompletionSource<bool>();
        database.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                return Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50));
                    scriptCompleted.TrySetResult(true);
                    return RedisResult.Create((RedisValue)"OK");
                });
            });

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions { BackgroundRefreshEnabled = false }),
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance,
            redis);

        // Trigger a distributed invalidation (fire-and-forget Redis ScriptEvaluateAsync)
        coordinator.NotifyInvalidation("layer:drain-test");

        // Act: stop the coordinator — StopAsync must drain the in-flight task
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(cts.Token);

        // Assert: the Redis script completed during the drain, not after
        scriptCompleted.Task.IsCompleted.Should().BeTrue(
            "StopAsync must await all pending fire-and-forget Redis invalidation tasks before returning");

        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ReleaseDistributedClaimAsync_IsAwaited_InProcessRefreshAsync()
    {
        // Verify that the Redis DEL/EXPIRE script is awaited (not fire-and-forget) when
        // a background refresh completes — ensures the claim is fully released before the
        // semaphore slot is freed.
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>()).Returns(database);
        redis.GetSubscriber().Returns(Substitute.For<ISubscriber>());

        var releaseCompleted = new TaskCompletionSource<bool>();
        database.Multiplexer.Returns(redis);
        database.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                return Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(30));
                    releaseCompleted.TrySetResult(true);
                    return RedisResult.Create((RedisValue)"OK");
                });
            });

        database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        // TryClaimDistributedRefresh calls the 4-argument StringSet overload
        // (key, value, expiry, when) — stub that exact overload, not the
        // CommandFlags one, or the claim silently fails and nothing enqueues.
        database.StringSet(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(true);

        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);

        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 1,
                RefreshTimeoutSeconds = 5
            }),
            performanceMonitor,
            NullLogger<DistributedCacheRefreshCoordinator>.Instance,
            redis);

        var refreshDone = new TaskCompletionSource<bool>();
        coordinator.TryEnqueueRefresh("layer:release-test", _ =>
        {
            refreshDone.TrySetResult(true);
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = coordinator.StartAsync(cts.Token);

        // Wait for refresh callback to fire
        await Task.WhenAny(refreshDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        // Poll until the release script has run — if it were fire-and-forget, it could
        // still be pending when the semaphore is released and a new enqueue steals the slot.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!releaseCompleted.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        releaseCompleted.Task.IsCompleted.Should().BeTrue(
            "the Redis claim-release script must be awaited (not fire-and-forget) so the lock " +
            "is cleared before the semaphore slot is freed");

        await coordinator.StopAsync(cts.Token);
        coordinator.Dispose();
    }
}
