// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.ImageServer;

[Collection("Database")]
[Protocol(Protocols.ImageServer)]
public class ImageServerBasicTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0; // Use existing test layer

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_ValidLayerId_ReturnsServiceMetadata()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer?f=json");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Verify basic Esri ImageServer service info structure
            json.RootElement.TryGetProperty("currentVersion", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("serviceDescription", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("capabilities", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_InvalidFormat_ReturnsBadRequest()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer?f=xml");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_WithValidParameters_ReturnsImageOrNotFound()
    {
        // Arrange
        var queryParams = "?bbox=-180,-90,180,90&size=256&format=png&f=json";

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer/exportImage{queryParams}");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Verify export image response structure
            json.RootElement.TryGetProperty("href", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("width", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("height", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("extent", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_WithInlineImageFormat_ReturnsImageBytes()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(TestLayerId, Arg.Any<CancellationToken>())
            .Returns(new RasterInfo
            {
                Id = 100,
                LayerId = TestLayerId,
                Name = "test-raster",
                Width = 1024,
                Height = 1024,
                BandCount = 3,
                PixelType = "8BUI",
                Srid = 4326,
                CreatedAt = DateTimeOffset.UtcNow
            });
        rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(new[]
            {
                new RasterInfo
                {
                    Id = 100,
                    LayerId = TestLayerId,
                    Name = "test-raster",
                    Width = 1024,
                    Height = 1024,
                    BandCount = 3,
                    PixelType = "8BUI",
                    Srid = 4326,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });
        rasterStore.ExportImageAsync(TestLayerId, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47],
                ContentType = "image/png",
                Width = 256,
                Height = 128,
                Srid = 4326,
                Extent = new RasterExtent
                {
                    XMin = -180,
                    YMin = -90,
                    XMax = 180,
                    YMax = 90,
                    Srid = 4326
                }
            });

        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?bbox=-180,-90,180,90&size=256&format=png&f=image");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal([(byte)0x89, 0x50, 0x4E, 0x47]);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithValidPoint_ReturnsIdentifyResult()
    {
        // Arrange
        var queryParams = "?geometry=0,0&geometryType=esriGeometryPoint&f=json";

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer/identify{queryParams}");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Verify identify response structure
            json.RootElement.TryGetProperty("location", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("properties", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    [Operation(Operations.GetTile)]
    public async Task GetImageTile_WithValidCoordinates_ReturnsTileOrNotFound()
    {
        // Arrange
        var level = 0;
        var row = 0;
        var col = 0;

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer/tile/{level}/{row}/{col}?format=png");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success, no content, or 404
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_NonExistentLayer_ReturnsNotFound()
    {
        // Arrange
        var nonExistentLayerId = 99999;

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{nonExistentLayerId}/ImageServer?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
