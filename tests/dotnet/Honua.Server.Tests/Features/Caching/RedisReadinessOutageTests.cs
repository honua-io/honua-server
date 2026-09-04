// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_RedisStopsAndRestarts_RequiresLiveProbeEvenDuringFallback(bool enterFallback)
    {
        await using var container = new RedisBuilder("redis:7.2-alpine").Build();
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
            distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<byte[]?>(new IOException("Redis unavailable")));
            await cache.GetAsync<MetadataV2Field>("layer:outage");
            Assert.True(cache.IsUsingFallback);
        }

        var unavailable = await readiness.CheckReadinessAsync();
        Assert.False(unavailable.IsReady);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.True(events.CanPersistEvents);

        await container.StartAsync();
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await cache.IsCacheHealthyAsync(recoveryTimeout.Token))
        {
            await Task.Delay(100, recoveryTimeout.Token);
        }
        Assert.True((await readiness.CheckReadinessAsync()).IsReady);
    }
}
