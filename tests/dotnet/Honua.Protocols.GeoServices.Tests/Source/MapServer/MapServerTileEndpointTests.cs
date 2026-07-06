// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        await invalidResponse.ShouldBeGeoServicesError(400);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_NegativeZoom_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/-1/0/0");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
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
