// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Caching;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.Health)]
public sealed class RedisReadinessOutageTests
{
    [IntegrationTheory]
    [InlineData("get")]
    [InlineData("@write")]
    [InlineData("sadd")]
    [InlineData("srem")]
    [InlineData("del")]
    [Operation(Operations.HealthCheck)]
    public async Task IsCacheHealthyAsync_RedisAllowsPingButDeniesCacheCommands_ReportsUnhealthyUntilRecovery(string deniedCommand)
    {
        await using var container = new RedisBuilder("redis:7.2-alpine").Build();
        await container.StartAsync();
        using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        using var cache = new RedisCacheService(Substitute.For<IDistributedCache>(),
            Options.Create(new CacheOptions { EnableFallback = true }),
            NullLogger<RedisCacheService>.Instance, Substitute.For<IPerformanceMonitor>(), redis);
        Assert.True(await cache.IsCacheHealthyAsync());

        // Expiring writes may use SETEX/PSETEX rather than SET, depending on
        // client optimization. Deny the whole write category for this case.
        var denied = await container.ExecAsync(["redis-cli", "ACL", "SETUSER", "default", "-" + deniedCommand]);
        Assert.Equal("OK", denied.Stdout.Trim());
        _ = await redis.GetDatabase().PingAsync();
        Assert.False(await cache.IsCacheHealthyAsync());

        var restored = await container.ExecAsync(["redis-cli", "ACL", "SETUSER", "default", "+" + deniedCommand]);
        Assert.Equal("OK", restored.Stdout.Trim());
        Assert.True(await cache.IsCacheHealthyAsync());
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_RedisStopsAndRestarts_RequiresLiveProbeEvenDuringFallback(bool enterFallback)
    {
        // Docker can remap ephemeral host ports on restart. Keep the dependency's
        // endpoint stable so this exercises recovery of the existing connection.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redisPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var container = new RedisBuilder("redis:7.2-alpine")
            .WithPortBinding(redisPort, 6379)
            .Build();
        await container.StartAsync();
        var configuration = ConfigurationOptions.Parse(container.GetConnectionString());
        configuration.AbortOnConnectFail = false;
        configuration.AsyncTimeout = 500;
        configuration.ConnectTimeout = 500;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        configuration.ReconnectRetryPolicy = new ExponentialRetry(100);
        using var redis = await ConnectionMultiplexer.ConnectAsync(configuration);
        var distributedCache = Substitute.For<IDistributedCache>();
        using var cache = new RedisCacheService(distributedCache,
            Options.Create(new CacheOptions { EnableFallback = true, RetryIntervalSeconds = 300 }),
            NullLogger<RedisCacheService>.Instance, Substitute.For<IPerformanceMonitor>(), redis);
        var database = Substitute.For<IDatabaseHealthChecker>();
        database.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var events = Substitute.For<IFeatureChangeEventStoreHealth>();
        events.CanPersistEvents.Returns(true); // No event operation has observed the idle outage.
        var migration = new MigrationState();
        migration.MarkSucceeded();
        var readiness = new ReadinessCheckService(database, migration,
            NullLogger<ReadinessCheckService>.Instance, cache, events);
        Assert.True((await readiness.CheckReadinessAsync()).IsReady);

        await container.StopAsync();
        if (enterFallback)
        {
            await cache.GetAsync<MetadataV2Field>("layer:outage");
            Assert.True(cache.IsUsingFallback);
        }

        var unavailable = await readiness.CheckReadinessAsync();
        Assert.False(unavailable.IsReady);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.True(events.CanPersistEvents);

        await container.StartAsync();
        Assert.Equal(redisPort, container.GetMappedPublicPort(6379));
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await cache.IsCacheHealthyAsync(recoveryTimeout.Token))
        {
            await Task.Delay(100, recoveryTimeout.Token);
        }
        Assert.True((await readiness.CheckReadinessAsync()).IsReady);
    }
}
