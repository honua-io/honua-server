// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Redis;

[Collection("Unit")]
public sealed class RedisFallbackIntegrationTests
{
    private readonly Mock<IHostEnvironment> _mockEnvironment = new();

    [UnitTest]
    public async Task MultipleServices_ConsistentBehaviorInDevelopment()
    {
        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Development");

        var healthMonitor = new RedisHealthMonitor(null, NullLogger<RedisHealthMonitor>.Instance);

        // Create multiple Redis-dependent services
        var leaderElection = new RedisLeaderElection(
            "test-leader",
            null,
            healthMonitor,
            _mockEnvironment.Object,
            NullLogger<RedisLeaderElection>.Instance);

        var jobQueue = new RedisJobQueue(
            "test-queue",
            null,
            healthMonitor,
            RedisFallbackStrategy.AllowLocalInDev,
            _mockEnvironment.Object,
            NullLogger<RedisJobQueue>.Instance);

        // All services should allow fallback in development
        var leadershipResult = await leaderElection.TryAcquireOrExtendLeadershipAsync();
        leadershipResult.Should().BeTrue();

        await jobQueue.EnqueueAsync("test-job");
        var queueLength = await jobQueue.GetQueueLengthAsync();
        queueLength.Should().Be(1);

        var job = await jobQueue.DequeueAsync(TimeSpan.FromSeconds(1));
        job.Should().Be("test-job");

        // Cleanup
        leaderElection.Dispose();
    }

    [UnitTest]
    public async Task MultipleServices_ConsistentBehaviorInProduction()
    {
        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Production");

        var healthMonitor = new RedisHealthMonitor(null, NullLogger<RedisHealthMonitor>.Instance);

        // Create multiple Redis-dependent services with fail-fast strategy
        var leaderElection = new RedisLeaderElection(
            "test-leader",
            null,
            healthMonitor,
            _mockEnvironment.Object,
            NullLogger<RedisLeaderElection>.Instance);

        var jobQueue = new RedisJobQueue(
            "test-queue",
            null,
            healthMonitor,
            RedisFallbackStrategy.FailFast,
            _mockEnvironment.Object,
            NullLogger<RedisJobQueue>.Instance);

        // Leadership should fail in production without Redis
        var leadershipResult = await leaderElection.TryAcquireOrExtendLeadershipAsync();
        leadershipResult.Should().BeFalse();

        // Job queue should throw in production without Redis
        var enqueueAction = () => jobQueue.EnqueueAsync("test-job");
        await enqueueAction.Should().ThrowAsync<InvalidOperationException>();

        // Cleanup
        leaderElection.Dispose();
    }

    [UnitTest]
    public async Task RedisFailure_SplitBrainPrevention()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var mockDatabase = new Mock<IDatabase>();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Production");

        var healthMonitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        // Create two leader election instances
        var election1 = new RedisLeaderElection(
            "shared-key",
            mockRedis.Object,
            healthMonitor,
            _mockEnvironment.Object,
            NullLogger<RedisLeaderElection>.Instance,
            nodeId: "node1");

        var election2 = new RedisLeaderElection(
            "shared-key",
            mockRedis.Object,
            healthMonitor,
            _mockEnvironment.Object,
            NullLogger<RedisLeaderElection>.Instance,
            nodeId: "node2");

        // Initially, Redis works and one node becomes leader
        mockDatabase.Setup(db => db.LockTakeAsync("shared-key", "node1", It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);
        mockDatabase.Setup(db => db.LockTakeAsync("shared-key", "node2", It.IsAny<TimeSpan>()))
            .ReturnsAsync(false);

        var result1 = await election1.TryAcquireOrExtendLeadershipAsync();
        var result2 = await election2.TryAcquireOrExtendLeadershipAsync();

        result1.Should().BeTrue();
        result2.Should().BeFalse();

        // Simulate Redis failure - both should lose leadership
        healthMonitor.RecordFailure(new RedisConnectionException("Connection lost"));

        // In production, both should recognize they can't maintain leadership without Redis
        var result1AfterFailure = await election1.TryAcquireOrExtendLeadershipAsync();
        var result2AfterFailure = await election2.TryAcquireOrExtendLeadershipAsync();

        result1AfterFailure.Should().BeFalse();
        result2AfterFailure.Should().BeFalse();

        // Neither should claim to be leader
        election1.IsLeader.Should().BeFalse();
        election2.IsLeader.Should().BeFalse();

        // Cleanup
        election1.Dispose();
        election2.Dispose();
    }

