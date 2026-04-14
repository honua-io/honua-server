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
[Protocol(Protocols.TestQuality)]
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
}
