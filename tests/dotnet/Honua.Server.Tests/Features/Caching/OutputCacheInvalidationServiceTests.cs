// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ServiceProtocols = Honua.Core.Features.Catalog.Domain.ServiceProtocols;

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
        var scopeFactory = CreateScopeFactory(CreateFeatureServerGraph(("alpha", [42])));
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
        var scopeFactory = CreateScopeFactory(CreateFeatureServerGraph(("alpha", [42])));
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
        var scopeFactory = CreateScopeFactory(CreateFeatureServerGraph(
            ("alpha", [7]),
            ("beta", [7, 9]),
            ("gamma", [9])));
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

    private static IServiceScopeFactory CreateScopeFactory(MetadataV2Graph graph)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestMetadataV2GraphProvider(graph));
        services.AddSingleton<IMetadataV2GraphProvider>(sp =>
            sp.GetRequiredService<TestMetadataV2GraphProvider>());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static MetadataV2Graph CreateFeatureServerGraph(
        params (string ServiceName, int[] LayerIds)[] serviceDefinitions)
    {
        var builder = new TestMetadataV2GraphBuilder();
        foreach (var layerId in serviceDefinitions
                     .SelectMany(static definition => definition.LayerIds)
                     .Distinct()
                     .OrderBy(static layerId => layerId))
        {
            var resourceId = $"res-layer-{layerId}";
            var bindingId = $"binding-layer-{layerId}";
            builder
                .AddResource(
                    resourceId,
                    layerId == 42 ? "Named Layer" : $"Layer {layerId}",
                    MetadataV2ResourceType.FeatureDataset,
                    spatial: new MetadataV2ResourceSpatial
                    {
                        SpatialReference = MetadataV2SpatialReference.Wgs84,
                        GeometryType = MetadataV2GeometryType.Point,
                        PrimaryGeometryField = "shape",
                        SupportedCrs = [MetadataV2SpatialReference.Wgs84],
                    })
                .AddStorageBinding(
                    bindingId,
                    resourceId,
                    $"features:{layerId}",
                    storageLayerId: layerId);
        }

        foreach (var (serviceName, layerIds) in serviceDefinitions)
        {
            var serviceId = $"svc-{serviceName.ToLowerInvariant()}";
            builder.AddService(
                serviceId,
                serviceName,
                protocols: [ServiceProtocols.FeatureServer]);

            foreach (var layerId in layerIds)
            {
                builder.AddPublication(
                    id: $"pub-{serviceName.ToLowerInvariant()}-{layerId}",
                    serviceId: serviceId,
                    resourceId: $"res-layer-{layerId}",
                    layerIndex: layerId,
                    serviceLocalId: layerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            }
        }

        return builder.Build();
    }
}
