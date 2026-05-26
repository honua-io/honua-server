// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class OutputCacheInvalidationServiceTests
{
    private static string ScopedKey(string key) => CacheScopeKeys.EnsureScoped(key, null);

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_RemovesMetadataKeysWithoutWildcardScans()
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
        await outputCacheStore.Received().EvictByTagAsync("service-metadata", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("tiles", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("layer-styles", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("ogc-maps", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("stac-metadata", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("terrain", Arg.Any<CancellationToken>());

        await metadataCache.Received().RemoveAsync(ScopedKey("services:all"), Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync(ScopedKey("layers:all"), Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync(ScopedKey("service:testservice"), Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync(ScopedKey("service:exists:testservice"), Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync(ScopedKey("layer:1"), Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync(ScopedKey("layer:2"), Arg.Any<CancellationToken>());
        await metadataCache.DidNotReceive().RemoveByPatternAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

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
    public async Task InvalidateServiceCatalogAsync_BlanketEviction_RemovesQueryResponsePatterns()
    {
        var responseCache = Substitute.For<IResponseCache>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(null, responseCache, null, scopeFactory, null, logger);

        await sut.InvalidateServiceCatalogAsync(null, null, CancellationToken.None);

        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:odata:layer:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateLayerAsync_WithServiceId_EvictsQueryResponseCache()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, null, scopeFactory, null, logger);

        await sut.InvalidateLayerAsync("TestService", 1, CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("service-metadata", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("tiles", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("terrain", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:testservice:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:testservice:layer:1:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateLayerAsync_WithoutServiceId_EvictsLayerQueryResponseCaches()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, null, scopeFactory, null, logger);

        await sut.InvalidateLayerAsync(null, 5, CancellationToken.None);

        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:*:layer:5:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:odata:layer:5:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:5:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateLayerAsync_WithNamedCollection_EvictsOgcNameQueryResponseCache()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(CreateNamedLayerGraphProvider());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, null, scopeFactory, null, logger);

        await sut.InvalidateLayerAsync(null, 42, CancellationToken.None);

        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:42:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:named_layer:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateCollectionAsync_WithNamedCollection_EvictsNumericAndNameAliases()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(CreateNamedLayerGraphProvider());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, null, scopeFactory, null, logger);

        await sut.InvalidateCollectionAsync("Named Layer", CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("terrain", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:42:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:named_layer:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_LayerIdsWithoutServiceId_EvictsLayerQueryResponseCaches()
    {
        var responseCache = Substitute.For<IResponseCache>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(null, responseCache, null, scopeFactory, null, logger);

        await sut.InvalidateServiceCatalogAsync(null, [1, 2], CancellationToken.None);

        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:*:layer:1:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:*:layer:2:*", Arg.Any<CancellationToken>());
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
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(CreateOwningServicesGraphProvider());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, metadataCache, scopeFactory, null, logger);

        await sut.InvalidateLayerAsync(null, 7, CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("service:alpha", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("service:beta", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("service-metadata", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("tiles", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("ogc-maps", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:alpha:layer:7:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:alpha:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:beta:layer:7:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:beta:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateSceneAsync_WithSceneId_EvictsBroadAndPerSceneTags()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, null, null, scopeFactory, null, logger);

        await sut.InvalidateSceneAsync("Downtown", CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("scene", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("scene:downtown", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateSceneAsync_WithoutSceneId_OnlyEvictsBroadTag()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, null, null, scopeFactory, null, logger);

        await sut.InvalidateSceneAsync(null, CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("scene", Arg.Any<CancellationToken>());
        await outputCacheStore.DidNotReceive().EvictByTagAsync(
            Arg.Is<string>(t => t.StartsWith("scene:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private static TestMetadataV2GraphProvider CreateNamedLayerGraphProvider()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-named-layer", "Named Layer", MetadataV2ResourceType.FeatureDataset)
            .AddService("svc-ogc", "ogc", protocols: [ServiceProtocols.OgcFeatures])
            .AddPublication(
                "pub-named-layer",
                "svc-ogc",
                "res-named-layer",
                layerIndex: 42,
                serviceLocalId: "42",
                publicationType: MetadataV2PublicationType.OgcCollection)
            .BuildProvider();

    private static TestMetadataV2GraphProvider CreateOwningServicesGraphProvider()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-layer-7", "Layer 7", MetadataV2ResourceType.FeatureDataset)
            .AddResource("res-layer-9", "Layer 9", MetadataV2ResourceType.FeatureDataset)
            .AddService("svc-alpha", "alpha", protocols: [ServiceProtocols.FeatureServer])
            .AddService("svc-beta", "beta", protocols: [ServiceProtocols.FeatureServer])
            .AddService("svc-gamma", "gamma", protocols: [ServiceProtocols.FeatureServer])
            .AddPublication("pub-alpha-layer-7", "svc-alpha", "res-layer-7", layerIndex: 7, serviceLocalId: "7")
            .AddPublication("pub-beta-layer-7", "svc-beta", "res-layer-7", layerIndex: 7, serviceLocalId: "7")
            .AddPublication("pub-beta-layer-9", "svc-beta", "res-layer-9", layerIndex: 9, serviceLocalId: "9")
            .AddPublication("pub-gamma-layer-9", "svc-gamma", "res-layer-9", layerIndex: 9, serviceLocalId: "9")
            .BuildProvider();

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
