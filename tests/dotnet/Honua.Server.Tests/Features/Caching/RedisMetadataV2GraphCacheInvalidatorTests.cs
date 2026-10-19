// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>Full-server registration coverage for metadata cache invalidation.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisMetadataV2GraphCacheInvalidatorTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public void Composition_WithRedis_RegistersTheBroadcastInvalidatorAndSubscriber()
    {
        var services = NewHostServices();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetSubscriber(Arg.Any<object?>()).Returns(Substitute.For<ISubscriber>());
        services.AddSingleton(redis);

        InfrastructureCompositionRoot.ConfigureMetadataGraphCacheInvalidation(services);

        using var provider = services.BuildServiceProvider();
        var invalidator = provider.GetRequiredService<IMetadataV2GraphCacheInvalidator>();
        invalidator.Should().BeOfType<RedisMetadataV2GraphCacheInvalidator>();
        provider.GetServices<IHostedService>().Should().Contain((IHostedService)invalidator);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Composition_WithoutRedis_KeepsTheInProcessInvalidatorAndNoSubscriber()
    {
        var services = NewHostServices();

        InfrastructureCompositionRoot.ConfigureMetadataGraphCacheInvalidation(services);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMetadataV2GraphCacheInvalidator>()
            .Should().BeOfType<MetadataV2GraphSnapshotCache>();
        provider.GetService<RedisMetadataV2GraphCacheInvalidator>().Should().BeNull();
        provider.GetServices<IHostedService>().Should().BeEmpty();
    }

    private static ServiceCollection NewHostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<CacheOptions>();

        // The store composition registers the in-process cache/invalidator first, as in Program.
        services.AddMetadataV2GraphSnapshotCache();
        return services;
    }
}
