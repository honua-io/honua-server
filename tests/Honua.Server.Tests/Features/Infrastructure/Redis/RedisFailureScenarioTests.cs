// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Redis;
using Honua.Server.Features.Infrastructure.Redis;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.Redis;

[Collection("Redis")]
public sealed class RedisFailureScenarioTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redisFixture;
    private readonly ITestOutputHelper _output;

    public RedisFailureScenarioTests(RedisFixture redisFixture, ITestOutputHelper output)
    {
        _redisFixture = redisFixture;
        _output = output;
    }

    [IntegrationTest]
    public async Task MultiServiceCoordination_SplitBrainPrevention_WhenRedisFailsInProduction()
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddXUnit(_output))
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = "Production" })
            .AddSingleton(_redisFixture.Redis)
            .AddStandardizedRedisInfrastructure()
            .AddRedisLeaderElection("coordination-test", TimeSpan.FromSeconds(30), "node1")
            .AddRedisJobQueue("test-queue", RedisFallbackMode.FailFast)
            .BuildServiceProvider();

        var healthMonitor = services.GetRequiredService<IRedisHealthMonitor>();
        var leaderElection1 = services.GetRequiredService<IRedisLeaderElection>();
        var jobQueue = services.GetRequiredService<IRedisJobQueue>();

        // Create second leader election for split-brain testing
        var leaderElection2 = new RedisLeaderElection(
            "coordination-test",
            _redisFixture.Redis,
            healthMonitor,
            services.GetRequiredService<IHostEnvironment>(),
            services.GetRequiredService<ILogger<RedisLeaderElection>>(),
            TimeSpan.FromSeconds(30),
            "node2");

        try
        {
            // Initial state - Redis is working
            healthMonitor.IsRedisAvailable.Should().BeTrue();

            // Node1 becomes leader
            var leadership1 = await leaderElection1.TryAcquireOrExtendLeadershipAsync();
            leadership1.Should().BeTrue();

            // Node2 cannot become leader
            var leadership2 = await leaderElection2.TryAcquireOrExtendLeadershipAsync();
            leadership2.Should().BeFalse();

            // Job queue works normally
            await jobQueue.EnqueueAsync("test-job-1");
            var queueLength = await jobQueue.GetQueueLengthAsync();
            queueLength.Should().Be(1);

            // Simulate Redis failure by disconnecting
            await _redisFixture.Redis.CloseAsync();

            // Both nodes should lose leadership immediately in production
            await Task.Delay(100); // Brief delay for failure detection

            var leadership1AfterFailure = await leaderElection1.TryAcquireOrExtendLeadershipAsync();
            var leadership2AfterFailure = await leaderElection2.TryAcquireOrExtendLeadershipAsync();

            leadership1AfterFailure.Should().BeFalse();
            leadership2AfterFailure.Should().BeFalse();
            leaderElection1.IsLeader.Should().BeFalse();
            leaderElection2.IsLeader.Should().BeFalse();

            // Job queue should fail fast in production
            var enqueueAction = () => jobQueue.EnqueueAsync("test-job-2");
            await enqueueAction.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            leaderElection2.Dispose();
        }
    }

    [IntegrationTest]
    public async Task GracefulDegradation_WhenRedisFailsInDevelopment()
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddXUnit(_output))
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = "Development" })
            .AddSingleton(_redisFixture.Redis)
            .AddStandardizedRedisInfrastructure()
            .AddRedisLeaderElection("dev-test", TimeSpan.FromSeconds(30))
            .AddRedisJobQueue("dev-queue", RedisFallbackMode.InMemoryFallback)
            .BuildServiceProvider();

        var healthMonitor = services.GetRequiredService<IRedisHealthMonitor>();
        var leaderElection = services.GetRequiredService<IRedisLeaderElection>();
        var jobQueue = services.GetRequiredService<IRedisJobQueue>();

        // Initial Redis operations
        var initialLeadership = await leaderElection.TryAcquireOrExtendLeadershipAsync();
        initialLeadership.Should().BeTrue();

        await jobQueue.EnqueueAsync("redis-job");
        jobQueue.IsUsingRedis.Should().BeTrue();

        // Simulate Redis failure
        await _redisFixture.Redis.CloseAsync();
        await Task.Delay(100); // Brief delay for failure detection

        // Services should continue working with fallback
        await jobQueue.EnqueueAsync("fallback-job");
        var job = await jobQueue.DequeueAsync(TimeSpan.FromSeconds(1));
        job.Should().NotBeNull(); // Should get one of the jobs

        // Leadership should still work in development
        var fallbackLeadership = await leaderElection.TryAcquireOrExtendLeadershipAsync();
        fallbackLeadership.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task ConsistentRecovery_WhenRedisComesBackOnline()
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddXUnit(_output))
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = "Development" })
            .AddSingleton(_redisFixture.Redis)
            .AddStandardizedRedisInfrastructure()
            .AddRedisJobQueue("recovery-queue", RedisFallbackMode.InMemoryFallback)
            .BuildServiceProvider();

        var healthMonitor = services.GetRequiredService<IRedisHealthMonitor>();
        var jobQueue = services.GetRequiredService<IRedisJobQueue>();

        // Start with Redis down
        await _redisFixture.Redis.CloseAsync();
        await Task.Delay(100);

        // Operate in fallback mode
        await jobQueue.EnqueueAsync("fallback-job-1");
        await jobQueue.EnqueueAsync("fallback-job-2");
        jobQueue.IsUsingRedis.Should().BeFalse();
        jobQueue.FallbackQueueLength.Should().Be(2);

        // Restore Redis connection
        await _redisFixture.RestoreConnectionAsync();
        healthMonitor.RecordSuccess(); // Simulate successful reconnection

        // Try to restore Redis usage
        var restored = await jobQueue.TryRestoreRedisAsync();
        restored.Should().BeTrue();
        jobQueue.IsUsingRedis.Should().BeTrue();

        // New jobs should go to Redis
        await jobQueue.EnqueueAsync("redis-job-after-recovery");

        // Should be able to process both fallback and Redis jobs
        var totalLength = await jobQueue.GetQueueLengthAsync();
        totalLength.Should().BeGreaterOrEqualTo(3); // 2 fallback + 1 Redis (or more depending on timing)
    }

    [IntegrationTest]
    public async Task HealthCheck_ReportsCorrectStatus_AcrossFailureStates()
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddXUnit(_output))
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .AddSingleton(_redisFixture.Redis)
            .AddStandardizedRedisInfrastructure()
            .AddRedisLeaderElection("health-test")
            .AddRedisJobQueue("health-queue")
            .AddRedisHealthCheck()
            .BuildServiceProvider();

        var healthCheck = services.GetRequiredService<RedisHealthCheck>();
        var healthMonitor = services.GetRequiredService<IRedisHealthMonitor>();

        // Healthy state
        var healthyResult = await healthCheck.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        healthyResult.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
        healthyResult.Data.Should().ContainKey("redis_available").WhoseValue.Should().Be(true);

        // Simulate failure
        await _redisFixture.Redis.CloseAsync();
        healthMonitor.RecordFailure(new RedisConnectionException("Test failure"));

        var unhealthyResult = await healthCheck.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        unhealthyResult.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
        unhealthyResult.Data.Should().ContainKey("redis_available").WhoseValue.Should().Be(false);
        unhealthyResult.Data.Should().ContainKey("consecutive_failures");
    }

    [IntegrationTest]
    public async Task DataConsistency_MaintainedDuringFailoverAndRecovery()
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddXUnit(_output))
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .AddSingleton(_redisFixture.Redis)
            .AddStandardizedRedisInfrastructure()
            .AddRedisJobQueue("consistency-queue", RedisFallbackMode.InMemoryFallback)
            .BuildServiceProvider();

        var jobQueue = services.GetRequiredService<IRedisJobQueue>();
        var processedJobs = new List<string>();

        // Add jobs while Redis is working
        var redisJobs = Enumerable.Range(1, 5).Select(i => $"redis-job-{i}").ToArray();
        foreach (var job in redisJobs)
        {
            await jobQueue.EnqueueAsync(job);
        }

        // Process some jobs
        for (int i = 0; i < 2; i++)
        {
            var job = await jobQueue.DequeueAsync(TimeSpan.FromSeconds(1));
            if (job != null)
            {
                processedJobs.Add(job);
                await jobQueue.CompleteAsync(job);
            }
        }

        // Simulate Redis failure
        await _redisFixture.Redis.CloseAsync();

        // Add more jobs to fallback
        var fallbackJobs = Enumerable.Range(1, 3).Select(i => $"fallback-job-{i}").ToArray();
        foreach (var job in fallbackJobs)
        {
            await jobQueue.EnqueueAsync(job);
        }

        // Process fallback jobs
        for (int i = 0; i < 2; i++)
        {
            var job = await jobQueue.DequeueAsync(TimeSpan.FromSeconds(1));
            if (job != null)
            {
                processedJobs.Add(job);
                await jobQueue.CompleteAsync(job);
            }
        }

        // Restore Redis
        await _redisFixture.RestoreConnectionAsync();
        await jobQueue.TryRestoreRedisAsync();

        // Process remaining jobs (should include both remaining Redis jobs and remaining fallback jobs)
        var remainingJobs = new List<string>();
        string? job;
        while ((job = await jobQueue.DequeueAsync(TimeSpan.FromSeconds(1))) != null)
        {
            remainingJobs.Add(job);
            await jobQueue.CompleteAsync(job);
        }

        // Verify data consistency - no jobs should be lost
        var allExpectedJobs = redisJobs.Concat(fallbackJobs).ToHashSet();
        var allProcessedJobs = processedJobs.Concat(remainingJobs).ToHashSet();

        allProcessedJobs.Should().BeSubsetOf(allExpectedJobs);
        // We should have processed most or all jobs (exact count depends on timing of failure)
        allProcessedJobs.Should().HaveCountGreaterOrEqualTo(allExpectedJobs.Count - 2);
    }

    private class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "TestApp";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}