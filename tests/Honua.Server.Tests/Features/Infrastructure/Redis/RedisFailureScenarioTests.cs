// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Redis;
using Honua.Server.Features.Infrastructure.Redis;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Redis;

[Collection("Redis")]
public sealed class RedisFailureScenarioTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redisFixture;

    public RedisFailureScenarioTests(RedisFixture redisFixture)
    {
        _redisFixture = redisFixture;
    }

    [IntegrationTest]
    public async Task JobQueue_WithLiveRedis_DequeuesAndCompletesJobs()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(_redisFixture.ConnectionString);
        var environment = new TestHostEnvironment();
        var healthMonitor = new RedisHealthMonitor(multiplexer, NullLogger<RedisHealthMonitor>.Instance);
        var queue = new RedisJobQueue(
            $"redis-failure-test:{Guid.NewGuid():N}",
            multiplexer,
            healthMonitor,
            RedisFallbackStrategy.InMemoryFallback,
            environment,
            NullLogger<RedisJobQueue>.Instance);

        await queue.EnqueueAsync("job-1");
        (await queue.GetQueueLengthAsync()).Should().Be(1);

        var dequeued = await queue.DequeueAsync(TimeSpan.FromSeconds(2));

        dequeued.Should().Be("job-1");
        (await queue.GetProcessingCountAsync()).Should().Be(1);

        await queue.CompleteAsync(dequeued!);
        (await queue.GetProcessingCountAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task HealthCheck_WithConfiguredRedis_ReportsHealthy()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(_redisFixture.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        services.AddStandardizedRedisInfrastructure();
        services.AddRedisLeaderElection($"leader:{Guid.NewGuid():N}");
        services.AddRedisJobQueue($"queue:{Guid.NewGuid():N}", RedisFallbackMode.InMemoryFallback);

        using var provider = services.BuildServiceProvider();
        var healthCheck = new RedisHealthCheck(
            provider.GetRequiredService<IRedisHealthMonitor>(),
            provider,
            NullLogger<RedisHealthCheck>.Instance);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("redis_available").WhoseValue.Should().Be(true);
        result.Data.Should().ContainKey("connectivity_test").WhoseValue.Should().Be(true);
    }

    [IntegrationTest]
    public async Task WithoutRedis_InDevelopment_UsesLocalLeadershipAndInMemoryQueue()
    {
        var environment = new TestHostEnvironment { EnvironmentName = "Development" };
        var healthMonitor = new RedisHealthMonitor(null, NullLogger<RedisHealthMonitor>.Instance);
        using var leaderElection = new RedisLeaderElection(
            "dev-fallback-leader",
            null,
            healthMonitor,
            environment,
            NullLogger<RedisLeaderElection>.Instance);
        var queue = new RedisJobQueue(
            "dev-fallback-queue",
            null,
            healthMonitor,
            RedisFallbackStrategy.InMemoryFallback,
            environment,
            NullLogger<RedisJobQueue>.Instance);

        (await leaderElection.TryAcquireOrExtendLeadershipAsync()).Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();

        await queue.EnqueueAsync("fallback-job");
        queue.IsUsingRedis.Should().BeFalse();
        (await queue.GetQueueLengthAsync()).Should().Be(1);
        (await queue.DequeueAsync(TimeSpan.FromMilliseconds(200))).Should().Be("fallback-job");
    }

    [IntegrationTest]
    public async Task WithoutRedis_WithFailFastQueue_ThrowsUnavailableException()
    {
        var environment = new TestHostEnvironment { EnvironmentName = "Production" };
        var healthMonitor = new RedisHealthMonitor(null, NullLogger<RedisHealthMonitor>.Instance);
        var queue = new RedisJobQueue(
            "prod-failfast-queue",
            null,
            healthMonitor,
            RedisFallbackStrategy.FailFast,
            environment,
            NullLogger<RedisJobQueue>.Instance);

        var act = () => queue.EnqueueAsync("job");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Honua.Server.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
