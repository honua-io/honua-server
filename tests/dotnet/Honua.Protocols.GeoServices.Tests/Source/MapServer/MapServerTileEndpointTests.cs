// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.MapServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Protocol(TestProtocols.MapServer)]
[Collection("Database")]
public sealed class MapServerTileEndpointTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public MapServerTileEndpointTests(WebAppFixture fixture) => _fixture = fixture;

    [UnitTest]
    public async Task ResolveTileLayerDescriptors_DraftStorageBinding_ExcludesPublication()
    {
        const string serviceId = "svc-binding-lifecycle";
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-binding-lifecycle", "Binding lifecycle")
            .AddStorageBinding(
                "binding-lifecycle",
                "resource-binding-lifecycle",
                "features",
                storageLayerId: 17)
            .AddService(serviceId, "Binding lifecycle", protocols: [ServiceProtocols.MapServer])
            .AddPublication(
                "publication-binding-lifecycle",
                serviceId,
                "resource-binding-lifecycle",
                layerIndex: 0,
                storageBindingId: "binding-lifecycle",
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .Build();
        graph = graph with
        {
            StorageBindings = graph.StorageBindings
                .Select(binding => binding with
                {
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft },
                })
                .ToArray(),
        };
        var snapshot = await new TestMetadataV2GraphProvider(graph).GetCurrentAsync();

        var layers = MapServerEndpoints.ResolveTileLayerDescriptors(
            snapshot,
            snapshot.Index.ServicesById[serviceId]);

        layers.Should().BeEmpty();
    }

    [UnitTest]
    public async Task IsTileLayerVisibleAtScale_AdvertisedMinScale_GatesOverviewTile()
    {
        const string serviceId = "svc-tile-scale";
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-tile-scale", "Scale gated")
            .AddStorageBinding("binding-tile-scale", "resource-tile-scale", "features", storageLayerId: 17)
            .AddService(serviceId, "Scale gated", protocols: [ServiceProtocols.MapServer])
            .AddPublication(
                "publication-tile-scale",
                serviceId,
                "resource-tile-scale",
                layerIndex: 0,
                storageBindingId: "binding-tile-scale",
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .Build();
        graph = graph with
        {
            Resources = graph.Resources.Select(resource => resource with
            {
                Display = new MetadataV2ResourceDisplay { MinScale = 50_000 }
            }).ToArray()
        };
        var snapshot = await new TestMetadataV2GraphProvider(graph).GetCurrentAsync();
        var layer = MapServerEndpoints.ResolveTileLayerDescriptors(
            snapshot,
            snapshot.Index.ServicesById[serviceId]).Should().ContainSingle().Subject;

        MapServerEndpoints.IsTileLayerVisibleAtScale(layer.Resource, z: 2).Should().BeFalse();
        MapServerEndpoints.IsTileLayerVisibleAtScale(layer.Resource, z: 15).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_ValidCoordinates_ReturnsPngImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/0/0/0");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_WhenCloudCacheHit_ReturnsStoredTile()
    {
        var cachedTile = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xCA, 0xFE };
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(CloudStorageProvider.AwsS3);
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new CloudFile
            {
                FileId = call.ArgAt<string>(0),
                FileName = "0-0-0.png",
                StoragePath = call.ArgAt<string>(0),
                ContentType = "image/png",
                SizeBytes = cachedTile.Length,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3
            });
        storage.DownloadBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedTile);

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<ICloudFileStorage>();
                services.AddSingleton(storage);
            });

        try
        {
            await fixture.InitializeAsync();

            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/0/0/0");
            var content = await response.Content.ReadAsByteArrayAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            content.Should().Equal(cachedTile);
            await storage.Received(1).DownloadBytesAsync(
                Arg.Is<string>(key => key.Contains("mapserver/tiles", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_HighZoom_ReturnsPngImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/5/10/15");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_ZoomAboveConfiguredMaximum_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/23/0/0");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_InvalidCoordinates_ReturnsBadRequest()
    {
        // z=2 means max tile index is 3 (2^2 - 1), so x=10 is out of range
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/2/10/10");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_AfterCachedValidRequest_InvalidCoordinatesStillReturnBadRequest()
    {
        var warmResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/0/0/0");
        warmResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // z=2 means max tile index is 3 (2^2 - 1), so x=10 is out of range.
        var invalidResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/2/10/10");

        await invalidResponse.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_NegativeZoom_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/-1/0/0");

        await response.AssertGeoServicesErrorAsync(400, 404);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_InvalidServiceId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/%20/MapServer/tile/0/0/0");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
