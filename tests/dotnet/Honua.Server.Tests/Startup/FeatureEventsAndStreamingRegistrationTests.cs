// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Startup;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Regression tests for #1618: readiness on single-node deployments without Redis.
/// The host-level registration must only enforce durable Redis-backed feature-change
/// event storage when Redis is actually configured — unconfigured is not unhealthy,
/// unreachable is.
/// </summary>
[Protocol(TestProtocols.Health)]
public sealed class FeatureEventsAndStreamingRegistrationTests
{
    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_DurableEnvironmentWithoutRedisConfigured_ReturnsReady()
    {
        // Production-equivalent host (requiresDurableDistributedEvents = true) with no
        // Redis configured: supported single-node/serverless topology must report Ready.
        await using var provider = BuildServices(requiresDurableDistributedEvents: true, redis: null);
        var health = provider.GetRequiredService<IFeatureChangeEventStoreHealth>();

        health.CanPersistEvents.Should().BeTrue();
        health.IsUsingInMemoryFallback.Should().BeTrue();

        var result = await CreateReadinessService(health).CheckReadinessAsync();

        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_DurableEnvironmentWithRedisUnreachable_ReturnsNotReady()
    {
        // Redis IS configured but the durable write path fails: that is a real fault and
        // must still fail readiness — the single-node fix must not mask Redis outages.
        await using var provider = BuildServices(
            requiresDurableDistributedEvents: true,
            redis: CreateRedis(CreateFailingDatabase()));
        var store = provider.GetRequiredService<IFeatureChangeEventStore>();
        var health = provider.GetRequiredService<IFeatureChangeEventStoreHealth>();

        var act = () => store.AppendAsync(CreateRequest());
        await act.Should().ThrowAsync<InvalidOperationException>();

        health.CanPersistEvents.Should().BeFalse();

        var result = await CreateReadinessService(health).CheckReadinessAsync();

        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Feature-change event storage unavailable");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_DurableEnvironmentWithHealthyRedis_ReturnsReady()
    {
        // Existing durable behavior is intact: configured and reachable Redis stays Ready
        // and is not reported as fallback.
        await using var provider = BuildServices(
            requiresDurableDistributedEvents: true,
            redis: CreateRedis(CreateHealthyDatabase()));
        var store = provider.GetRequiredService<IFeatureChangeEventStore>();
        var health = provider.GetRequiredService<IFeatureChangeEventStoreHealth>();

        var created = await store.AppendAsync(CreateRequest());
        created.Should().NotBeNull();

        health.CanPersistEvents.Should().BeTrue();
        health.IsUsingInMemoryFallback.Should().BeFalse();

        var result = await CreateReadinessService(health).CheckReadinessAsync();

        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_RelaxedEnvironmentWithoutRedis_ReturnsReady()
    {
        // Development/Test hosts keep their existing in-memory behavior.
        await using var provider = BuildServices(requiresDurableDistributedEvents: false, redis: null);
        var health = provider.GetRequiredService<IFeatureChangeEventStoreHealth>();

        health.CanPersistEvents.Should().BeTrue();

        var result = await CreateReadinessService(health).CheckReadinessAsync();

        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
    }

    private static ServiceProvider BuildServices(bool requiresDurableDistributedEvents, IConnectionMultiplexer? redis)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (redis is not null)
        {
            services.AddSingleton(redis);
        }

        var configuration = new ConfigurationBuilder().Build();
        services.AddHonuaFeatureEventsAndStreaming(configuration, requiresDurableDistributedEvents);
        return services.BuildServiceProvider();
    }

    private static ReadinessCheckService CreateReadinessService(IFeatureChangeEventStoreHealth health)
    {
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();
        return new ReadinessCheckService(
            new MockHealthyDatabaseChecker(),
            migrationState,
            new MockLogger<ReadinessCheckService>(),
            cacheHealthChecker: null,
            featureChangeEventStoreHealth: health);
    }

    private static IConnectionMultiplexer CreateRedis(IDatabase database)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        return redis;
    }

    private static IDatabase CreateFailingDatabase()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns<Task<RedisResult>>(_ =>
                Task.FromException<RedisResult>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated outage")));
        return database;
    }

    private static IDatabase CreateHealthyDatabase()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create(new[] { RedisResult.Create((RedisValue)"1"), RedisResult.Create((RedisValue)"1") })));
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);
        database.SortedSetLengthAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<Exclude>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(0L));
        return database;
    }

    private static FeatureChangeEventRequest CreateRequest() => new()
    {
        ServiceId = "svc-1",
        LayerId = 0,
        ObjectId = 1,
        Operation = "update",
        Protocol = "rest",
        RequestId = "req-1"
    };
}
