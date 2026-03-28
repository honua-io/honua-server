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

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for CacheRefreshCoordinator — validates deduplication, bounded concurrency, and failure isolation.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class CacheRefreshCoordinatorTests : IDisposable
{
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
            NullLogger<CacheRefreshCoordinator>.Instance);

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
    public async Task ExecuteAsync_ProcessesEnqueuedItems()
    {
        var refreshed = new TaskCompletionSource<bool>();

        _coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            refreshed.SetResult(true);
            return Task.CompletedTask;
        });

        // Start the background service
        _ = _coordinator.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(refreshed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(refreshed.Task, "the refresh callback should have been invoked");

        // Wait for post-callback bookkeeping (Interlocked.Increment + _pendingKeys removal)
        for (int i = 0; i < 100 && _coordinator.SuccessCount < 1; i++)
            await Task.Delay(10);

        _coordinator.SuccessCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ExecuteAsync_FailedRefresh_CountsAsFailure()
    {
        var refreshAttempted = new TaskCompletionSource<bool>();

        _coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            refreshAttempted.SetResult(true);
            throw new InvalidOperationException("simulated failure");
        });

        _ = _coordinator.StartAsync(_cts.Token);

        await Task.WhenAny(refreshAttempted.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        // Wait for post-callback bookkeeping (Interlocked.Increment + _pendingKeys removal)
        for (int i = 0; i < 100 && _coordinator.FailureCount < 1; i++)
            await Task.Delay(10);

        _coordinator.FailureCount.Should().Be(1);
        _coordinator.SuccessCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ExecuteAsync_AfterRefreshCompletes_KeyCanBeReenqueued()
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

        // Wait for first refresh callback to complete
        var completed = await Task.WhenAny(firstRefreshDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(firstRefreshDone.Task, "the first refresh should have completed");

        // Wait for ProcessRefreshAsync.finally to release the key from _pendingKeys
        for (int i = 0; i < 100 && _coordinator.QueueDepth > 0; i++)
            await Task.Delay(10);

        // Now re-enqueue the same key — should succeed since first completed
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
    public void NotifyInvalidation_WithPendingRefresh_MarksKeyAsInvalidated()
    {
        // Invalidation is only tracked when a refresh is pending for the key
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        _coordinator.NotifyInvalidation("layer:1");

        _coordinator.WasInvalidated("layer:1").Should().BeTrue();
        _coordinator.WasInvalidated("layer:2").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void NotifyInvalidation_WithoutPendingRefresh_IsIgnored()
    {
        // No pending refresh → invalidation flag is not tracked (avoids stale flags)
        _coordinator.NotifyInvalidation("layer:1");

        _coordinator.WasInvalidated("layer:1").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_PendingKey_SucceedsAndBlocksInvalidation()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        // Claim should succeed for a pending (non-invalidated) key
        _coordinator.TryClaimWriteBack("layer:1").Should().BeTrue();

        // After claim, NotifyInvalidation should still mark the key (state 2 → 1)
        _coordinator.NotifyInvalidation("layer:1");
        _coordinator.WasInvalidated("layer:1").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_InvalidatedKey_ReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        // Invalidate first, then try to claim
        _coordinator.NotifyInvalidation("layer:1");
        _coordinator.TryClaimWriteBack("layer:1").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_NoKey_ReturnsFalse()
    {
        // No pending refresh → claim fails
        _coordinator.TryClaimWriteBack("layer:nonexistent").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void TryClaimWriteBack_DoubleClaimSameKey_SecondReturnsFalse()
    {
        _coordinator.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        _coordinator.TryClaimWriteBack("layer:1").Should().BeTrue();
        // Second claim fails — already in write-claimed state
        _coordinator.TryClaimWriteBack("layer:1").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ExecuteAsync_InvalidatedRefresh_CountsAsSkippedNotSuccess()
    {
        // Regression: invalidated refreshes must not inflate SuccessCount.
        var gate = new TaskCompletionSource<bool>();
        var refreshDone = new TaskCompletionSource<bool>();

        _coordinator.TryEnqueueRefresh("layer:1", async _ =>
        {
            await gate.Task;
            refreshDone.SetResult(true);
        });

        // Invalidate before the callback runs
        _coordinator.NotifyInvalidation("layer:1");

        _ = _coordinator.StartAsync(_cts.Token);
        gate.SetResult(true);

        var completed = await Task.WhenAny(refreshDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(refreshDone.Task);

        // Wait for post-callback bookkeeping
        for (int i = 0; i < 100 && _coordinator.SkippedCount < 1; i++)
            await Task.Delay(10);

        _coordinator.SkippedCount.Should().Be(1);
        _coordinator.SuccessCount.Should().Be(0, "invalidated refreshes must not be counted as successful");
        _coordinator.FailureCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ProcessRefreshAsync_CleansUpInvalidationTrackingAfterCompletion()
    {
        // Gate the refresh callback so it doesn't complete until after we call NotifyInvalidation.
        var gate = new TaskCompletionSource<bool>();
        var refreshDone = new TaskCompletionSource<bool>();

        // Enqueue BEFORE starting the service so the item is pending when we notify.
        _coordinator.TryEnqueueRefresh("layer:1", async _ =>
        {
            await gate.Task;
            refreshDone.SetResult(true);
        });

        _coordinator.NotifyInvalidation("layer:1");
        _coordinator.WasInvalidated("layer:1").Should().BeTrue();

        // Start the service and release the gate so the refresh callback can complete.
        _ = _coordinator.StartAsync(_cts.Token);
        gate.SetResult(true);

        var completed = await Task.WhenAny(refreshDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(refreshDone.Task);

        // Wait for finally block to clean up
        for (int i = 0; i < 100 && _coordinator.WasInvalidated("layer:1"); i++)
            await Task.Delay(10);

        _coordinator.WasInvalidated("layer:1").Should().BeFalse();
    }

}
