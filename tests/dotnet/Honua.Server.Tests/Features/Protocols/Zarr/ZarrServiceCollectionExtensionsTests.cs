// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Protocols.Zarr;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

public sealed class ZarrServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddZarrServices_WithScopedMetadataProvider_ContainerValidates()
    {
        var services = CreateServices();

        // Match the Development Docker host and the Postgres metadata lifetime.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [UnitTest]
    public void AddZarrServices_AcrossRequestScopes_IsolatesTileServicesAndSharesCatalog()
    {
        using var provider = CreateServices().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var tileService = first.ServiceProvider.GetRequiredService<IZarrTileService>();
        tileService.Should().BeSameAs(first.ServiceProvider.GetRequiredService<IZarrTileService>());
        tileService.Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<IZarrTileService>());
        first.ServiceProvider.GetRequiredService<IZarrStore>().Should()
            .BeSameAs(second.ServiceProvider.GetRequiredService<IZarrStore>());
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(Substitute.For<ILayerAccessAuthorizer>());
        services.AddSingleton(Substitute.For<ITileMatrixSetRegistry>());
        services.AddZarrServices();
        return services;
    }
}
