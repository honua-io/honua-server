// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IMetadataV2GraphProvider>());
        services.AddScoped(_ => Substitute.For<ILayerAccessAuthorizer>());
        services.AddSingleton(Substitute.For<ITileMatrixSetRegistry>());
        services.AddZarrServices();

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
}
