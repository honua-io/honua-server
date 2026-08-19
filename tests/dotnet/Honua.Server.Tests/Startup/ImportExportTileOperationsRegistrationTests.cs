// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Tiles;
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

    /// <summary>
    /// Regression guard for the release-train boot crash in honua-release run 32279881347
    /// (gate_e2e / slice1): every e2e driver reported BLOCKED because the server never came up with
    /// <c>Cannot consume scoped service &apos;IMetadataV2GraphProvider&apos; from singleton
    /// &apos;ITileCacheJobService&apos;</c>.
    /// <para>
    /// The failure is CONFIGURATION-GATED, which is exactly why nothing caught it. Three things have
    /// to line up: (1) the host validates the container — ASP.NET only turns on
    /// <c>ValidateOnBuild</c>/<c>ValidateScopes</c> in the Development environment, which the e2e
    /// harness compose sets; (2) <see cref="IConnectionMultiplexer"/> is in the collection, which is
    /// what gates <c>ITileCacheJobService</c> in at all, and which in turn needs a reachable
    /// <c>ConnectionStrings:Redis</c> AND a <c>caching.redis</c> entitlement
    /// (<c>Licensing:DevGrantEdition=Pro/Enterprise</c>); and (3) the metadata graph provider is the
    /// scoped Postgres one rather than the singleton the file-backed dev/test path registers. Drop
    /// any one of the three — as the honua-esri-compat stack does by leaving Redis behind an opt-in
    /// compose profile — and the identical image boots clean.
    /// </para>
    /// <para>
    /// So this test pins the crashing combination, not the default one: a container-validation test
    /// against the default configuration passes even with the bug present.
    /// </para>
    /// </summary>
    [UnitTest]
    public void AddTileOperations_RedisEntitledWithScopedMetadataGraphProvider_ContainerValidates()
    {
        var services = BuildProductionShapedServices(redisConfigured: true);

        // Guard against the assertion below going vacuous: if the Redis gate ever stops registering
        // ITileCacheJobService, this test would "pass" while covering nothing.
        services.Should().Contain(
            descriptor => descriptor.ServiceType == typeof(ITileCacheJobService),
            "the Redis-configured shape is the one that registers the singleton under test");

        ValidateContainer(services);
    }

    /// <summary>
    /// The complementary half of the configuration matrix: without Redis the tile-cache job service
    /// is never registered, and the module must still validate cleanly. Keeping both permutations
    /// here documents that the Redis gate — not the metadata provider lifetime — is what decides
    /// whether the offending descriptor exists.
    /// </summary>
    [UnitTest]
    public void AddTileOperations_WithoutRedisAndScopedMetadataGraphProvider_ContainerValidates()
    {
        var services = BuildProductionShapedServices(redisConfigured: false);

        services.Should().NotContain(
            descriptor => descriptor.ServiceType == typeof(ITileCacheJobService),
            "the batch submission service is gated on the Redis-backed execution-job substrate");

        ValidateContainer(services);
    }

    /// <summary>
    /// Composes the tile-operations module over production service lifetimes: Postgres registers
    /// <see cref="IMetadataV2GraphProvider"/> as <em>scoped</em>
    /// (<c>src/Honua.Db/Postgres/ServiceCollectionExtensions.cs</c>), unlike the singleton the
    /// file-backed <c>AddFileMetadataV2Graph</c> dev/test path registers.
    /// </summary>
    private static ServiceCollection BuildProductionShapedServices(bool redisConfigured)
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

        if (redisConfigured)
        {
            services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

            // Durable execution-job substrate, contributed alongside Redis by AddGeoprocessing.
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
        var build = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        build.Should().NotThrow(
            "no singleton in the tile-operations module may capture a scoped service; "
            + "the host fails to boot with ValidateOnBuild enabled when one does");
    }
}
