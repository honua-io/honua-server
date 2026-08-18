// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Startup;

public sealed class ImportExportTileOperationsRegistrationTests
{
    [UnitTest]
    public void AddTileOperations_WithRedisAndEvictionDisabled_RegistersLiveKeyIndex()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TileOptions:Eviction:Enabled"] = "false",
            })
            .Build();

        services.AddHonuaImportExportAndTileOperations(configuration);

        var descriptor = services.Last(service => service.ServiceType == typeof(ITileCacheKeyIndex));
        descriptor.ImplementationFactory.Should().NotBeNull();
        descriptor.ImplementationInstance.Should().NotBe(NullTileCacheKeyIndex.Instance);
    }

    [UnitTest]
    public void AddTileOperations_WithoutRedis_RegistersDisabledKeyIndex()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddHonuaImportExportAndTileOperations(new ConfigurationBuilder().Build());

        var descriptor = services.Last(service => service.ServiceType == typeof(ITileCacheKeyIndex));
        descriptor.ImplementationInstance.Should().BeSameAs(NullTileCacheKeyIndex.Instance);
    }
}