    [UnitTest]
    public async Task RedisRecovery_ConsistentRestoration()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var mockDatabase = new Mock<IDatabase>();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Development");

        var healthMonitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        var jobQueue = new RedisJobQueue(
            "test-queue",
            mockRedis.Object,
            healthMonitor,
            RedisFallbackStrategy.InMemoryFallback,
            _mockEnvironment.Object,
            NullLogger<RedisJobQueue>.Instance);

        var leaderElection = new RedisLeaderElection(
            "test-leader",
            mockRedis.Object,
            healthMonitor,
            _mockEnvironment.Object,
            NullLogger<RedisLeaderElection>.Instance);

        // Simulate initial Redis failure
        healthMonitor.RecordFailure(new RedisConnectionException("Initial failure"));

        // Both services should use fallback
        await jobQueue.EnqueueAsync("fallback-job");
        var leadership = await leaderElection.TryAcquireOrExtendLeadershipAsync();

        jobQueue.IsUsingRedis.Should().BeFalse();
        jobQueue.FallbackQueueLength.Should().Be(1);
        leadership.Should().BeTrue(); // Allowed in dev

        // Simulate Redis recovery
        mockDatabase.Setup(db => db.PingAsync()).ReturnsAsync(TimeSpan.FromMilliseconds(10));
        mockDatabase.Setup(db => db.ListLeftPushAsync("test-queue", "redis-job")).ReturnsAsync(1);

        await jobQueue.TryRestoreRedisAsync();
        await leaderElection.TryRestoreRedisAsync();

        // Both should now use Redis again
        jobQueue.IsUsingRedis.Should().BeTrue();
        leaderElection.IsUsingRedis.Should().BeTrue();

        // Verify Redis operations work
        await jobQueue.EnqueueAsync("redis-job");
        var totalLength = await jobQueue.GetQueueLengthAsync();
        totalLength.Should().BeGreaterThan(1); // Includes both fallback and Redis jobs

        // Cleanup
        leaderElection.Dispose();
    }

    [UnitTest]
    public void FallbackStrategies_ProvideDifferentBehaviors()
    {
        var healthMonitor = new RedisHealthMonitor(null, NullLogger<RedisHealthMonitor>.Instance);

        var devEnvironment = new Mock<IHostEnvironment>();
        devEnvironment.Setup(e => e.EnvironmentName).Returns("Development");

        var prodEnvironment = new Mock<IHostEnvironment>();
        prodEnvironment.Setup(e => e.EnvironmentName).Returns("Production");

        // Test different strategies
        var failFastStrategy = RedisFallbackStrategy.FailFast;
        var inMemoryStrategy = RedisFallbackStrategy.InMemoryFallback;
        var localDevStrategy = RedisFallbackStrategy.AllowLocalInDev;

        // Fail-fast never allows fallback
        failFastStrategy.ShouldAllowFallback(healthMonitor, devEnvironment.Object).Should().BeFalse();
        failFastStrategy.ShouldAllowFallback(healthMonitor, prodEnvironment.Object).Should().BeFalse();

        // In-memory always allows fallback
        inMemoryStrategy.ShouldAllowFallback(healthMonitor, devEnvironment.Object).Should().BeTrue();
        inMemoryStrategy.ShouldAllowFallback(healthMonitor, prodEnvironment.Object).Should().BeTrue();

        // Local dev allows fallback only in dev/test
        localDevStrategy.ShouldAllowFallback(healthMonitor, devEnvironment.Object).Should().BeTrue();
        localDevStrategy.ShouldAllowFallback(healthMonitor, prodEnvironment.Object).Should().BeFalse();
    }
}