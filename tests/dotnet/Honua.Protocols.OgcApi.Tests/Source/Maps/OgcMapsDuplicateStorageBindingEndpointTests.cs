// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
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
/// Endpoint coverage for a layer published through vector and raster storage bindings.
/// </summary>
[Collection("Database.OgcApiTiles")]
[Protocol(TestProtocols.OgcApiMaps)]
public sealed class OgcMapsDuplicateStorageBindingEndpointTests : IAsyncLifetime
{
    private const int LayerId = 2000;
    private static readonly byte[] PngBytes = [137, 80, 78, 71, 13, 10, 26, 10, 0];
    private readonly WebAppFixture _fixture;

    public OgcMapsDuplicateStorageBindingEndpointTests()
    {
        var graphProvider = BuildDuplicateBindingGraph();
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderCollectionMapAsync(
                LayerId,
                Arg.Any<MapRenderRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RasterResult
            {
                Data = PngBytes,
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 4326
            }));

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.PostConfigure<GeocodingConfiguration>(options =>
                    options.Providers.Nominatim = options.Providers.Nominatim with { Enabled = false });
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
                services.RemoveAll<IRasterMapRenderer>();
                services.AddSingleton(renderer);
            });
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_DuplicateStorageLayerBindings_ReturnsPng()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{LayerId}/map" +
            "?bbox=-122.44,37.76,-122.40,37.79&width=256&height=256&f=png");
        var responseBytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            System.Text.Encoding.UTF8.GetString(responseBytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        responseBytes.Should().StartWith(PngBytes);
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
                accessPolicy: publicPolicy)
            .AddStorageBinding("storage-image-layer-2000", "raster-resource", "honua.raster_data",
                storageLayerId: LayerId)
            .AddStorageBinding("storage-layer-2000", "vector-resource", "public.features",
                storageLayerId: LayerId)
            .AddService("image-service", "browser_compat", protocols: ["ImageServer"])
            .AddService("maps-service", "browser_compat", protocols: ["OGC-API-Maps"])
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
