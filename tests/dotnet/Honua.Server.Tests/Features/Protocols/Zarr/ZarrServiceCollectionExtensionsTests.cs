// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Protocols.Zarr;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

[Protocol(TestProtocols.TestQuality)]
public sealed class ZarrServiceCollectionExtensionsTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void AddZarrServices_WithScopedMetadata_ValidatesAndIsolatesTileServices()
    {
        var services = CreateServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstService = first.ServiceProvider.GetRequiredService<IZarrTileService>();

        first.ServiceProvider.GetRequiredService<IZarrTileService>().Should().BeSameAs(firstService);
        second.ServiceProvider.GetRequiredService<IZarrTileService>().Should().NotBeSameAs(firstService);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AddZarrServices_AfterRequestScopeEnds_PreservesCatalogRegistrations()
    {
        using var provider = CreateServices().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        ZarrRegistration registration;
        using (var first = provider.CreateScope())
        {
            registration = await first.ServiceProvider.GetRequiredService<IZarrStore>()
                .RegisterAsync(new ZarrRegistrationRequest
                {
                    LayerId = 1,
                    Name = "Request scope regression",
                    Provider = CloudStorageProvider.Local,
                    Bucket = "test",
                    RootPath = "coverage.zarr"
                });
        }

        using var second = provider.CreateScope();
        var store = second.ServiceProvider.GetRequiredService<IZarrStore>();
        (await store.GetAsync(registration.Id)).Should().BeEquivalentTo(registration);
        (await store.ListByLayerAsync(registration.LayerId)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(registration);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddZarrServices_WithScopedMetadataAndAuthorization_ResolvesWithinEachRequestScope()
    {
        var services = CreateServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var firstService = firstScope.ServiceProvider.GetRequiredService<IZarrTileService>();
        firstService.Should().BeSameAs(firstScope.ServiceProvider.GetRequiredService<IZarrTileService>());
        firstService.Should().NotBeSameAs(secondScope.ServiceProvider.GetRequiredService<IZarrTileService>());
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IMetadataV2GraphProvider>());
        services.AddScoped(_ => Substitute.For<ILayerAccessAuthorizer>());
        services.AddSingleton(Substitute.For<ITileMatrixSetRegistry>());
        ZarrServiceCollectionExtensions.AddZarrServices(services);
        return services;
    }
}
