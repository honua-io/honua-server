// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Redis integration tests for RedisCacheService.
/// </summary>
[Collection("Redis")]
[Protocol(Protocols.Infrastructure)]
public sealed class RedisCacheServiceRedisIntegrationTests
{
    private readonly RedisFixture _redis;

    public RedisCacheServiceRedisIntegrationTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [IntegrationTest]
    [Operation(Operations.Cache, Operations.TestInfrastructure)]
    public async Task SetAsync_WhenRedisConfigured_SharesAcrossInstances()
    {
        var prefix = $"test:{Guid.NewGuid():N}:";

        using var cacheA = CreateCacheScope(prefix);
        using var cacheB = CreateCacheScope(prefix);

        await cacheA.Cache.SetAsync("layer:1", new FieldDefinition("objectid", FieldType.Integer, Nullable: false));

        var result = await cacheB.Cache.GetAsync<FieldDefinition>("layer:1");

        result.Should().NotBeNull();
        result!.Name.Should().Be("objectid");
    }

    [IntegrationTest]
    [Operation(Operations.Cache, Operations.TestInfrastructure)]
    public async Task RemoveAsync_RemovesKeyAcrossInstances()
    {
        var prefix = $"test:{Guid.NewGuid():N}:";

        using var cacheA = CreateCacheScope(prefix);
        using var cacheB = CreateCacheScope(prefix);

        var layer = LayerDefinition.CreateBasic(1, "Layer", GeometryType.Point);
        var service = new ServiceDefinition("test", "Test Service", [layer], SpatialReference.WGS84);
        await cacheA.Cache.SetAsync("service:1", service);

        await cacheB.Cache.RemoveAsync("service:1");
        var result = await cacheA.Cache.GetAsync<ServiceDefinition>("service:1");

        result.Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Cache, Operations.TestInfrastructure)]
    public async Task RemoveByPatternAsync_WhenRedisConfigured_RemovesMatchingKeysAcrossInstances()
    {
        var prefix = $"test:{Guid.NewGuid():N}:";

        using var cacheA = CreateCacheScope(prefix);
        using var cacheB = CreateCacheScope(prefix);

        await cacheA.Cache.SetAsync("layer:1", new FieldDefinition("Layer1", FieldType.String, Length: 10));
        await cacheA.Cache.SetAsync("layer:2", new FieldDefinition("Layer2", FieldType.String, Length: 10));
        await cacheA.Cache.SetAsync("service:1", new FieldDefinition("Service1", FieldType.String, Length: 10));

        await cacheB.Cache.RemoveByPatternAsync("layer:*");

        (await cacheA.Cache.GetAsync<FieldDefinition>("layer:1")).Should().BeNull();
        (await cacheA.Cache.GetAsync<FieldDefinition>("layer:2")).Should().BeNull();
        (await cacheA.Cache.GetAsync<FieldDefinition>("service:1")).Should().NotBeNull();
    }

    private CacheScope CreateCacheScope(string keyPrefix)
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = _redis.ConnectionString;
            options.InstanceName = string.Empty;
        });

        var provider = services.BuildServiceProvider();
        var distributedCache = provider.GetRequiredService<IDistributedCache>();
        var options = Options.Create(new CacheOptions
        {
            Enabled = true,
            EnableFallback = true,
            DefaultTtlSeconds = 60,
            ServiceTtlSeconds = 60,
            LayerTtlSeconds = 60,
            NegativeTtlSeconds = 10,
            KeyPrefix = keyPrefix
        });

        var logger = NullLogger<RedisCacheService>.Instance;
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var cache = new RedisCacheService(distributedCache, options, logger, performanceMonitor, multiplexer);
        return new CacheScope(cache, provider, multiplexer);
    }

    private sealed class CacheScope : IDisposable
    {
        public CacheScope(RedisCacheService cache, ServiceProvider provider, IConnectionMultiplexer multiplexer)
        {
            Cache = cache;
            _provider = provider;
            _multiplexer = multiplexer;
        }

        public RedisCacheService Cache { get; }
        private readonly ServiceProvider _provider;
        private readonly IConnectionMultiplexer _multiplexer;

        public void Dispose()
        {
            Cache.Dispose();
            _provider.Dispose();
            _multiplexer.Dispose();
        }
    }
}
