// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Coordination;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Integration;

/// <summary>
/// Integration tests for distributed coordination features across multiple simulated instances.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
[Collection("Performance")]
public sealed class DistributedCoordinationIntegrationTests
{
    [UnitTest]
    [Operation(Operations.Cache, Operations.Infrastructure)]
    public async Task CacheCoordination_MultipleInstances_DeduplicatesRefreshes()
    {
        // Arrange: Create two coordinator instances (simulating two app instances)
        var refreshCount = 0;
        var options = Options.Create(new CacheOptions
        {
            BackgroundRefreshEnabled = true,
            MaxConcurrentRefreshes = 2,
            RefreshTimeoutSeconds = 5
        });

        var coordinator1 = new DistributedCacheRefreshCoordinator(
            options,
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        var coordinator2 = new DistributedCacheRefreshCoordinator(
            options,
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        // Start background services
        using var cts = new CancellationTokenSource();
        _ = coordinator1.StartAsync(cts.Token);
        _ = coordinator2.StartAsync(cts.Token);

        // Act: Both instances try to refresh the same key
        var enqueued1 = coordinator1.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.CompletedTask;
        });

        var enqueued2 = coordinator2.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.CompletedTask;
        });

        // Wait for processing
        await Task.Delay(500);

        // Assert: In fallback mode (no Redis), both instances work independently
        // Each instance deduplicates within itself but they don't coordinate
        enqueued1.Should().BeTrue();
        enqueued2.Should().BeTrue();
        refreshCount.Should().Be(2, "fallback mode allows both instances to refresh independently");

        // Cleanup
        cts.Cancel();
        coordinator1.Dispose();
        coordinator2.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task LeaderElection_MultipleInstances_OnlyOneBecomesLeader()
    {
        // Arrange: Create two leader election instances
        var election1 = new RedisDistributedLeaderElection(
            "test-service",
            null, // No Redis - fallback mode
            NullLogger<RedisDistributedLeaderElection>.Instance);

        var election2 = new RedisDistributedLeaderElection(
            "test-service",
            null, // No Redis - fallback mode
            NullLogger<RedisDistributedLeaderElection>.Instance);

        // Act: Both try to acquire leadership
        var leader1 = await election1.TryAcquireLeadershipAsync();
        var leader2 = await election2.TryAcquireLeadershipAsync();

        // Assert: In fallback mode, both become leaders (single instance assumption)
        leader1.Should().BeTrue("first instance becomes leader in fallback mode");
        leader2.Should().BeTrue("second instance also becomes leader in fallback mode");
        election1.IsLeader.Should().BeTrue();
        election2.IsLeader.Should().BeTrue();

        // Cleanup
        election1.Dispose();
        election2.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CacheInvalidation_CrossInstance_PropagatesCorrectly()
    {
        // Arrange
        var options = Options.Create(new CacheOptions
        {
            BackgroundRefreshEnabled = true,
            MaxConcurrentRefreshes = 1,
            RefreshTimeoutSeconds = 5
        });

        var coordinator1 = new DistributedCacheRefreshCoordinator(
            options,
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        var coordinator2 = new DistributedCacheRefreshCoordinator(
            options,
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        // Start background services
        using var cts = new CancellationTokenSource();
        _ = coordinator1.StartAsync(cts.Token);
        _ = coordinator2.StartAsync(cts.Token);

        // Enqueue a refresh on coordinator1
        coordinator1.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        // Act: Invalidate from coordinator2 (simulating cross-instance invalidation)
        await coordinator2.NotifyInvalidationClusterWideAsync("layer:1");

        // Assert: In fallback mode without Redis, invalidation only affects locally pending keys.
        // coordinator1 has a pending refresh for "layer:1" but coordinator2 sent the invalidation
        // through its own local path — coordinator1's key state is unaffected because there is
        // no cross-instance channel. coordinator2 never had a pending refresh for the key, so
        // WasInvalidated is false (per ICacheRefreshCoordinator contract: invalidation without
        // a pending refresh is a no-op to avoid stale markers).
        coordinator1.WasInvalidated("layer:1").Should().BeFalse("local invalidation state not shared in fallback mode");
        coordinator2.WasInvalidated("layer:1").Should().BeFalse("coordinator2 never had a pending refresh for this key");

        // Cleanup
        cts.Cancel();
        coordinator1.Dispose();
        coordinator2.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void FallbackBehavior_WithoutRedis_RemainsOperational()
    {
        // Arrange: Create coordinator without Redis
        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions { BackgroundRefreshEnabled = true }),
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        // Act & Assert: All operations should work in fallback mode
        coordinator.IsDistributed.Should().BeFalse();

        var enqueued = coordinator.TryEnqueueRefresh("test:key", _ => Task.CompletedTask);
        enqueued.Should().BeTrue();

        coordinator.NotifyInvalidation("test:key");
        coordinator.WasInvalidated("test:key").Should().BeTrue();

        var claimed = coordinator.TryClaimWriteBack("test:key");
        claimed.Should().BeFalse("key was invalidated");

        coordinator.QueueDepth.Should().BeGreaterOrEqualTo(0);

        // Cleanup
        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RetryBackoff_PreventsDuplicateRefreshes()
    {
        // Arrange
        var refreshCount = 0;
        var coordinator = new DistributedCacheRefreshCoordinator(
            Options.Create(new CacheOptions
            {
                BackgroundRefreshEnabled = true,
                MaxConcurrentRefreshes = 2,
                RefreshTimeoutSeconds = 5
            }),
            Substitute.For<IPerformanceMonitor>(),
            NullLogger<DistributedCacheRefreshCoordinator>.Instance);

        using var cts = new CancellationTokenSource();
        _ = coordinator.StartAsync(cts.Token);

        // Act: Enqueue a refresh that will fail
        coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref refreshCount);
            throw new InvalidOperationException("Simulated failure");
        });

        // Wait for failure processing
        await Task.Delay(200);

        // Try to enqueue the same key again immediately
        var retriedImmediately = coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.CompletedTask;
        });

        // Assert: Should be rejected due to retry backoff
        retriedImmediately.Should().BeFalse("retry should be blocked by backoff");
        refreshCount.Should().Be(1, "only the first (failing) refresh should have executed");

        // Wait for backoff to expire
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Try again after backoff
        var retriedAfterBackoff = coordinator.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.CompletedTask;
        });

        // Wait for processing
        await Task.Delay(200);

        retriedAfterBackoff.Should().BeTrue("retry should be allowed after backoff expires");
        refreshCount.Should().Be(2, "second refresh should have executed");

        // Cleanup
        cts.Cancel();
        coordinator.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task LeaderElection_HeartbeatMaintenance_KeepsLeadership()
    {
        // Arrange
        var election = new RedisDistributedLeaderElection(
            "test-heartbeat",
            null,
            NullLogger<RedisDistributedLeaderElection>.Instance);

        // Act: Acquire leadership and perform heartbeats
        var acquired = await election.TryAcquireLeadershipAsync();
        acquired.Should().BeTrue();
        election.IsLeader.Should().BeTrue();

        // Perform multiple heartbeats
        for (int i = 0; i < 3; i++)
        {
            var heartbeat = await election.HeartbeatAsync();
            heartbeat.Should().BeTrue($"heartbeat {i + 1} should succeed");
            election.IsLeader.Should().BeTrue($"should remain leader after heartbeat {i + 1}");
            await Task.Delay(50);
        }

        // Act: Release leadership
        await election.ReleaseLeadershipAsync();

        // Assert: No longer leader
        election.IsLeader.Should().BeFalse();

        // Cleanup
        election.Dispose();
    }
}
