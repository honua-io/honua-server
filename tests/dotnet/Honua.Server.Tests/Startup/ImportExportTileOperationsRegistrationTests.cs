// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
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

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddTileOperations_WithScopedMetadataGraphProvider_ContainerValidates(bool jobSubstrateConfigured)
    {
        var services = BuildProductionShapedServices(jobSubstrateConfigured);

        services.Should().Contain(
            descriptor => descriptor.ServiceType == typeof(ITileCacheJobService),
            "tile operations always run as execution jobs, so the job service is always composed");

        ValidateContainer(services);
    }

    /// <summary>
    /// Composes the tile-operations module over production service lifetimes: Postgres registers
    /// <see cref="IMetadataV2GraphProvider"/> as <em>scoped</em>
    /// (<c>src/Honua.Db/Postgres/ServiceCollectionExtensions.cs</c>), unlike the singleton the
    /// file-backed <c>AddFileMetadataV2Graph</c> dev/test path registers.
    /// </summary>
    private static ServiceCollection BuildProductionShapedServices(bool jobSubstrateConfigured)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment
        {
            EnvironmentName = Environments.Production,
            ApplicationName = "Honua.Server",
            ContentRootPath = AppContext.BaseDirectory,
        });
        services.AddScoped(_ => Substitute.For<IMetadataV2GraphProvider>());
        // Layer publication is scoped in the Postgres provider, and tenant identity is scoped
        // to the request. Preserve those lifetimes when validating the shared job module.
        services.AddScoped(_ => Substitute.For<ILayerPublishingService>());
        services.AddScoped(_ => Substitute.For<ITenantContext>());
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());

        if (jobSubstrateConfigured)
        {
            services.AddSingleton(Substitute.For<IExecutionJobStore>());
            services.AddSingleton(Substitute.For<IJobQueue>());
        }

        services.AddHonuaImportExportAndTileOperations(configuration);
        return services;
    }

    /// <summary>
    /// Builds the container the way a Development-environment host does. This must stay
    /// <c>ValidateOnBuild</c>-based: a lazy resolve only covers the services a test happens to ask
    /// for, while <c>ValidateOnBuild</c> walks every descriptor in the module and turns a
    /// three-minute e2e boot timeout into a fast unit failure.
    /// </summary>
    private static void ValidateContainer(IServiceCollection services)
    {
        var build = () =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<ILayerPublishJobs>()
                .Should().BeOfType<LayerPublishJobService>();
        };

        build.Should().NotThrow(
            "no singleton in the tile-operations module may capture a scoped service; "
            + "the host fails to boot with ValidateOnBuild enabled when one does");
    }
}
