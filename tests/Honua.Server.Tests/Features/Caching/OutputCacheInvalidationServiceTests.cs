// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(Protocols.TestQuality)]
public sealed class OutputCacheInvalidationServiceTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_RemovesMetadataKeysAndPatterns()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var metadataCache = Substitute.For<ICacheService>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, metadataCache, scopeFactory, null, logger);

        await sut.InvalidateServiceCatalogAsync("TestService", [1, 2, 2], CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("service-directory", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("service:testservice", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("layer:1", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("layer:2", Arg.Any<CancellationToken>());

        await metadataCache.Received().RemoveAsync("services:all", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("layers:all", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("service:testservice", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("service:exists:testservice", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("layer:1", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("layer:2", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("relationship:1:*", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("relationship:2:*", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:services:all", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:layers:all", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:service:testservice", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:service:exists:testservice", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:layer:1", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:layer:2", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:relationship:1:*", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("scope:*:relationship:2:*", Arg.Any<CancellationToken>());

        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:testservice:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:testservice:layer:1:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:testservice:layer:2:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:odata:layer:1:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:1:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_WithoutLayerIds_RemovesServiceWidePattern()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var metadataCache = Substitute.For<ICacheService>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, metadataCache, scopeFactory, null, logger);

        await sut.InvalidateServiceCatalogAsync("TestService", null, CancellationToken.None);

        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:testservice:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_WithNullCaches_DoesNotThrow()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(null, null, null, scopeFactory, null, logger);

        var act = async () => await sut.InvalidateServiceCatalogAsync("svc", [3], CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateLayerAsync_WithoutServiceId_ResolvesOwningServices()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var metadataCache = Substitute.For<ICacheService>();
        var layerCatalog = Substitute.For<ILayerCatalog>();
        layerCatalog.ListServicesAsync(Arg.Any<CancellationToken>())
            .Returns([
                CreateService("alpha", 7),
                CreateService("beta", 7, 9),
                CreateService("gamma", 9)
            ]);

        var services = new ServiceCollection();
        services.AddScoped(_ => layerCatalog);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, metadataCache, scopeFactory, null, logger);

        await sut.InvalidateLayerAsync(null, 7, CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("service:alpha", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("service:beta", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:alpha:layer:7:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:beta:layer:7:*", Arg.Any<CancellationToken>());
    }

    private static ServiceDefinition CreateService(string name, params int[] layerIds)
    {
        var layers = layerIds
            .Select(layerId => LayerDefinition.CreateBasic(layerId, $"Layer {layerId}", GeometryType.Point))
            .ToArray();

        return new ServiceDefinition(
            name,
            $"Service {name}",
            layers,
            SpatialReference.Create(4326));
    }
}
