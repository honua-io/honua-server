// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Endpoint coverage for a numeric layer id published through both a raster and a vector
/// storage binding that share the same StorageLayerId (the browser-compat pattern in
/// honua-server#2759 / #2799).
/// </summary>
/// <remarks>
/// The raster binding is declared first, so it wins the deliberately one-to-one, first-wins
/// <c>ResourcesByStorageLayerId</c> index. Only the vector service enables OGC API - Maps.
/// These tests use the <b>real</b> <see cref="Honua.Core.Features.Raster.Abstractions.IRasterMapRenderer"/>
/// (the vector-aware decorator over the provider raster renderer) — they do not mock it — so
/// the handler-resolved resource identity must flow into the renderer, otherwise the renderer
/// re-resolves first-wins to the raster resource, finds no geometry, and 404s. Only the
/// feature reader is stubbed so the vector Skia path renders deterministically without seeded
/// geometry.
/// </remarks>
[Collection("Database.OgcApiTiles")]
[Protocol(TestProtocols.OgcApiMaps)]
public sealed class OgcMapsDuplicateStorageBindingEndpointTests : IAsyncLifetime
{
    private const int LayerId = 2000;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly WebAppFixture _fixture;

    public OgcMapsDuplicateStorageBindingEndpointTests()
    {
        var graphProvider = BuildDuplicateBindingGraph();

        // Stub only the feature reader: the vector fallback renders a (blank) Skia canvas
        // from zero features, which is enough to prove the renderer reached the styled-vector
        // path for the colliding layer instead of 404ing on the raster resource.
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.QueryAsync(
                Arg.Any<int>(),
                Arg.Any<FeatureQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new QueryResult<Feature>
            {
                TotalCount = 0,
                HasMoreResults = false,
                Items = ImmutableArray<Feature>.Empty
            }));

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.PostConfigure<GeocodingConfiguration>(options =>
                    options.Providers.Nominatim = options.Providers.Nominatim with { Enabled = false });
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
                services.RemoveAll<IFeatureReader>();
                services.AddSingleton(featureReader);
            });
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_DuplicateStorageLayerBindings_RealRenderer_ReturnsPng()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{LayerId}/map" +
            "?bbox=-122.44,37.76,-122.40,37.79&width=256&height=256&f=png");
        var responseBytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            System.Text.Encoding.UTF8.GetString(responseBytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        responseBytes.Should().StartWith(PngSignature);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    public async Task GetMapTileSets_DuplicateStorageLayerBindings_ReturnsTileSets()
    {
        // The sibling map-tiles endpoint must resolve the same Maps-enabled (vector) resource
        // as the map endpoint; before #2799 the tile-set handler's first-wins lookup 404'd
        // while the map endpoint returned 200 (a capabilities/runtime consistency violation).
        using var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{LayerId}/map/tiles");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("tilesets");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_DuplicateStorageLayerBindings_UnfilteredIncludesCollidingCollection_ReturnsPng()
    {
        // The unfiltered dataset-map path enumerates storage layer ids; before #2799 it read
        // the first-wins index directly and excluded the colliding Maps-enabled collection,
        // so with layer 2000 as the only collection it 404'd. It must now include it.
        using var response = await _fixture.Client.GetAsync(
            "/ogc/maps/map?bbox=-122.44,37.76,-122.40,37.79&width=256&height=256&f=png");
        var responseBytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            System.Text.Encoding.UTF8.GetString(responseBytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        responseBytes.Should().StartWith(PngSignature);
    }

    [IntegrationTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/collections")]
    public async Task GetCollections_DuplicateLocalIds_UsesResolvableIdsAndExcludesDeniedResources(bool denyFirst, bool collideWithName, bool collideWithPublicationId)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("first-resource", collideWithName ? "0" : "First", MetadataV2ResourceType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = !denyFirst })
            .AddResource("second-resource", "Second", MetadataV2ResourceType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("first-binding", "first-resource", "public.features", storageLayerId: 4000)
            .AddStorageBinding("second-binding", "second-resource", "public.features", storageLayerId: 4001)
            .AddService("first-service", "first", protocols: ["OGC-API-Maps"])
            .AddService("second-service", "second", protocols: ["OGC-API-Maps"])
            .AddPublication("first-publication", "first-service", "first-resource",
                serviceLocalId: collideWithName ? "first" : "0", storageBindingId: "first-binding")
            .AddPublication("second-publication", "second-service", "second-resource",
                serviceLocalId: "0", storageBindingId: "second-binding")
            .Build();
        if (collideWithPublicationId)
        {
            graph = graph with
            {
                Resources = graph.Resources.Select(resource => resource.Metadata.Id == "first-resource"
                    ? resource with { Metadata = resource.Metadata with { Title = "second-publication" } }
                    : resource).ToArray()
            };
        }

        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new MetadataV2GraphSnapshot(graph, "\"discovery-test\"", DateTimeOffset.UtcNow));
        await using var fixture = new WebAppFixture().ConfigureWebHost(builder =>
            builder.UseSetting("HONUA_DEV_AUTH", "false")).ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
        });
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        graphProvider.ClearReceivedCalls();
        using var response = await client.GetAsync("/ogc/maps/collections");
        await graphProvider.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var collections = json.RootElement.GetProperty("collections").EnumerateArray().ToArray();
        collections.Should().HaveCount(denyFirst ? 1 : 2);
        foreach (var collection in collections)
        {
            var id = collection.GetProperty("id").GetString();
            id.Should().NotBe("0");
            graphProvider.ClearReceivedCalls();
            using var detail = await client.GetAsync($"/ogc/maps/collections/{id}?f=json");
            detail.StatusCode.Should().Be(HttpStatusCode.OK);
            await graphProvider.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            detailJson.RootElement.GetProperty("id").GetString().Should().Be(id);
            detailJson.RootElement.GetProperty("links").EnumerateArray()
                .Single(link => link.GetProperty("rel").GetString() == "self")
                .GetProperty("href").GetString().Should().EndWith("?f=json");
            detailJson.RootElement.GetProperty("title").GetString().Should().Be(collection.GetProperty("title").GetString());
        }
        if (denyFirst)
        {
            collections[0].GetProperty("id").GetString().Should().Be("second-publication");
        }
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static TestMetadataV2GraphProvider BuildDuplicateBindingGraph()
    {
        var publicPolicy = new AccessPolicy { AllowAnonymous = true };
        var graph = new TestMetadataV2GraphBuilder()
            // Match the compatibility snapshot's ordering: the raster binding is first and
            // therefore wins the intentionally one-to-one storage-layer index.
            .AddResource("raster-resource", "Browser Points imagery", MetadataV2ResourceType.RasterDataset,
                accessPolicy: publicPolicy)
            .AddResource("vector-resource", "Browser Points", MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry, Nullable = false }
                ],
                accessPolicy: publicPolicy)
            .AddStorageBinding("storage-image-layer-2000", "raster-resource", "honua.raster_data",
                storageLayerId: LayerId)
            .AddStorageBinding("storage-layer-2000", "vector-resource", "public.features",
                storageLayerId: LayerId)
            .AddService("image-service", "browser_compat", protocols: ["ImageServer"])
            .AddService("maps-service", "browser_compat", protocols: ["OGC-API-Maps", "OGC-API-Tiles"])
            .AddPublication(
                "image-publication",
                "image-service",
                "raster-resource",
                layerIndex: LayerId,
                storageBindingId: "storage-image-layer-2000",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .AddPublication(
                "maps-publication",
                "maps-service",
                "vector-resource",
                layerIndex: LayerId,
                storageBindingId: "storage-layer-2000",
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .Build();

        return new TestMetadataV2GraphProvider(graph);
    }
}
