// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Protocols.Zarr;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

[Protocol(TestProtocols.TestQuality)]
public sealed class ZarrTileServiceScopeTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task HandleAsync_AcrossRequestScopes_UsesEachRequestsMetadataProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var snapshot = await new TestMetadataV2GraphBuilder().BuildProvider().GetCurrentAsync();
        services.AddScoped(_ =>
        {
            var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
            graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
            return graphProvider;
        });
        services.AddSingleton(Substitute.For<ILayerAccessAuthorizer>());
        services.AddSingleton(Substitute.For<ITileMatrixSetRegistry>());
        services.AddZarrServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var firstService = firstScope.ServiceProvider.GetRequiredService<IZarrTileService>();
        var secondService = secondScope.ServiceProvider.GetRequiredService<IZarrTileService>();
        var firstGraph = firstScope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var secondGraph = secondScope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        firstGraph.Should().NotBeSameAs(secondGraph);

        await firstService.HandleAsync(new DefaultHttpContext(), 0, "WebMercatorQuad", 0, 0, 0, CancellationToken.None);
        await firstGraph.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
        await secondGraph.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());

        await secondService.HandleAsync(new DefaultHttpContext(), 0, "WebMercatorQuad", 0, 0, 0, CancellationToken.None);
        await firstGraph.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
        await secondGraph.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
    }
}
