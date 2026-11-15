// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

public sealed class MemoryCacheRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("unreachable.invalid:6379,connectTimeout=1")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureCaching_WithLegacyRedisConfiguration_ReadsAndRemovesLocalEntries(string? connectionString)
    {
        await using var provider = BuildProvider(connectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetStringAsync("response", "cached response");
        (await cache.GetStringAsync("response")).Should().Be("cached response");

        await cache.RemoveAsync("response");
        (await cache.GetStringAsync("response")).Should().BeNull();
    }

    [UnitTest]
    public async Task ConfigureCaching_WithSeparateHosts_KeepsEntriesLocalToEachHost()
    {
        await using var firstHost = BuildProvider("unreachable.invalid:6379");
        await using var secondHost = BuildProvider("unreachable.invalid:6379");
        var firstCache = firstHost.GetRequiredService<IDistributedCache>();
        var secondCache = secondHost.GetRequiredService<IDistributedCache>();

        await firstCache.SetStringAsync("response", "first host");

        (await secondCache.GetStringAsync("response")).Should().BeNull();
        await secondCache.SetStringAsync("response", "second host");
        (await firstCache.GetStringAsync("response")).Should().Be("first host");
    }

    private static ServiceProvider BuildProvider(string? connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = connectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        InfrastructureCompositionRoot.ConfigureCaching(services, configuration);
        return services.BuildServiceProvider();
    }
}
