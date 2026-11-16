// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
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
    public async Task AddTileOperations_WithLocalCache_AdmitsProgressAtomicallyWithoutSharingAcrossHosts()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddHonuaImportExportAndTileOperations(configuration);
        await using var firstHost = services.BuildServiceProvider();
        await using var secondHost = services.BuildServiceProvider();
        var firstStore = firstHost.GetRequiredService<IUniversalProgressStore>();
        var secondStore = secondHost.GetRequiredService<IUniversalProgressStore>();
        var progress = UploadProgress.CreateInitial("local-upload", "data.bin", 100);

        (await firstStore.TryReplaceProgressAsync(new OperationProgressWrite("local-upload", progress), null))
            .Outcome.Should().Be(ProgressCompareAndSetOutcome.Updated);
        (await firstStore.TryReplaceProgressAsync(new OperationProgressWrite("local-upload", progress), null))
            .Outcome.Should().Be(ProgressCompareAndSetOutcome.StatusMismatch);
        (await firstStore.GetProgressAsync("local-upload")).Should().NotBeNull();
        (await secondStore.GetProgressAsync("local-upload")).Should().BeNull();
    }

    [UnitTest]
    public void AddTileOperations_WithoutSystemDatabase_ComposesNoSharedLeaseStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddHonuaImportExportAndTileOperations(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetService<ILeaseStore>().Should().BeNull();
    }

    [UnitTheory]
    [InlineData("durable")]
    [InlineData("local")]
    public void AddTileOperations_WithScopedMetadataGraphProvider_ContainerValidates(string substrate)
    {
        var services = BuildProductionShapedServices(substrate);

        services.Should().Contain(
            descriptor => descriptor.ServiceType == typeof(ITileCacheJobService),
            "tile operations always run as execution jobs, so the job service is always composed");

        ValidateContainer(services);
    }

    private static ServiceCollection BuildProductionShapedServices(string substrate)
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

        if (substrate == "durable")
        {
            services.AddSingleton(Substitute.For<IExecutionJobStore>());
            services.AddSingleton(Substitute.For<IJobQueue>());
        }

        services.AddHonuaImportExportAndTileOperations(configuration);
        return services;
    }

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
